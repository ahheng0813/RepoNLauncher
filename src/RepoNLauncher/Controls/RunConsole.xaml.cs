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
using RepoNLauncher.ViewModels;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace RepoNLauncher.Controls;

/// <summary>
/// A project's Run console. The output is text that can be selected with the mouse and copied, like a terminal's, and
/// searched: every match is marked, and the arrows step through them. Like a terminal, it follows new output while it's
/// scrolled to the bottom, so a prompt is in view when it asks; scrolled up to read, it stays put.
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

    private readonly SearchMarker _marker;

    // New output while searching moves the count on, at most this often rather than at every line.
    private readonly DispatcherTimer _recount = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private ObservableCollection<string>? _lines;

    // The lines shown, and where the last one starts: a partial line (a prompt) is replaced as more of it comes.
    private int _lineCount;
    private int _lastLineStart;

    // Where the search text is, in order, and which of them the arrows are on (-1: none yet).
    private readonly List<int> _matches = new();
    private int _current = -1;

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
        area.SelectionBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0xCC, 0xCC, 0xCC)));
        area.SelectionForeground = null;
        area.SelectionBorder = null;
        area.SelectionCornerRadius = 0;

        var view = area.TextView;
        view.LinkTextForegroundBrush = Resource("Cyan", Brushes.Cyan);
        view.LineTransformers.Add(new DashboardLineColorizer(Resource("Cyan", Brushes.Cyan)));
        var warning = (Resource("Warning", Brushes.Gold) as SolidColorBrush)?.Color ?? Colors.Gold;
        _marker = new SearchMarker(
            Frozen(new SolidColorBrush(Color.FromArgb(0x60, warning.R, warning.G, warning.B))),
            Frozen(new SolidColorBrush(warning)),
            Resource("Bg.App", Brushes.Black));
        view.LineTransformers.Add(_marker);

        Output.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Output_ScrollChanged));
        Output.AddHandler(System.Windows.Documents.Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(Output_LinkClicked));

        _recount.Tick += (_, _) => FindMatches(landOnNearest: false);
        DataContextChanged += (_, _) => Attach((DataContext as ProjectViewModel)?.ConsoleOutput);
    }

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
        Output.ScrollToEnd();
    }

    // Lines come split already; a line break inside one would put the lines shown out of step with the console's.
    private static string OneLine(string line) =>
        line.AsSpan().IndexOfAny('\r', '\n') < 0 ? line : line.Replace('\r', ' ').Replace('\n', ' ');

    private void Output_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange <= 0 || e.OriginalSource is not ScrollViewer viewer) return;
        var wasAtBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - e.ExtentHeightChange - 1;
        if (wasAtBottom) viewer.ScrollToEnd();
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
        Output.ScrollTo(location.Line, location.Column);
    }

    /// <summary>Colors the dashboard's own lines ("[dashboard] ...") apart from the app's output.</summary>
    private sealed class DashboardLineColorizer(Brush brush) : DocumentColorizingTransformer
    {
        private const string Prefix = "[dashboard]";

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length < Prefix.Length || CurrentContext.Document.GetText(line.Offset, Prefix.Length) != Prefix) return;
            ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    /// <summary>Marks every place a line has the search text, and the one the arrows are on more strongly.</summary>
    private sealed class SearchMarker(Brush match, Brush current, Brush currentText) : DocumentColorizingTransformer
    {
        public string Term { get; set; } = string.Empty;

        /// <summary>Where the match the arrows are on starts; -1 for none.</summary>
        public int Current { get; set; } = -1;

        protected override void ColorizeLine(DocumentLine line)
        {
            var term = Term;
            if (term.Length == 0 || line.Length < term.Length) return;

            var text = CurrentContext.Document.GetText(line);
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
