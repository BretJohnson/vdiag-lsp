﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Extensions.ContextQuery;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.CodeAnalysis.CSharp.Completion.KeywordRecommenders
{
    internal class InitKeywordRecommender : AbstractSyntacticSingleKeywordRecommender
    {
        public InitKeywordRecommender()
            : base(SyntaxKind.InitKeyword)
        {
        }

        protected override bool IsValidContext(int position, CSharpSyntaxContext context, CancellationToken cancellationToken)
        {
            return
                context.TargetToken.IsAccessorDeclarationContext<PropertyDeclarationSyntax>(position, SyntaxKind.InitKeyword) ||
                context.TargetToken.IsAccessorDeclarationContext<IndexerDeclarationSyntax>(position, SyntaxKind.InitKeyword);
        }
    }
}
