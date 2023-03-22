﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Diagnostics.Telemetry;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Telemetry;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Workspaces.Diagnostics;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Remote.Diagnostics
{
    internal class DiagnosticComputer
    {
        /// <summary>
        /// Cache of <see cref="CompilationWithAnalyzers"/> and a map from analyzer IDs to <see cref="DiagnosticAnalyzer"/>s
        /// for all analyzers for the last project to be analyzed.
        /// The <see cref="CompilationWithAnalyzers"/> instance is shared between all the following document analyses modes for the project:
        ///  1. Span-based analysis for active document (lightbulb)
        ///  2. Background analysis for active and open documents.
        ///  
        /// NOTE: We do not re-use this cache for project analysis as it leads to significant memory increase in the OOP process.
        /// Additionally, we only store the cache entry for the last project to be analyzed instead of maintaining a CWT keyed off
        /// each project in the solution, as the CWT does not seem to drop entries until ForceGC happens, leading to significant memory
        /// pressure when there are large number of open documents across different projects to be analyzed by background analysis.
        /// </summary>
        private static CompilationWithAnalyzersCacheEntry? s_compilationWithAnalyzersCache = null;

        /// <summary>
        /// List of high priority diagnostic computation tasks which are currently executing.
        /// Any new high priority diagnostic request is added to this list before the core diagnostics
        /// compute call is performed, and removed from this list after the computation finishes.
        /// Any new normal priority diagnostic request first waits for all the high priority tasks in this list
        /// to complete, and moves ahead only after this list becomes empty.
        /// </summary>
        /// <remarks>
        /// Read/write access to the list is guarded by <see cref="s_gate"/>.
        /// </remarks>
        private static readonly List<Task> s_highPriorityComputeTasks = new();

        /// <summary>
        /// List of cancellation token sources for normal priority diagnostic computation tasks which are currently executing.
        /// For any new normal priority diagnostic request, a new cancellation token source is created and added to this list
        /// before the core diagnostics compute call is performed, and removed from this list after the computation finishes.
        /// Any new high priority diagnostic request first fires cancellation on all the cancellation token sources in this list
        /// to avoid resource contention between normal and high priority requests.
        /// Canceled normal priority diagnostic requests are re-attempted from scratch after all the high priority requests complete.
        /// </summary>
        /// <remarks>
        /// Read/write access to the list is guarded by <see cref="s_gate"/>.
        /// </remarks>
        private static readonly List<CancellationTokenSource> s_cancellationSourcesForNormalPriorityComputeTasks = new();

        /// <summary>
        /// Static gate controlling access to following static fields:
        /// - <see cref="s_compilationWithAnalyzersCache"/>
        /// - <see cref="s_highPriorityComputeTasks"/>
        /// - <see cref="s_cancellationSourcesForNormalPriorityComputeTasks"/>
        /// </summary>
        private static readonly object s_gate = new();

        /// <summary>
        /// Solution checksum for the diagnostic request.
        /// We use this checksum and the <see cref="ProjectId"/> of the diagnostic request as the key
        /// to the <see cref="s_compilationWithAnalyzersCache"/>.
        /// </summary>
        private readonly Checksum _solutionChecksum;

        private readonly TextDocument? _document;
        private readonly Project _project;
        private readonly IdeAnalyzerOptions _ideOptions;
        private readonly TextSpan? _span;
        private readonly AnalysisKind? _analysisKind;
        private readonly IPerformanceTrackerService? _performanceTracker;
        private readonly DiagnosticAnalyzerInfoCache _analyzerInfoCache;
        private readonly HostWorkspaceServices _hostWorkspaceServices;

        private DiagnosticComputer(
            TextDocument? document,
            Project project,
            Checksum solutionChecksum,
            IdeAnalyzerOptions ideOptions,
            TextSpan? span,
            AnalysisKind? analysisKind,
            DiagnosticAnalyzerInfoCache analyzerInfoCache,
            HostWorkspaceServices hostWorkspaceServices)
        {
            _document = document;
            _project = project;
            _solutionChecksum = solutionChecksum;
            _ideOptions = ideOptions;
            _span = span;
            _analysisKind = analysisKind;
            _analyzerInfoCache = analyzerInfoCache;
            _hostWorkspaceServices = hostWorkspaceServices;
            _performanceTracker = project.Solution.Services.GetService<IPerformanceTrackerService>();
        }

        public static async Task<SerializableDiagnosticAnalysisResults> GetDiagnosticsAsync(
            TextDocument? document,
            Project project,
            Checksum solutionChecksum,
            IdeAnalyzerOptions ideOptions,
            TextSpan? span,
            IEnumerable<string> analyzerIds,
            AnalysisKind? analysisKind,
            DiagnosticAnalyzerInfoCache analyzerInfoCache,
            HostWorkspaceServices hostWorkspaceServices,
            bool highPriority,
            bool reportSuppressedDiagnostics,
            bool logPerformanceInfo,
            bool getTelemetryInfo,
            CancellationToken cancellationToken)
        {
            // PERF: Due to the concept of InFlight solution snapshots in OOP process, we might have been
            //       handed a Project instance that does not match the Project instance corresponding to our
            //       cached CompilationWithAnalyzers instance, while the underlying Solution checksum matches
            //       for our cached entry and the incoming request.
            //       We detect this case upfront here and re-use the cached CompilationWithAnalyzers and Project
            //       instance for diagnostic computation, thus improving the performance of analyzer execution.
            //       This is an important performance optimization for lightbulb diagnostic computation.
            //       See https://github.com/dotnet/roslyn/issues/66968 for details.
            lock (s_gate)
            {
                if (s_compilationWithAnalyzersCache?.SolutionChecksum == solutionChecksum &&
                    s_compilationWithAnalyzersCache.Project.Id == project.Id &&
                    s_compilationWithAnalyzersCache.Project != project)
                {
                    project = s_compilationWithAnalyzersCache.Project;
                    if (document != null)
                        document = project.GetTextDocument(document.Id);
                }
            }

            // We perform prioritized execution of diagnostic computation requests based on the
            // 'highPriority' boolean parameter.
            //   - High priority requests forces cancellation of all the executing normal priority requests,
            //     which are re-attempted once the high priority request completes.
            //   - Normal priority requests wait for all the executing high priority requests to complete
            //     before starting the compute.
            //   - Canceled normal priority requests are re-attempted in the below loop.

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Step 1:
                //  - High priority task forces cancellation of all the executing normal priority tasks
                //    to minimize resource and CPU contention with normal priority tasks.
                //  - Normal priority task waits for all the executing high priority tasks to complete.
                if (highPriority)
                {
                    CancelNormalPriorityTasks(cancellationToken);
                }
                else
                {
                    await WaitForHighPriorityTasksAsync(cancellationToken).ConfigureAwait(false);
                }

                // Step 2:
                //  - Create the core 'computeTask' for computing diagnostics.
                //  - Create a custom 'cancellationTokenSource' associated with this 'computeTask'.
                //    This token source allows normal priority computeTasks to be cancelled when
                //    a high priority diagnostic request is received.
                var (computeTask, cancellationTokenSource) = CreateComputeTaskAndCancellationSource(cancellationToken);

                // Step 3:
                //  - Start tracking the 'computeTask' and 'cancellationTokenSource' prior to invoking the computation.
                //    These are used in Step 1 if a new diagnostic request is received while this computeTask is running.
                StartTrackingPreCompute(computeTask, cancellationTokenSource, highPriority);

                try
                {
                    // Step 4:
                    //  - Execute the core 'computeTask' for diagnostic computation.
                    return await computeTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Step 5:
                    // Check if cancellation fired on the custom 'cancellationTokenSource' that was created for
                    // allowing cancellation of 'computeTask' from subsequent highPriority requests.
                    if (cancellationTokenSource.IsCancellationRequested)
                    {
                        // We expect only normal priority tasks to get forcefully cancelled
                        // by firing cancellation on our custom 'cancellationTokenSource'.
                        Debug.Assert(!highPriority);

                        // Attempt to re-execute this cancelled normal priority task
                        // by running the loop again.
                        continue;
                    }

                    // Propagate all other OperationCanceledExceptions up the stack.
                    throw;
                }
                finally
                {
                    // Step 6:
                    //  - Stop tracking the 'computeTask' and 'cancellationTokenSource' for
                    //    completed or cancelled task. For the case where the computeTask was
                    //    cancelled, we will create a new 'computeTask' and 'cancellationTokenSource'
                    //    for the retry.
                    StopTrackingPostCompute(computeTask, cancellationTokenSource, highPriority);
                    cancellationTokenSource.Dispose();
                }
            }

            throw ExceptionUtilities.Unreachable();

            (Task<SerializableDiagnosticAnalysisResults>, CancellationTokenSource) CreateComputeTaskAndCancellationSource(CancellationToken cancellationToken)
            {
                // Create a linked cancellation source to allow high priority tasks to cancel normal priority tasks.
                var cancellationTokenSource = new CancellationTokenSource();
                var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token, cancellationToken);
                cancellationToken = linkedCancellationTokenSource.Token;

                var computeTask = Task.Run(async () =>
                {
                    try
                    {
                        var diagnosticsComputer = new DiagnosticComputer(document, project,
                            solutionChecksum, ideOptions, span, analysisKind, analyzerInfoCache, hostWorkspaceServices);
                        return await diagnosticsComputer.GetDiagnosticsAsync(analyzerIds, reportSuppressedDiagnostics, logPerformanceInfo, getTelemetryInfo, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        linkedCancellationTokenSource.Dispose();
                    }
                }, cancellationToken);

                return (computeTask, cancellationTokenSource);
            }

            static void CancelNormalPriorityTasks(CancellationToken cancellationToken)
            {
                ImmutableArray<CancellationTokenSource> cancellationTokenSourcesToCancel;
                lock (s_gate)
                {
                    cancellationTokenSourcesToCancel = s_cancellationSourcesForNormalPriorityComputeTasks.ToImmutableArrayOrEmpty();
                }

                foreach (var cancellationTokenSource in cancellationTokenSourcesToCancel)
                {
                    try
                    {
                        cancellationTokenSource.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // CancellationTokenSource might get disposed if the normal priority
                        // task completes while we were executing this foreach loop.
                        // Gracefully handle this case and ignore this exception.
                    }
                }
            }

            static async Task WaitForHighPriorityTasksAsync(CancellationToken cancellationToken)
            {
                // We loop continuously until we have an empty high priority task queue.
                while (true)
                {
                    ImmutableArray<Task> highPriorityTasksToAwait;
                    lock (s_gate)
                    {
                        highPriorityTasksToAwait = s_highPriorityComputeTasks.ToImmutableArrayOrEmpty();
                    }

                    if (highPriorityTasksToAwait.IsEmpty)
                    {
                        return;
                    }

                    foreach (var task in highPriorityTasksToAwait)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            await task.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // Gracefully ignore cancellations for high priority tasks.
                        }
                    }
                }
            }

            static void StartTrackingPreCompute(Task computeTask, CancellationTokenSource tokenSource, bool highPriority)
            {
                lock (s_gate)
                {
                    if (highPriority)
                    {
                        Debug.Assert(!s_highPriorityComputeTasks.Contains(computeTask));
                        s_highPriorityComputeTasks.Add(computeTask);
                    }
                    else
                    {
                        Debug.Assert(!s_cancellationSourcesForNormalPriorityComputeTasks.Contains(tokenSource));
                        s_cancellationSourcesForNormalPriorityComputeTasks.Add(tokenSource);
                    }
                }
            }

            static void StopTrackingPostCompute(Task computeTask, CancellationTokenSource tokenSource, bool highPriority)
            {
                lock (s_gate)
                {
                    if (highPriority)
                    {
                        var removed = s_highPriorityComputeTasks.Remove(computeTask);
                        Debug.Assert(removed);
                    }
                    else
                    {
                        var removed = s_cancellationSourcesForNormalPriorityComputeTasks.Remove(tokenSource);
                        Debug.Assert(removed);
                    }
                }
            }
        }

        private async Task<SerializableDiagnosticAnalysisResults> GetDiagnosticsAsync(
            IEnumerable<string> analyzerIds,
            bool reportSuppressedDiagnostics,
            bool logPerformanceInfo,
            bool getTelemetryInfo,
            CancellationToken cancellationToken)
        {
            var (compilationWithAnalyzers, analyzerToIdMap) = await GetOrCreateCompilationWithAnalyzersAsync(cancellationToken).ConfigureAwait(false);

            var analyzers = GetAnalyzers(analyzerToIdMap, analyzerIds);
            if (analyzers.IsEmpty)
            {
                return SerializableDiagnosticAnalysisResults.Empty;
            }

            if (_document == null && analyzers.Length < compilationWithAnalyzers.Analyzers.Length)
            {
                // PERF: Generate a new CompilationWithAnalyzers with trimmed analyzers for non-document analysis case.
                compilationWithAnalyzers = compilationWithAnalyzers.Compilation.WithAnalyzers(analyzers, compilationWithAnalyzers.AnalysisOptions);
            }

            var skippedAnalyzersInfo = _project.GetSkippedAnalyzersInfo(_analyzerInfoCache);

            return await AnalyzeAsync(compilationWithAnalyzers, analyzerToIdMap, analyzers, skippedAnalyzersInfo,
                reportSuppressedDiagnostics, logPerformanceInfo, getTelemetryInfo, cancellationToken).ConfigureAwait(false);
        }

        private async Task<SerializableDiagnosticAnalysisResults> AnalyzeAsync(
            CompilationWithAnalyzers compilationWithAnalyzers,
            BidirectionalMap<string, DiagnosticAnalyzer> analyzerToIdMap,
            ImmutableArray<DiagnosticAnalyzer> analyzers,
            SkippedHostAnalyzersInfo skippedAnalyzersInfo,
            bool reportSuppressedDiagnostics,
            bool logPerformanceInfo,
            bool getTelemetryInfo,
            CancellationToken cancellationToken)
        {
            var documentAnalysisScope = _document != null
                ? new DocumentAnalysisScope(_document, _span, analyzers, _analysisKind!.Value)
                : null;

            var (analysisResult, additionalPragmaSuppressionDiagnostics) = await compilationWithAnalyzers.GetAnalysisResultAsync(
                documentAnalysisScope, _project, _analyzerInfoCache, cancellationToken).ConfigureAwait(false);

            if (logPerformanceInfo && _performanceTracker != null)
            {
                // Only log telemetry snapshot is we have an active telemetry session,
                // i.e. user has not opted out of reporting telemetry.
                var telemetryService = _hostWorkspaceServices.GetRequiredService<IWorkspaceTelemetryService>();
                if (telemetryService.HasActiveSession)
                {
                    // +1 to include project itself
                    var unitCount = 1;
                    if (documentAnalysisScope == null)
                        unitCount += _project.DocumentIds.Count;

                    _performanceTracker.AddSnapshot(analysisResult.AnalyzerTelemetryInfo.ToAnalyzerPerformanceInfo(_analyzerInfoCache), unitCount, forSpanAnalysis: _span.HasValue);
                }
            }

            var builderMap = await analysisResult.ToResultBuilderMapAsync(
                additionalPragmaSuppressionDiagnostics, documentAnalysisScope,
                _project, VersionStamp.Default, compilationWithAnalyzers.Compilation,
                analyzers, skippedAnalyzersInfo, reportSuppressedDiagnostics, cancellationToken).ConfigureAwait(false);

            var telemetry = getTelemetryInfo
                ? GetTelemetryInfo(analysisResult, analyzers, analyzerToIdMap)
                : ImmutableArray<(string analyzerId, AnalyzerTelemetryInfo)>.Empty;

            return new SerializableDiagnosticAnalysisResults(Dehydrate(builderMap, analyzerToIdMap), telemetry);
        }

        private static ImmutableArray<(string analyzerId, SerializableDiagnosticMap diagnosticMap)> Dehydrate(
            ImmutableDictionary<DiagnosticAnalyzer, DiagnosticAnalysisResultBuilder> builderMap,
            BidirectionalMap<string, DiagnosticAnalyzer> analyzerToIdMap)
        {
            using var _ = ArrayBuilder<(string analyzerId, SerializableDiagnosticMap diagnosticMap)>.GetInstance(out var diagnostics);

            foreach (var (analyzer, analyzerResults) in builderMap)
            {
                var analyzerId = GetAnalyzerId(analyzerToIdMap, analyzer);

                diagnostics.Add((analyzerId,
                    new SerializableDiagnosticMap(
                        analyzerResults.SyntaxLocals.SelectAsArray(entry => (entry.Key, entry.Value)),
                        analyzerResults.SemanticLocals.SelectAsArray(entry => (entry.Key, entry.Value)),
                        analyzerResults.NonLocals.SelectAsArray(entry => (entry.Key, entry.Value)),
                        analyzerResults.Others)));
            }

            return diagnostics.ToImmutable();
        }

        private static ImmutableArray<(string analyzerId, AnalyzerTelemetryInfo)> GetTelemetryInfo(
            AnalysisResult analysisResult,
            ImmutableArray<DiagnosticAnalyzer> analyzers,
            BidirectionalMap<string, DiagnosticAnalyzer> analyzerToIdMap)
        {
            Func<DiagnosticAnalyzer, bool> shouldInclude;
            if (analyzers.Length < analysisResult.AnalyzerTelemetryInfo.Count)
            {
                // Filter the telemetry info to the executed analyzers.
                using var _1 = PooledHashSet<DiagnosticAnalyzer>.GetInstance(out var analyzersSet);
                analyzersSet.AddRange(analyzers);

                shouldInclude = analyzer => analyzersSet.Contains(analyzer);
            }
            else
            {
                shouldInclude = _ => true;
            }

            using var _2 = ArrayBuilder<(string analyzerId, AnalyzerTelemetryInfo)>.GetInstance(out var telemetryBuilder);
            foreach (var (analyzer, analyzerTelemetry) in analysisResult.AnalyzerTelemetryInfo)
            {
                if (shouldInclude(analyzer))
                {
                    var analyzerId = GetAnalyzerId(analyzerToIdMap, analyzer);
                    telemetryBuilder.Add((analyzerId, analyzerTelemetry));
                }
            }

            return telemetryBuilder.ToImmutable();
        }

        private static string GetAnalyzerId(BidirectionalMap<string, DiagnosticAnalyzer> analyzerMap, DiagnosticAnalyzer analyzer)
        {
            var analyzerId = analyzerMap.GetKeyOrDefault(analyzer);
            Contract.ThrowIfNull(analyzerId);

            return analyzerId;
        }

        private static ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(BidirectionalMap<string, DiagnosticAnalyzer> analyzerMap, IEnumerable<string> analyzerIds)
        {
            // TODO: this probably need to be cached as well in analyzer service?
            var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();

            foreach (var analyzerId in analyzerIds)
            {
                if (analyzerMap.TryGetValue(analyzerId, out var analyzer))
                {
                    builder.Add(analyzer);
                }
            }

            return builder.ToImmutable();
        }

        private async Task<(CompilationWithAnalyzers compilationWithAnalyzers, BidirectionalMap<string, DiagnosticAnalyzer> analyzerToIdMap)> GetOrCreateCompilationWithAnalyzersAsync(CancellationToken cancellationToken)
        {
            var cacheEntry = await GetOrCreateCacheEntryAsync().ConfigureAwait(false);
            return (cacheEntry.CompilationWithAnalyzers, cacheEntry.AnalyzerToIdMap);

            async Task<CompilationWithAnalyzersCacheEntry> GetOrCreateCacheEntryAsync()
            {
                if (_document == null)
                {
                    // Only use cache for document analysis.
                    return await CreateCompilationWithAnalyzersCacheEntryAsync(cancellationToken).ConfigureAwait(false);
                }

                lock (s_gate)
                {
                    if (s_compilationWithAnalyzersCache?.SolutionChecksum == _solutionChecksum &&
                        s_compilationWithAnalyzersCache.Project == _project)
                    {
                        return s_compilationWithAnalyzersCache;
                    }
                }

                var entry = await CreateCompilationWithAnalyzersCacheEntryAsync(cancellationToken).ConfigureAwait(false);

                lock (s_gate)
                {
                    s_compilationWithAnalyzersCache = entry;
                }

                return entry;
            }
        }

        private async Task<CompilationWithAnalyzersCacheEntry> CreateCompilationWithAnalyzersCacheEntryAsync(CancellationToken cancellationToken)
        {
            // We could consider creating a service so that we don't do this repeatedly if this shows up as perf cost
            using var pooledObject = SharedPools.Default<HashSet<object>>().GetPooledObject();
            using var pooledMap = SharedPools.Default<Dictionary<string, DiagnosticAnalyzer>>().GetPooledObject();
            var referenceSet = pooledObject.Object;
            var analyzerMapBuilder = pooledMap.Object;

            // This follows what we do in DiagnosticAnalyzerInfoCache.CheckAnalyzerReferenceIdentity
            using var _ = ArrayBuilder<DiagnosticAnalyzer>.GetInstance(out var analyzerBuilder);
            foreach (var reference in _project.Solution.AnalyzerReferences.Concat(_project.AnalyzerReferences))
            {
                if (!referenceSet.Add(reference.Id))
                {
                    continue;
                }

                var analyzers = reference.GetAnalyzers(_project.Language);
                analyzerBuilder.AddRange(analyzers);
                analyzerMapBuilder.AppendAnalyzerMap(analyzers);
            }

            var compilationWithAnalyzers = await CreateCompilationWithAnalyzerAsync(analyzerBuilder.ToImmutable(), cancellationToken).ConfigureAwait(false);
            var analyzerToIdMap = new BidirectionalMap<string, DiagnosticAnalyzer>(analyzerMapBuilder);

            return new CompilationWithAnalyzersCacheEntry(_solutionChecksum, _project, compilationWithAnalyzers, analyzerToIdMap);
        }

        private async Task<CompilationWithAnalyzers> CreateCompilationWithAnalyzerAsync(ImmutableArray<DiagnosticAnalyzer> analyzers, CancellationToken cancellationToken)
        {
            // Always run analyzers concurrently in OOP
            const bool concurrentAnalysis = true;

            // Get original compilation
            var compilation = await _project.GetRequiredCompilationAsync(cancellationToken).ConfigureAwait(false);

            // Fork compilation with concurrent build. this is okay since WithAnalyzers will fork compilation
            // anyway to attach event queue. This should make compiling compilation concurrent and make things
            // faster
            compilation = compilation.WithOptions(compilation.Options.WithConcurrentBuild(concurrentAnalysis));

            // Run analyzers concurrently, with performance logging and reporting suppressed diagnostics.
            // This allows all client requests with or without performance data and/or suppressed diagnostics to be satisfied.
            // TODO: can we support analyzerExceptionFilter in remote host? 
            //       right now, host doesn't support watson, we might try to use new NonFatal watson API?
            var analyzerOptions = new CompilationWithAnalyzersOptions(
                options: new WorkspaceAnalyzerOptions(_project.AnalyzerOptions, _ideOptions),
                onAnalyzerException: null,
                analyzerExceptionFilter: null,
                concurrentAnalysis: concurrentAnalysis,
                logAnalyzerExecutionTime: true,
                reportSuppressedDiagnostics: true);

            return compilation.WithAnalyzers(analyzers, analyzerOptions);
        }

        private sealed class CompilationWithAnalyzersCacheEntry
        {
            public Checksum SolutionChecksum { get; }
            public Project Project { get; }
            public CompilationWithAnalyzers CompilationWithAnalyzers { get; }
            public BidirectionalMap<string, DiagnosticAnalyzer> AnalyzerToIdMap { get; }

            public CompilationWithAnalyzersCacheEntry(Checksum solutionChecksum, Project project, CompilationWithAnalyzers compilationWithAnalyzers, BidirectionalMap<string, DiagnosticAnalyzer> analyzerToIdMap)
            {
                SolutionChecksum = solutionChecksum;
                Project = project;
                CompilationWithAnalyzers = compilationWithAnalyzers;
                AnalyzerToIdMap = analyzerToIdMap;
            }
        }
    }
}
