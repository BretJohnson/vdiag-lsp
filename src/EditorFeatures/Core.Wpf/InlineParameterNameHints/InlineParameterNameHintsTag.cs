﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

namespace Microsoft.CodeAnalysis.Editor.InlineParameterNameHints
{
    /// <summary>
    /// This is the tag which implements the IntraTextAdornmentTag and is meant to create the UIElements that get shown
    /// in the editor
    /// </summary>
    internal class InlineParameterNameHintsTag : IntraTextAdornmentTag
    {
        public const string TagId = "inline parameter name hints";
        /// <summary>
        /// Creates the UIElement on call
        /// Uses PositionAffinity.Successor because we want the tag to be associated with the following character
        /// </summary>
        /// <param name="text">The name of the parameter associated with the argument</param>
        public InlineParameterNameHintsTag(string text, double lineHeight, TextFormattingRunProperties format)
            : base(CreateElement(text, lineHeight, format), removalCallback: null, PositionAffinity.Successor)
        {
        }

        private static UIElement CreateElement(string text, double lineHeight, TextFormattingRunProperties format)
        {
            // Constructs the hint block which gets assigned parameter name, a normal fontstyle, and sets the padding 
            // space around the block to 0
            var block = new TextBlock
            {
                Text = text + ":",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontStyle = FontStyles.Normal,
                FontFamily = format.Typeface.FontFamily, //new System.Windows.Media.FontFamily("Consolas"),
                FontSize = format.FontRenderingEmSize - 1,
                Padding = new Thickness(0),
                Background = format.BackgroundBrush,//System.Windows.Media.Brushes.Lavender,
                Foreground = format.ForegroundBrush //System.Windows.Media.Brushes.Gray
            };

            var border = new Border
            {
                CornerRadius = new CornerRadius(2),
                Background = format.BackgroundBrush,//System.Windows.Media.Brushes.Lavender,
                BorderBrush = format.BackgroundBrush, //System.Windows.Media.Brushes.Lavender,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
                Height = lineHeight - 3,
                Child = block,
            };
            block.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            border.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            return border;
            //return block;

        }
    }
}
