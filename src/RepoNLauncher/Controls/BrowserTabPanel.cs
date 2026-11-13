using System.Windows;
using System.Windows.Controls;
using Size = System.Windows.Size;

namespace RepoNLauncher.Controls;

/// <summary>
/// A browser window's tabs, side by side as browsers lay them out: each as wide as <see cref="MaxTabWidth"/> while they
/// fit, all narrower alike as more open, down to <see cref="MinTabWidth"/>. Its last child isn't a tab but what comes
/// straight after them, the new-tab button.
/// </summary>
public class BrowserTabPanel : System.Windows.Controls.Panel
{
    public double MaxTabWidth { get; set; } = 220;
    public double MinTabWidth { get; set; } = 56;

    /// <summary>How wide each tab is now, for working out which place a tab dragged along the tabs is over.</summary>
    public double TabWidth { get; private set; }

    protected override Size MeasureOverride(Size available)
    {
        var children = InternalChildren;
        if (children.Count == 0) return default;

        var trailing = children[children.Count - 1];
        trailing.Measure(available);
        var tabs = children.Count - 1;
        var room = double.IsInfinity(available.Width)
            ? tabs * MaxTabWidth
            : Math.Max(0, available.Width - trailing.DesiredSize.Width);
        // Whole pixels, so the tabs' edges stay sharp.
        TabWidth = tabs == 0 ? 0 : Math.Floor(Math.Clamp(room / tabs, MinTabWidth, MaxTabWidth));

        var height = trailing.DesiredSize.Height;
        for (var i = 0; i < tabs; i++)
        {
            children[i].Measure(new Size(TabWidth, available.Height));
            height = Math.Max(height, children[i].DesiredSize.Height);
        }
        var width = tabs * TabWidth + trailing.DesiredSize.Width;
        return new Size(double.IsInfinity(available.Width) ? width : Math.Min(width, available.Width), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = InternalChildren;
        if (children.Count == 0) return final;

        var tabs = children.Count - 1;
        var x = 0.0;
        for (var i = 0; i < tabs; i++)
        {
            children[i].Arrange(new Rect(x, 0, TabWidth, final.Height));
            x += TabWidth;
        }
        var trailing = children[tabs];
        trailing.Arrange(new Rect(x, (final.Height - trailing.DesiredSize.Height) / 2, trailing.DesiredSize.Width,
            trailing.DesiredSize.Height));
        return final;
    }
}
