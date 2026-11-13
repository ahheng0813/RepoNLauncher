using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using RepoNLauncher.Services;
using RepoNLauncher.ViewModels;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace RepoNLauncher.Controls;

/// <summary>
/// A project's Run console. The output is text that can be selected with the mouse and copied, like a terminal's, and
/// searched: every match is marked, and the arrows step through them. Like a terminal, it follows new output, so a prompt
/// is in view when it asks. Scrolled up to read, it stays put and offers Jump to present, which (like scrolling back down
/// to the bottom) follows the output again.
/// </summary>
public partial class RunConsole : UserControl
{
    public static readonly DependencyProperty CanPopOutProperty = DependencyProperty.Register(
        nameof(CanPopOut), typeof(bool), typeof(RunConsole), new PropertyMetadata(true, (d, e) =>
            ((RunConsole)d).PopOutButton.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    /// <summary>Offers to open the console in a window of its own; off in that window.</summary>
    public bool CanPopOut
    {
        get => (bool)GetValue(CanPopOutProperty);
        set => SetValue(CanPopOutProperty, value);
    }

    // A search that long is a paste gone wrong rather than something to look for.
    private const int MaxSelectionToSearch = 200;

    private readonly SearchMarker _marker = new();
    private readonly DashboardLineColorizer _dashboardLines = new();

    // New output while searching moves the count on, at most this often rather than at every line.
    private readonly DispatcherTimer _recount = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private ObservableCollection<string>? _lines;

    // The lines shown, and where the last one starts: a partial line (a prompt) is replaced as more of it comes.
    private int _lineCount;
    private int _lastLineStart;

    // Where the search text is, in order, and which of them the arrows are on (-1: none yet).
    private readonly List<int> _matches = new();
    private int _current = -1;

    // Whether the view keeps the latest output in view. It's a state of its own rather than worked out from each scroll
    // (was it at the bottom before this change?), which a resize or the console coming back into view at the same time
    // as new output got wrong, and then it stopped following for good.
    private bool _following = true;

    // While the user moves the view (the mouse held on the output or its scrollbar, or a scroll they asked for still to
    // land) it isn't pulled to the bottom: a move up stops following, and it catches up after if it's still following.
    private bool _mouseHeld;
    private int _scrollsLanding;
    private bool UserScrolling => _mouseHeld || _scrollsLanding > 0;

    public RunConsole()
    {
        InitializeComponent();

        var options = Output.Options;
        options.EnableHyperlinks = true;
        options.RequireControlModifierForHyperlinkClick = true;
        options.EnableEmailHyperlinks = false;
        options.ShowBoxForControlCharacters = false;
        options.EnableTextDragDrop = false;
        options.AllowScrollBelowDocument = false;
        options.HighlightCurrentLine = false;
        // Every line the app prints would otherwise be kept a second time, to undo.
        Output.Document.UndoStack.SizeLimit = 0;

        var area = Output.TextArea;
        area.Caret.CaretBrush = Brushes.Transparent;
        area.SelectionForeground = null;
        area.SelectionBorder = null;
        area.SelectionCornerRadius = 0;

        var view = area.TextView;
        view.LineTransformers.Add(_dashboardLines);
        view.LineTransformers.Add(_marker);
        ApplyColors();
        // A theme change while the console isn't loaded is picked up when it is.
        Loaded += (_, _) =>
        {
            ThemeService.Changed -= OnThemeChanged;
            ThemeService.Changed += OnThemeChanged;
            ApplyColors();
        };
        Unloaded += (_, _) => ThemeService.Changed -= OnThemeChanged;

        Output.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Output_ScrollChanged));
        Output.AddHandler(System.Windows.Documents.Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(Output_LinkClicked));
        Output.PreviewMouseWheel += Output_PreviewMouseWheel;
        Output.PreviewMouseLeftButtonDown += (_, _) => _mouseHeld = true;
        Output.PreviewMouseLeftButtonUp += (_, _) => MouseLetGo();
        Output.AddHandler(LostMouseCaptureEvent, new System.Windows.Input.MouseEventHandler((_, _) => MouseLetGo()), handledEventsToo: true);
        Output.IsVisibleChanged += (_, _) =>
        {
            if (Output.IsVisible && _following) Output.ScrollToEnd();
        };

        _recount.Tick += (_, _) => FindMatches(landOnNearest: false);
        // The project's console lines: the run's whose tab is open, so they change when another tab is picked.
        SetBinding(LinesProperty, new System.Windows.Data.Binding(nameof(ProjectViewModel.ConsoleOutput)));
    }

    private static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        "Lines", typeof(ObservableCollection<string>), typeof(RunConsole),
        new PropertyMetadata(null, (d, e) => ((RunConsole)d).Attach(e.NewValue as ObservableCollection<string>)));

    /// <summary>Puts the cursor in the search box, with the selected text in it when that's a few words on one line.</summary>
    public void FocusSearch()
    {
        var selection = Output.TextArea.Selection;
        if (!selection.IsEmpty && !selection.IsMultiline && selection.GetText() is { Length: <= MaxSelectionToSearch } selected
            && selected.Trim().Length > 0)
            SearchBox.Text = selected;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyColors();

    // The editor takes brushes rather than resource references, so they're set from the palette again when it changes.
    private void ApplyColors()
    {
        var area = Output.TextArea;
        area.SelectionBrush = Resource("Console.Selection", Brushes.Gray);
        var cyan = Resource("Cyan", Brushes.Cyan);
        area.TextView.LinkTextForegroundBrush = cyan;
        _dashboardLines.Brush = cyan;
        var warning = (Resource("Warning", Brushes.Gold) as SolidColorBrush)?.Color ?? Colors.Gold;
        _marker.MatchBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x60, warning.R, warning.G, warning.B)));
        _marker.CurrentBrush = Frozen(new SolidColorBrush(warning));
        _marker.CurrentTextBrush = Resource("Bg.App", Brushes.Black);
        area.TextView.Redraw();
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    // ---- The output, kept in step with the project's console lines ----

    private void Attach(ObservableCollection<string>? lines)
    {
        if (ReferenceEquals(lines, _lines)) return;
        if (_lines != null) _lines.CollectionChanged -= Lines_CollectionChanged;
        _lines = lines;
        if (_lines != null) _lines.CollectionChanged += Lines_CollectionChanged;
        Rebuild();
    }

    // The console only ever gains lines at the end, has its last line replaced, or is cleared for a new run; anything
    // else is shown afresh.
    private void Lines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var document = Output.Document;
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == _lineCount && e.NewItems != null)
        {
            var added = new StringBuilder();
            foreach (string line in e.NewItems)
            {
                if (_lineCount > 0) added.Append('\n');
                _lastLineStart = document.TextLength + added.Length;
                added.Append(OneLine(line));
                _lineCount++;
            }
            document.Insert(document.TextLength, added.ToString());
        }
        else if (e.Action == NotifyCollectionChangedAction.Replace && e.NewStartingIndex == _lineCount - 1
                 && e.NewItems?.Count == 1 && e.NewItems[0] is string line)
        {
            document.Replace(_lastLineStart, document.TextLength - _lastLineStart, OneLine(line));
        }
        else
        {
            Rebuild();
            return;
        }

        if (SearchBox.Text.Length > 0 && !_recount.IsEnabled) _recount.Start();
    }

    private void Rebuild()
    {
        var text = new StringBuilder();
        _lineCount = _lines?.Count ?? 0;
        _lastLineStart = 0;
        for (var i = 0; i < _lineCount; i++)
        {
            if (i > 0) text.Append('\n');
            _lastLineStart = text.Length;
            text.Append(OneLine(_lines![i]));
        }

        Output.TextArea.ClearSelection();
        Output.Document.Text = text.ToString();
        _current = -1;
        FindMatches(landOnNearest: false);
        // A new run, another run's tab, or another project's console: from its latest output.
        JumpToPresent();
    }

    // Lines come split already; a line break inside one would put the lines shown out of step with the console's.
    private static string OneLine(string line) =>
        line.AsSpan().IndexOfAny('\r', '\n') < 0 ? line : line.Replace('\r', ' ').Replace('\n', ' ');

    // ---- Following the output ----

    private bool IsAtBottom => Output.VerticalOffset + Output.ViewportHeight >= Output.ExtentHeight - 1;

    private void SetFollowing(bool following)
    {
        _following = following;
        JumpToPresentButton.Visibility = following ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Goes to the latest output, and keeps it in view as more comes.</summary>
    private void JumpToPresent()
    {
        SetFollowing(true);
        Output.ScrollToEnd();
    }

    private void JumpToPresent_Click(object sender, RoutedEventArgs e) => JumpToPresent();

    // The output grows, the console is resized or comes back into view, the lines rewrap: whatever moved, it decides
    // from where the view is now and whether it's following, never from what it was before the change.
    private void Output_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer viewer || !ReferenceEquals(viewer.TemplatedParent, Output)) return;

        // The mouse let go where it wasn't seen (outside the window, nothing captured).
        if (_mouseHeld && Mouse.LeftButton != MouseButtonState.Pressed) _mouseHeld = false;

        if (viewer.VerticalOffset + viewer.ViewportHeight >= viewer.ExtentHeight - 1)
        {
            // At the bottom, however it got there: following.
            SetFollowing(true);
            return;
        }

        if (UserScrolling)
        {
            // Moved up by the user: reading, so it stays where they put it.
            if (e.VerticalChange < 0) SetFollowing(false);
            return;
        }

        if (!_following) return;

        // Moved up with nothing else changing: keys, a selection dragged past the top, a match shown.
        if (e.VerticalChange < 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0) SetFollowing(false);
        // More output, or a resize: back to the latest line. Lines measured on the way down can grow the output again,
        // which brings another change here, until it rests at the bottom.
        else viewer.ScrollToEnd();
    }

    // Wheeling up starts the user's scroll; whether it left the bottom is decided once it lands.
    private void Output_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0 && Output.VerticalOffset > 0) LetScrollLand();
    }

    /// <summary>
    /// For a scroll the user asked for (a wheel, a key, a search match), which may or may not leave the bottom: the view
    /// isn't pulled down until it has landed, then catches up if it's still following.
    /// </summary>
    private void LetScrollLand()
    {
        _scrollsLanding++;
        // After layout, which is where the scroll lands.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _scrollsLanding--;
            if (!UserScrolling) CatchUp();
        });
    }

    private void MouseLetGo()
    {
        if (!_mouseHeld) return;
        // After the thumb or the selection has taken its last move, which comes after the button's release.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!_mouseHeld || Mouse.LeftButton == MouseButtonState.Pressed) return;
            _mouseHeld = false;
            if (!UserScrolling) CatchUp();
        });
    }

    // The user's scroll is over: still following (they didn't move up, or came back down), so to the output that came
    // meanwhile.
    private void CatchUp()
    {
        var atBottom = IsAtBottom;
        if (!_following && !atBottom) return;
        SetFollowing(true);
        if (!atBottom) Output.ScrollToEnd();
    }

    // Ctrl+click on an address: the app's own site opens where the Run tab's Open in says, like its URL buttons; any
    // other page in the default browser.
    private void Output_LinkClicked(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        var uri = e.Uri;
        if (uri.Host is "0.0.0.0" or "[::]") uri = new UriBuilder(uri) { Host = "localhost" }.Uri;

        if (uri.IsLoopback && DataContext is ProjectViewModel project)
        {
            project.OpenSiteCommand.Execute(uri.AbsoluteUri);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
    }

    // ---- Copying ----

    private void OutputMenu_Opened(object sender, RoutedEventArgs e) => CopyMenuItem.IsEnabled = !Output.TextArea.Selection.IsEmpty;

    private void Copy_Click(object sender, RoutedEventArgs e) => Output.Copy();

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (Output.Document.TextLength > 0) ClipboardText.Set(Output.Document.Text.Replace("\n", Environment.NewLine));
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        Output.SelectAll();
        Output.TextArea.Focus();
    }

    private void PopOut_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ProjectViewModel project) RunConsoleWindow.Open(project);
    }

    // ---- Searching ----

    private void Search_Click(object sender, RoutedEventArgs e) => FocusSearch();

    private void RunConsole_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FocusSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.F3 && SearchBox.Text.Length > 0)
        {
            Step(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (Output.TextArea.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.End && Keyboard.Modifiers == ModifierKeys.Control)
            {
                JumpToPresent();
                e.Handled = true;
            }
            // The output's own keys that can scroll it up.
            else if (e.Key is Key.PageUp or Key.Up || (e.Key == Key.Home && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
                LetScrollLand();
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Step(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // The first Esc clears the search, the next goes back to the output.
            if (SearchBox.Text.Length > 0) SearchBox.Clear();
            else Output.TextArea.Focus();
            e.Handled = true;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => FindMatches(landOnNearest: true);

    private void PreviousMatch_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void NextMatch_Click(object sender, RoutedEventArgs e) => Step(1);

    /// <summary>
    /// Finds every place the output has the search text. landOnNearest is for a changed search: it goes to the first
    /// match from the one it was on, else from the top of what's in view, else to the last one (the latest output).
    /// Otherwise it stays on the match it was on, while that's still there.
    /// </summary>
    private void FindMatches(bool landOnNearest)
    {
        _recount.Stop();
        var was = _current >= 0 && _current < _matches.Count ? _matches[_current] : -1;
        var term = SearchBox.Text;

        _matches.Clear();
        if (term.Length > 0)
        {
            var text = Output.Document.Text;
            for (var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = text.IndexOf(term, at + term.Length, StringComparison.OrdinalIgnoreCase))
                _matches.Add(at);
        }

        _marker.Term = term;
        if (landOnNearest)
        {
            _current = NearestMatch(was >= 0 ? was : TopInView());
            ShowCurrent(scroll: true);
        }
        else
        {
            _current = was >= 0 && _matches.BinarySearch(was) is var index and >= 0 ? index : -1;
            ShowCurrent(scroll: false);
        }
    }

    private void Step(int direction)
    {
        if (_recount.IsEnabled) FindMatches(landOnNearest: false);
        if (_matches.Count == 0) return;

        _current = _current < 0
            ? NearestMatch(TopInView())
            : (_current + direction + _matches.Count) % _matches.Count;
        ShowCurrent(scroll: true);
    }

    // The first match at or after the offset, else the last one.
    private int NearestMatch(int offset)
    {
        if (_matches.Count == 0) return -1;
        var index = _matches.BinarySearch(offset);
        if (index < 0) index = ~index;
        return Math.Min(index, _matches.Count - 1);
    }

    // Where the topmost line in view starts.
    private int TopInView()
    {
        var view = Output.TextArea.TextView;
        return view.Document == null ? 0 : view.GetDocumentLineByVisualTop(view.ScrollOffset.Y)?.Offset ?? 0;
    }

    private void ShowCurrent(bool scroll)
    {
        var offset = _current >= 0 ? _matches[_current] : -1;
        _marker.Current = offset;
        Output.TextArea.TextView.Redraw();

        MatchNote.Text = SearchBox.Text.Length == 0 ? string.Empty
            : _matches.Count == 0 ? "No matches"
            : _current >= 0 ? $"{_current + 1} of {_matches.Count}"
            : _matches.Count == 1 ? "1 match" : $"{_matches.Count} matches";

        if (!scroll || offset < 0 || !Output.IsLoaded) return;
        var location = Output.Document.GetLocation(offset);
        // Scrolled up to the match, it stays in view while more output comes.
        LetScrollLand();
        Output.ScrollTo(location.Line, location.Column);
    }

    /// <summary>Colors the dashboard's own lines ("[dashboard] ...") apart from the app's output.</summary>
    private sealed class DashboardLineColorizer : DocumentColorizingTransformer
    {
        private const string Prefix = "[dashboard]";

        public Brush Brush { get; set; } = Brushes.Cyan;

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length < Prefix.Length || CurrentContext.Document.GetText(line.Offset, Prefix.Length) != Prefix) return;
            var brush = Brush;
            ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    /// <summary>Marks every place a line has the search text, and the one the arrows are on more strongly.</summary>
    private sealed class SearchMarker : DocumentColorizingTransformer
    {
        public string Term { get; set; } = string.Empty;

        public Brush MatchBrush { get; set; } = Brushes.Transparent;
        public Brush CurrentBrush { get; set; } = Brushes.Transparent;
        public Brush CurrentTextBrush { get; set; } = Brushes.Black;

        /// <summary>Where the match the arrows are on starts; -1 for none.</summary>
        public int Current { get; set; } = -1;

        protected override void ColorizeLine(DocumentLine line)
        {
            var term = Term;
            if (term.Length == 0 || line.Length < term.Length) return;

            var text = CurrentContext.Document.GetText(line);
            Brush match = MatchBrush, current = CurrentBrush, currentText = CurrentTextBrush;
            for (var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = text.IndexOf(term, at + term.Length, StringComparison.OrdinalIgnoreCase))
            {
                var start = line.Offset + at;
                var isCurrent = start == Current;
                ChangeLinePart(start, start + term.Length, element =>
                {
                    element.BackgroundBrush = isCurrent ? current : match;
                    if (isCurrent) element.TextRunProperties.SetForegroundBrush(currentText);
                });
            }
        }
    }
}
