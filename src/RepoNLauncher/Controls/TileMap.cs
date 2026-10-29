using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RepoNLauncher.Models;
using RepoNLauncher.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using FlowDirection = System.Windows.FlowDirection;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace RepoNLauncher.Controls;

/// <summary>
/// A map of OpenStreetMap's tiles with a pin: drag to look around, scroll to zoom, click to put the pin there, or drag
/// the pin. A <see cref="Pin"/> set from outside, rather than with the mouse, moves the map to show it.
/// </summary>
public class TileMap : FrameworkElement
{
    public const double MinZoom = 1, MaxZoom = 19;
    private const int TileSize = WebMercator.TileSize;
    private const double DragDistance = 4;
    private const double CornerRadius = 6;

    // Where the pin's tip is, at (0, 0): a drop with a dot in its head.
    private static readonly Geometry PinShape = Freeze(Geometry.Parse("M 0,0 C -2.5,-7 -10,-12 -10,-19 A 10,10 0 1 1 10,-19 C 10,-12 2.5,-7 0,0 Z"));
    private static readonly Pen PinOutline = Freeze(new Pen(Brushes.White, 1.5));
    private static readonly Brush PinShadow = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromArgb(70, 0, 0, 0)));
    private static readonly Brush Land = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE4, 0xE1, 0xDA)));
    private static readonly Brush NoteText = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x58, 0x60)));

    public static readonly DependencyProperty PinProperty = DependencyProperty.Register(
        nameof(Pin), typeof(GeoPoint?), typeof(TileMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPinChanged));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(TileMap),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnZoomChanged,
            (_, value) => Math.Clamp((double)value, MinZoom, MaxZoom)));

    /// <summary>Where the pin is; null for none.</summary>
    public GeoPoint? Pin
    {
        get => (GeoPoint?)GetValue(PinProperty);
        set => SetValue(PinProperty, value);
    }

    /// <summary>The zoom level being shown, or eased to: 1 shows the whole world, 19 a street close up.</summary>
    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    // The map's middle as a world position (see WebMercator), and the zoom shown this moment, which eases towards Zoom.
    private double _centerX = 0.5, _centerY = 0.42;
    private double _shownZoom = 2;
    private Point? _zoomAnchor; // the spot under the mouse, kept there while zooming with the wheel
    private bool _easing;
    private bool _fromMouse; // Pin or Zoom being set here, so the map doesn't jump to it

    private enum Gesture { None, Pressed, Panning, DraggingPin }
    private Gesture _gesture;
    private Point _pressedAt, _lastMouse;
    private Vector _pinGrab; // from the mouse to the pin's tip, while dragging the pin

    private readonly Dictionary<TileKey, CancellationTokenSource> _loading = new();
    private readonly HashSet<TileKey> _shown = new();

    public TileMap()
    {
        ClipToBounds = true;
        Focusable = false;
        Cursor = Cursors.Cross;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Unloaded += (_, _) => StopLoading(keep: null);
    }

    /// <summary>Zooms in (more than 0) or out, about the middle.</summary>
    public void ZoomBy(double levels) => ZoomAbout(null, levels);

    private static void OnPinChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (TileMap)d;
        if (!map._fromMouse && e.NewValue is GeoPoint pin)
        {
            (map._centerX, map._centerY) = WebMercator.ToWorld(pin);
            map._zoomAnchor = null;
            map.KeepInWorld();
        }
        map.InvalidateVisual();
    }

    private static void OnZoomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (TileMap)d;
        if (map._fromMouse)
        {
            map.StartEasing();
            return;
        }

        map.StopEasing();
        map._zoomAnchor = null;
        map._shownZoom = (double)e.NewValue;
        map.KeepInWorld();
        map.InvalidateVisual();
    }

    private void ZoomAbout(Point? anchor, double levels)
    {
        _zoomAnchor = anchor;
        _fromMouse = true;
        try
        {
            SetCurrentValue(ZoomProperty, Math.Clamp(Zoom + levels, MinZoom, MaxZoom));
        }
        finally
        {
            _fromMouse = false;
        }
    }

    private void StartEasing()
    {
        if (_easing) return;
        _easing = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopEasing()
    {
        if (!_easing) return;
        _easing = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var target = Zoom;
        var next = _shownZoom + (target - _shownZoom) * 0.35;
        if (Math.Abs(target - next) < 0.01) next = target;

        if (_zoomAnchor is { } anchor)
        {
            // Keep the place under the mouse where it is.
            var (x, y) = WorldAt(anchor);
            _shownZoom = next;
            var world = WorldSize;
            _centerX = x - (anchor.X - ActualWidth / 2) / world;
            _centerY = y - (anchor.Y - ActualHeight / 2) / world;
        }
        else
        {
            _shownZoom = next;
        }
        KeepInWorld();
        InvalidateVisual();
        if (next == target) StopEasing();
    }

    private double WorldSize => TileSize * Math.Pow(2, _shownZoom);

    private (double X, double Y) WorldAt(Point point) =>
        (_centerX + (point.X - ActualWidth / 2) / WorldSize, _centerY + (point.Y - ActualHeight / 2) / WorldSize);

    private Point ScreenAt(GeoPoint place)
    {
        var (x, y) = WebMercator.ToWorld(place);
        var world = WorldSize;
        var across = (x - _centerX) * world;
        across -= Math.Round(across / world) * world; // the copy of the world nearest the middle
        return new Point(ActualWidth / 2 + across, ActualHeight / 2 + (y - _centerY) * world);
    }

    // Round the world east and west; no further north or south than its edges.
    private void KeepInWorld()
    {
        _centerX -= Math.Floor(_centerX);
        var half = ActualHeight / 2 / WorldSize;
        _centerY = half >= 0.5 ? 0.5 : Math.Clamp(_centerY, half, 1 - half);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        KeepInWorld();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size.Width <= 0 || size.Height <= 0) return;

        dc.PushClip(new RectangleGeometry(new Rect(size), CornerRadius, CornerRadius));
        dc.DrawRectangle(Land, null, new Rect(size));

        // The tiles of the nearest whole zoom level, scaled to the zoom shown.
        var level = (int)Math.Clamp(Math.Round(_shownZoom), 0, MaxZoom);
        var count = 1 << level;
        var tile = TileSize * Math.Pow(2, _shownZoom - level);
        var left = size.Width / 2 - _centerX * count * tile;
        var top = size.Height / 2 - _centerY * count * tile;

        _shown.Clear();
        var drewAny = false;
        var firstRow = Math.Max(0, (int)Math.Floor(-top / tile));
        var lastRow = Math.Min(count - 1, (int)Math.Floor((size.Height - top) / tile));
        var firstColumn = (int)Math.Floor(-left / tile);
        var lastColumn = (int)Math.Floor((size.Width - left) / tile);
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var key = new TileKey(level, ((column % count) + count) % count, row);
                // A hair bigger than the tile, so no seams show between tiles at in-between zooms.
                var rect = new Rect(left + column * tile, top + row * tile, tile + 0.6, tile + 0.6);
                _shown.Add(key);
                if (MapTiles.Cached(key) is { } image)
                {
                    dc.DrawImage(image, rect);
                    drewAny = true;
                }
                else
                {
                    drewAny |= DrawStandIn(dc, key, rect, tile);
                    Load(key);
                }
            }
        }
        StopLoading(keep: _shown);

        if (!drewAny && MapTiles.IsOffline)
        {
            var note = new FormattedText("The map needs the internet. You can still type the coordinates.",
                CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, NoteText,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, size.Width - 40),
                TextAlignment = TextAlignment.Center,
            };
            dc.DrawText(note, new Point(20, (size.Height - note.Height) / 2));
        }

        if (Pin is { } pin) DrawPin(dc, ScreenAt(pin));
        dc.Pop();
    }

    // While a tile loads: the tile over it, zoomed in, or the four under it, zoomed out, if they're in memory.
    private static bool DrawStandIn(DrawingContext dc, TileKey key, Rect rect, double tile)
    {
        for (var up = 1; up <= Math.Min(key.Zoom, 6); up++)
        {
            var parent = new TileKey(key.Zoom - up, key.X >> up, key.Y >> up);
            if (MapTiles.Cached(parent) is not { } image) continue;

            var span = 1 << up;
            var parentRect = new Rect(rect.X - (key.X - (parent.X << up)) * tile, rect.Y - (key.Y - (parent.Y << up)) * tile,
                tile * span + 0.6, tile * span + 0.6);
            dc.PushClip(new RectangleGeometry(rect));
            dc.DrawImage(image, parentRect);
            dc.Pop();
            return true;
        }

        if (key.Zoom >= MaxZoom) return false;
        var drew = false;
        for (var i = 0; i < 4; i++)
        {
            if (MapTiles.Cached(new TileKey(key.Zoom + 1, key.X * 2 + i % 2, key.Y * 2 + i / 2)) is not { } image) continue;
            dc.DrawImage(image, new Rect(rect.X + i % 2 * tile / 2, rect.Y + i / 2 * tile / 2, tile / 2 + 0.6, tile / 2 + 0.6));
            drew = true;
        }
        return drew;
    }

    private void DrawPin(DrawingContext dc, Point tip)
    {
        var fill = TryFindResource("Accent") as Brush ?? Brushes.MediumPurple;
        dc.DrawEllipse(PinShadow, null, tip, 5, 2.5);
        dc.PushTransform(new TranslateTransform(tip.X, tip.Y));
        dc.DrawGeometry(fill, PinOutline, PinShape);
        dc.DrawEllipse(Brushes.White, null, new Point(0, -19), 3.5, 3.5);
        dc.Pop();
    }

    private bool IsOnPin(Point point)
    {
        if (Pin is not { } pin) return false;
        var tip = ScreenAt(pin);
        return point.X >= tip.X - 11 && point.X <= tip.X + 11 && point.Y >= tip.Y - 31 && point.Y <= tip.Y + 1;
    }

    private void Load(TileKey key)
    {
        if (_loading.ContainsKey(key)) return;
        var cancel = new CancellationTokenSource();
        _loading[key] = cancel;
        _ = LoadAsync(key, cancel);
    }

    private async Task LoadAsync(TileKey key, CancellationTokenSource cancel)
    {
        BitmapSource? image = null;
        try
        {
            image = await MapTiles.LoadAsync(key, cancel.Token);
        }
        catch (OperationCanceledException)
        {
        }

        if (_loading.TryGetValue(key, out var current) && current == cancel) _loading.Remove(key);
        cancel.Dispose();
        if (image != null || MapTiles.IsOffline) InvalidateVisual();
    }

    // Tiles moved out of view aren't fetched: the tile policy allows only the ones being looked at.
    private void StopLoading(HashSet<TileKey>? keep)
    {
        foreach (var (key, cancel) in _loading.ToList())
        {
            if (keep?.Contains(key) == true) continue;
            cancel.Cancel();
            _loading.Remove(key);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var point = e.GetPosition(this);
        _pressedAt = _lastMouse = point;
        if (IsOnPin(point) && Pin is { } pin)
        {
            _gesture = Gesture.DraggingPin;
            _pinGrab = ScreenAt(pin) - point;
        }
        else
        {
            _gesture = Gesture.Pressed;
        }
        StopEasing();
        _zoomAnchor = null;
        _shownZoom = Zoom;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_gesture == Gesture.None)
        {
            Cursor = IsOnPin(point) ? Cursors.Hand : Cursors.Cross;
            return;
        }

        if (_gesture == Gesture.Pressed && (point - _pressedAt).Length > DragDistance)
        {
            _gesture = Gesture.Panning;
            Cursor = Cursors.SizeAll;
        }

        if (_gesture == Gesture.Panning)
        {
            _centerX -= (point.X - _lastMouse.X) / WorldSize;
            _centerY -= (point.Y - _lastMouse.Y) / WorldSize;
            KeepInWorld();
            InvalidateVisual();
        }
        else if (_gesture == Gesture.DraggingPin)
        {
            PinAt(point + _pinGrab);
        }
        _lastMouse = point;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_gesture == Gesture.None) return;

        if (_gesture == Gesture.Pressed) PinAt(e.GetPosition(this)); // a click
        _gesture = Gesture.None;
        Cursor = IsOnPin(e.GetPosition(this)) ? Cursors.Hand : Cursors.Cross;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _gesture = Gesture.None;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ZoomAbout(e.GetPosition(this), e.Delta / 120.0);
        e.Handled = true;
    }

    private void PinAt(Point point)
    {
        var (x, y) = WorldAt(point);
        _fromMouse = true;
        try
        {
            SetCurrentValue(PinProperty, WebMercator.FromWorld(x, y));
        }
        finally
        {
            _fromMouse = false;
        }
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
