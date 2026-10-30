using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Size = System.Windows.Size;

namespace RepoNLauncher.Controls;

/// <summary>
/// Rows of same-size tiles, like File Explorer's icon views, making only the ones in view (and a row either side, for
/// the arrow keys to move to): a camera folder of thousands of photos opens as quickly as one of ten. It scrolls itself,
/// up and down only.
/// </summary>
public class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(nameof(TileWidth), typeof(double),
        typeof(VirtualizingTilePanel), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(nameof(TileHeight), typeof(double),
        typeof(VirtualizingTilePanel), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private Size _extent;
    private Size _viewport;
    private double _offset;
    private int _columns = 1;

    public double TileWidth
    {
        get => (double)GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }

    public double TileHeight
    {
        get => (double)GetValue(TileHeightProperty);
        set => SetValue(TileHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        _columns = double.IsInfinity(availableSize.Width) ? Math.Max(1, count) : Math.Max(1, (int)(availableSize.Width / TileWidth));
        var rows = (count + _columns - 1) / _columns;
        var viewport = new Size(double.IsInfinity(availableSize.Width) ? _columns * TileWidth : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? rows * TileHeight : availableSize.Height);
        UpdateScrollInfo(new Size(viewport.Width, rows * TileHeight), viewport);

        var first = Math.Min(count, Math.Max(0, (int)(_offset / TileHeight) - 1) * _columns);
        var last = Math.Min(count - 1, ((int)Math.Ceiling((_offset + _viewport.Height) / TileHeight) + 1) * _columns - 1);
        Realize(first, last);
        CleanUp(first, last);
        return viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        for (var i = 0; i < children.Count; i++)
        {
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            children[i].Arrange(new Rect(index % _columns * TileWidth, index / _columns * TileHeight - _offset, TileWidth, TileHeight));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Move)
            RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
    }

    // What ListBox asks for to move the selection to a tile that isn't made yet, wanting it made by the time this returns.
    protected override void BringIndexIntoView(int index)
    {
        ScrollIntoView(index);
        UpdateLayout();
    }

    private void Realize(int first, int last)
    {
        if (last < first) return;
        // InternalChildren first: it's what ties the generator to this panel.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        var start = generator.GeneratorPositionFromIndex(first);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            for (var i = first; i <= last; i++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out var isNew);
                if (isNew)
                {
                    if (childIndex >= children.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(TileWidth, TileHeight));
            }
        }
    }

    // The tile with the keyboard focus stays, out of view, so the arrow keys still have somewhere to start from.
    private void CleanUp(int first, int last)
    {
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index >= first && index <= last || children[i].IsKeyboardFocusWithin) continue;
            generator.Remove(new GeneratorPosition(i, 0), 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    private void ScrollIntoView(int index)
    {
        var top = index / _columns * TileHeight;
        if (top < _offset) SetVerticalOffset(top);
        else if (top + TileHeight > _offset + _viewport.Height) SetVerticalOffset(top + TileHeight - _viewport.Height);
    }

    // Fewer files, or a taller window, can leave the offset past the end.
    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        var offset = Math.Max(0, Math.Min(_offset, extent.Height - viewport.Height));
        if (extent == _extent && viewport == _viewport && offset == _offset) return;
        _extent = extent;
        _viewport = viewport;
        _offset = offset;
        ScrollOwner?.InvalidateScrollInfo();
    }

    // A turn of the mouse wheel (three lines) is a row.
    private double LineHeight => TileHeight / 3;

    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;
    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(_offset - LineHeight);
    public void LineDown() => SetVerticalOffset(_offset + LineHeight);
    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset - SystemParameters.WheelScrollLines * LineHeight);
    public void MouseWheelDown() => SetVerticalOffset(_offset + SystemParameters.WheelScrollLines * LineHeight);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));
        if (offset == _offset) return;
        _offset = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i] != visual && !children[i].IsAncestorOf(visual)) continue;
            ScrollIntoView(ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0)));
            break;
        }
        return rectangle;
    }
}
