using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;

namespace RepoNLauncher;

// Dragging tabs, as in Chrome: along the tabs to move one, the others sliding aside; out of the window to give it a
// window of its own, which follows the mouse until it's let go; and onto another browser window's tabs to join them.
// Dragging a window's only tab drags the window. The mouse is read each frame rather than from its events, as the drag
// goes on across windows that open and close under it.
public partial class BrowserWindow
{
    // How far past the tabs a tab is dragged before it leaves for a window of its own, in the window's units.
    private const double DetachDistance = 28;

    // How far above or below a window's tabs a dragged tab still lands on them.
    private const double DropSlack = 10;

    private static readonly Duration SlideTime = TimeSpan.FromMilliseconds(150);

    /// <summary>A tab being dragged.</summary>
    private sealed class TabDrag(BrowserTab tab, BrowserWindow window, Point start, Point grab)
    {
        public BrowserTab Tab { get; } = tab;

        /// <summary>The window the tab is in now.</summary>
        public BrowserWindow Window { get; set; } = window;

        /// <summary>Where it was pressed, on the screen, so a click isn't taken for a drag.</summary>
        public Point Start { get; } = start;

        /// <summary>Where on the tab it was picked up, which stays under the mouse.</summary>
        public Point Grab { get; } = grab;

        /// <summary>It has moved far enough to be a drag.</summary>
        public bool Moving { get; set; }

        /// <summary>Its window follows the mouse: one of its own, dragged out, or the window it was the only tab of.</summary>
        public bool Floating { get; set; }

        /// <summary>
        /// Windows whose tabs were already under the mouse when its window started to follow it, such as another
        /// maximized one's, right behind a maximized window's: it joins them only once it has left them and come back.
        /// </summary>
        public HashSet<BrowserWindow> Passed { get; } = new();
    }

    private static TabDrag? _drag;

    private void BeginDrag(BrowserTab tab, FrameworkElement header, Point grab)
    {
        EndDrag();
        _drag = new TabDrag(tab, this, NativeMethods.CursorPosition(), grab);
        var shift = (TranslateTransform)header.RenderTransform;
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.X = 0;
        header.CaptureMouse();
        CompositionTarget.Rendering += OnDragFrame;
    }

    private static void OnDragFrame(object? sender, EventArgs e)
    {
        if (_drag is not { } drag) return;
        if (!NativeMethods.IsPrimaryButtonDown() || drag.Window._closed)
        {
            EndDrag();
            return;
        }

        var cursor = NativeMethods.CursorPosition();
        if (!drag.Moving)
        {
            if (Math.Abs(cursor.X - drag.Start.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(cursor.Y - drag.Start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            drag.Moving = true;
            // A window's only tab takes the window with it, out of full screen or maximized.
            if (drag.Window._tabs.Count == 1)
            {
                drag.Window.SetFullScreen(false);
                if (drag.Window.WindowState == WindowState.Maximized) drag.Window.WindowState = WindowState.Normal;
                drag.Floating = true;
                drag.Window.PassTabsUnder(drag, drag.Start);
            }
        }

        if (drag.Floating) drag.Window.FollowCursor(drag, cursor);
        else drag.Window.DragAlongTabs(drag, cursor);
    }

    private static void EndDrag()
    {
        if (_drag is not { } drag) return;
        _drag = null;
        CompositionTarget.Rendering -= OnDragFrame;

        var window = drag.Window;
        if (window._headers.TryGetValue(drag.Tab, out var header))
        {
            if (header.IsMouseCaptured) header.ReleaseMouseCapture();
            Panel.SetZIndex(header, 0);
            // Let go between places, it slides into its own.
            var shift = (TranslateTransform)header.RenderTransform;
            var from = shift.X;
            shift.X = 0;
            if (from != 0) shift.BeginAnimation(TranslateTransform.XProperty, Slide(from));
        }
        if (drag.Floating && !window._closed) window.Activate();
    }

    private static DoubleAnimation Slide(double from) =>
        new(from, 0, SlideTime) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };

    // Along the tabs: the tab follows the mouse, and takes the place it's over. Far enough off them, it leaves.
    private void DragAlongTabs(TabDrag drag, Point cursor)
    {
        if (!_headers.TryGetValue(drag.Tab, out var header) || PresentationSource.FromVisual(TabStrip) == null) return;

        var at = TabStrip.PointFromScreen(cursor);
        if (_tabs.Count > 1 && (at.Y < -DetachDistance || at.Y > TabStrip.ActualHeight + DetachDistance
                                || at.X < -DetachDistance || at.X > TabStrip.ActualWidth + DetachDistance))
        {
            Detach(drag, cursor);
            return;
        }

        var width = TabStrip.TabWidth;
        if (width <= 0) return;
        var left = Math.Clamp(at.X - drag.Grab.X, 0, (_tabs.Count - 1) * width);
        var index = Math.Clamp((int)Math.Round(left / width), 0, _tabs.Count - 1);
        MoveTab(drag.Tab, index);
        Panel.SetZIndex(header, 1);
        ((TranslateTransform)header.RenderTransform).X = left - index * width;
    }

    // Moves a tab to another place along the tabs; those it passes slide over to make room, as a browser's do. They're
    // what's moved among the strip's children, so the tab dragged stays put there and keeps the mouse.
    private void MoveTab(BrowserTab tab, int index)
    {
        var from = _tabs.IndexOf(tab);
        if (from < 0 || from == index) return;

        _tabs.RemoveAt(from);
        _tabs.Insert(index, tab);
        var width = TabStrip.TabWidth;
        var children = TabStrip.Children;
        if (from < index)
        {
            for (var i = from + 1; i <= index; i++)
            {
                var passed = children[i];
                children.RemoveAt(i);
                children.Insert(i - 1, passed);
                SlideFrom(passed, width);
            }
        }
        else
        {
            for (var i = from - 1; i >= index; i--)
            {
                var passed = children[i];
                children.RemoveAt(i);
                children.Insert(i + 1, passed);
                SlideFrom(passed, -width);
            }
        }
    }

    // A tab moved a place along slides there from where it was.
    private static void SlideFrom(UIElement header, double offset)
    {
        if (header.RenderTransform is not TranslateTransform shift) return;
        var from = shift.X + offset;
        shift.X = 0;
        shift.BeginAnimation(TranslateTransform.XProperty, Slide(from));
    }

    // Dragged off the tabs: the tab leaves for a window of its own, the size this one is when not maximized, which then
    // follows the mouse.
    private void Detach(TabDrag drag, Point cursor)
    {
        var tab = drag.Tab;
        var window = new BrowserWindow(_sites, _incognito);
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty) bounds = new Rect(Left, Top, Width, Height);
        window.Width = bounds.Width;
        window.Height = bounds.Height;

        // About where the tab is under the mouse; FollowCursor puts it there exactly once the window has drawn it.
        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var at = fromDevice.Transform(cursor);
        var stripOffset = TabStrip.TranslatePoint(new Point(0, 0), this);
        window.Left = at.X - drag.Grab.X - stripOffset.X - 8;
        window.Top = at.Y - drag.Grab.Y - stripOffset.Y - SystemParameters.WindowCaptionHeight - 8;

        drag.Window = window;
        drag.Floating = true;
        RemoveTab(tab);
        window.AddTab(tab, 0, select: true);
        window.Show();
        window._headers[tab].CaptureMouse();
    }

    // The window follows the mouse with the tab where it was picked up under it; over another browser window's tabs,
    // the tab joins them.
    private void FollowCursor(TabDrag drag, Point cursor)
    {
        if (!_headers.TryGetValue(drag.Tab, out var header) || PresentationSource.FromVisual(header) == null) return;

        var grab = header.PointToScreen(drag.Grab);
        NativeMethods.MoveWindowBy(new WindowInteropHelper(this).Handle, (int)Math.Round(cursor.X - grab.X),
            (int)Math.Round(cursor.Y - grab.Y));
        if (DropTarget(drag, cursor) is { } target) target.TakeTab(drag, this, cursor);
    }

    // The other windows whose tabs are under the mouse as this one starts to follow it.
    private void PassTabsUnder(TabDrag drag, Point cursor)
    {
        drag.Passed.Clear();
        drag.Passed.UnionWith(OpenWindows.Where(w => w != this && w.CanTakeTabAt(cursor)));
    }

    // The browser window whose tabs are under the mouse, of the same kind (incognito or not) as this one; the one in
    // front, when they overlap. Those it started over count once the mouse has left them.
    private BrowserWindow? DropTarget(TabDrag drag, Point cursor)
    {
        drag.Passed.RemoveWhere(w => !w.CanTakeTabAt(cursor));
        var under = OpenWindows.Where(w => w != this && w._incognito == _incognito && !drag.Passed.Contains(w) && w.CanTakeTabAt(cursor))
            .ToList();
        if (under.Count <= 1) return under.FirstOrDefault();

        var handles = under.ToDictionary(w => new WindowInteropHelper(w).Handle);
        return NativeMethods.WindowsFrontToBack().Select(h => handles.GetValueOrDefault(h)).FirstOrDefault(w => w != null);
    }

    private bool CanTakeTabAt(Point cursor)
    {
        if (_closed || !IsVisible || WindowState == WindowState.Minimized || !TabBar.IsVisible
            || PresentationSource.FromVisual(TabBar) == null) return false;
        var at = TabBar.PointFromScreen(cursor);
        return at.X >= 0 && at.X <= TabBar.ActualWidth && at.Y >= -DropSlack && at.Y <= TabBar.ActualHeight + DropSlack;
    }

    // The tab dragged onto this window's tabs joins them, where it's over; the window it was alone in closes. It's
    // still being dragged, now along these tabs.
    private void TakeTab(TabDrag drag, BrowserWindow from, Point cursor)
    {
        var tab = drag.Tab;
        var at = TabStrip.PointFromScreen(cursor);
        var width = TabStrip.TabWidth > 0 ? TabStrip.TabWidth : TabStrip.MaxTabWidth;
        var index = Math.Clamp((int)Math.Round((at.X - drag.Grab.X) / width), 0, _tabs.Count);

        drag.Window = this;
        drag.Floating = false;
        from.RemoveTab(tab);
        AddTab(tab, index, select: true);
        NativeMethods.BringToFront(this);

        var header = _headers[tab];
        Panel.SetZIndex(header, 1);
        UpdateLayout();
        header.CaptureMouse();
    }
}
