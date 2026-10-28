using System.Windows;
using System.Windows.Controls;

namespace RepoNLauncher.Controls;

/// <summary>
/// Lays out up to four chats: one fills the space, two sit side by side, three put the first in a tall column with the
/// other two stacked beside it, and four make a 2×2 grid. <see cref="Gap"/> is left between them for a divider to show.
/// </summary>
public class ChatPanesPanel : System.Windows.Controls.Panel
{
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(ChatPanesPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size available)
    {
        // It takes all the room it's given; with no limit, it sizes to nothing rather than to infinity.
        var size = new System.Windows.Size(
            double.IsInfinity(available.Width) ? 0 : available.Width,
            double.IsInfinity(available.Height) ? 0 : available.Height);
        var cells = Cells(InternalChildren.Count, size);
        for (var i = 0; i < InternalChildren.Count; i++) InternalChildren[i].Measure(cells[i].Size);
        return size;
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size final)
    {
        var cells = Cells(InternalChildren.Count, final);
        for (var i = 0; i < InternalChildren.Count; i++) InternalChildren[i].Arrange(cells[i]);
        return final;
    }

    private Rect[] Cells(int count, System.Windows.Size size)
    {
        var w = size.Width;
        var h = size.Height;
        var half = Math.Max(0, (w - Gap) / 2);
        var halfHeight = Math.Max(0, (h - Gap) / 2);
        var right = half + Gap;
        var bottom = halfHeight + Gap;
        Rect[] layout = count switch
        {
            0 => [],
            1 => [new(0, 0, w, h)],
            2 => [new(0, 0, half, h), new(right, 0, half, h)],
            3 => [new(0, 0, half, h), new(right, 0, half, halfHeight), new(right, bottom, half, halfHeight)],
            _ => [new(0, 0, half, halfHeight), new(right, 0, half, halfHeight), new(0, bottom, half, halfHeight), new(right, bottom, half, halfHeight)],
        };
        // More than four never happens; any extra would be stacked out of sight on the last cell.
        return count <= 4 ? layout : [.. layout, .. Enumerable.Repeat(layout[^1], count - 4)];
    }
}
