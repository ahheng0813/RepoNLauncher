using System.Windows;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Size = System.Windows.Size;
using UIElement = System.Windows.UIElement;

namespace RepoNLauncher.Controls;

/// <summary>
/// A row of buttons that carries on onto another line when there isn't room for it all, instead of pushing the ones at
/// its ends out of sight as a horizontal StackPanel does on a smaller screen. Lines are <see cref="LineSpacing"/> apart
/// and lined up by <see cref="LineAlignment"/>. A child marked <c>IsSeparator</c> (a divider between groups of buttons)
/// shows only between two others on the same line, never at a line's start or end. The gap a child keeps from the one
/// before it (its left margin) is left out at the start of a line, and from the one after it (its right margin) at the
/// end, so every line lines up with the others.
/// </summary>
public class WrapToolbar : System.Windows.Controls.Panel
{
    public static readonly DependencyProperty LineSpacingProperty = DependencyProperty.Register(
        nameof(LineSpacing), typeof(double), typeof(WrapToolbar),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty LineAlignmentProperty = DependencyProperty.Register(
        nameof(LineAlignment), typeof(HorizontalAlignment), typeof(WrapToolbar),
        new FrameworkPropertyMetadata(HorizontalAlignment.Left, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty IsSeparatorProperty = DependencyProperty.RegisterAttached(
        "IsSeparator", typeof(bool), typeof(WrapToolbar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    /// <summary>The room between one line and the next.</summary>
    public double LineSpacing
    {
        get => (double)GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    /// <summary>Where each line sits across the panel: Left, Center or Right (Stretch counts as Left).</summary>
    public HorizontalAlignment LineAlignment
    {
        get => (HorizontalAlignment)GetValue(LineAlignmentProperty);
        set => SetValue(LineAlignmentProperty, value);
    }

    public static bool GetIsSeparator(UIElement element) => (bool)element.GetValue(IsSeparatorProperty);

    public static void SetIsSeparator(UIElement element, bool value) => element.SetValue(IsSeparatorProperty, value);

    // Layout rounding can hand Arrange a width a fraction under the one Measure laid the lines out in.
    private const double Slack = 0.5;

    protected override Size MeasureOverride(Size available)
    {
        foreach (UIElement child in InternalChildren) child.Measure(new Size(available.Width, double.PositiveInfinity));

        var lines = Lines(available.Width);
        var width = 0.0;
        var height = 0.0;
        foreach (var line in lines)
        {
            width = Math.Max(width, line.ShownWidth);
            height += line.Height;
        }
        if (lines.Count > 1) height += LineSpacing * (lines.Count - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var placed = new HashSet<UIElement>();
        var top = 0.0;
        foreach (var line in Lines(final.Width))
        {
            var x = LineAlignment switch
            {
                HorizontalAlignment.Right => Math.Max(0, final.Width - line.ShownWidth),
                HorizontalAlignment.Center => Math.Max(0, (final.Width - line.ShownWidth) / 2),
                _ => 0,
            };
            x -= line.Lead;
            foreach (var child in line.Children)
            {
                child.Arrange(new Rect(x, top, child.DesiredSize.Width, line.Height));
                x += child.DesiredSize.Width;
                placed.Add(child);
            }
            top += line.Height + LineSpacing;
        }

        // Separators left at a line's edge, and anything collapsed, take no room and don't show.
        foreach (UIElement child in InternalChildren)
            if (!placed.Contains(child)) child.Arrange(new Rect());
        return final;
    }

    private sealed class Line
    {
        public readonly List<UIElement> Children = [];
        // From the first child's left margin to the last one's right margin
        public double Width;
        public double Height;
        // The first child's left margin and the last one's right, which the line doesn't show
        public double Lead;
        public double Trail;

        public double ShownWidth => Math.Max(0, Width - Lead - Trail);

        public void Add(UIElement child)
        {
            if (Children.Count == 0) Lead = Gap(child, left: true);
            Children.Add(child);
            Width += child.DesiredSize.Width;
            Height = Math.Max(Height, child.DesiredSize.Height);
            Trail = Gap(child, left: false);
        }
    }

    private static double Gap(UIElement child, bool left)
    {
        if (child is not FrameworkElement element) return 0;
        var gap = left ? element.Margin.Left : element.Margin.Right;
        return Math.Clamp(gap, 0, child.DesiredSize.Width);
    }

    private List<Line> Lines(double width)
    {
        var lines = new List<Line>();
        var line = new Line();
        UIElement? separator = null;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            if (GetIsSeparator(child))
            {
                // Held until what follows it is known to fit on the same line
                if (line.Children.Count > 0) separator = child;
                continue;
            }

            // Would the line, ending with this child, still fit?
            var before = separator?.DesiredSize.Width ?? 0;
            if (line.Children.Count > 0
                && line.Width - line.Lead + before + child.DesiredSize.Width - Gap(child, left: false) > width + Slack)
            {
                lines.Add(line);
                line = new Line();
                separator = null;
            }
            if (separator != null) line.Add(separator);
            separator = null;
            line.Add(child);
        }
        if (line.Children.Count > 0) lines.Add(line);
        return lines;
    }
}
