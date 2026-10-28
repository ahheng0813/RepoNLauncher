using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using MdBlock = Markdig.Syntax.Block;
using MdCell = Markdig.Extensions.Tables.TableCell;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdTable = Markdig.Extensions.Tables.Table;
using RichTextBox = System.Windows.Controls.RichTextBox;
using WpfBlock = System.Windows.Documents.Block;
using WpfCell = System.Windows.Documents.TableCell;
using WpfInline = System.Windows.Documents.Inline;
using WpfRow = System.Windows.Documents.TableRow;
using WpfTable = System.Windows.Documents.Table;

namespace RepoNLauncher.Controls;

/// <summary>
/// Markdown, shown as a read-only rich text box so it can be selected and copied: headings, lists, code, quotes,
/// tables and links. Renders when shown, so a reply streaming into a hidden copy isn't re-rendered on every word.
/// </summary>
public sealed class MarkdownBlock : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownBlock), new PropertyMetadata(null, (d, _) => ((MarkdownBlock)d).OnMarkdownChanged()));

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseTaskLists().UseAutoLinks().UseEmphasisExtras().Build();

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private bool _stale;

    public MarkdownBlock()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        IsUndoEnabled = false;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        FocusVisualStyle = null;
        SelectionBrush = Brush("Accent");
        Document = new FlowDocument();
        AddHandler(Hyperlink.RequestNavigateEvent, new System.Windows.Navigation.RequestNavigateEventHandler(OnNavigate));
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _stale) Render();
        };
    }

    public string? Markdown
    {
        get => (string?)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void OnMarkdownChanged()
    {
        if (IsVisible) Render();
        else _stale = true;
    }

    private void Render()
    {
        _stale = false;
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13.5,
            Foreground = Brush("Text.Primary"),
            LineHeight = 21,
        };
        foreach (var block in Markdig.Markdown.Parse(Markdown ?? string.Empty, Pipeline))
            if (Convert(block, tight: false) is { } converted) document.Blocks.Add(converted);
        if (document.Blocks.LastBlock is { } last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
        Document = document;
    }

    // The inner scroll viewer swallows the wheel even with nothing to scroll; pass it on to the chat's list.
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        if (VisualTreeHelper.GetParent(this) is UIElement parent)
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this });
    }

    private static void OnNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        if (e.Uri.IsAbsoluteUri && (e.Uri.Scheme == Uri.UriSchemeHttp || e.Uri.Scheme == Uri.UriSchemeHttps))
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private static WpfBlock? Convert(MdBlock block, bool tight)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var title = new Paragraph { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6) };
                title.FontSize = heading.Level switch { 1 => 19, 2 => 16.5, 3 => 14.5, _ => 13.5 };
                AddInlines(title.Inlines, heading.Inline);
                return title;

            case ParagraphBlock paragraph:
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, tight ? 3 : 10) };
                AddInlines(p.Inlines, paragraph.Inline);
                return p;

            case ListBlock list:
                var wpfList = new List
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, tight ? 3 : 10),
                    Padding = new Thickness(22, 0, 0, 0),
                };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start)) wpfList.StartIndex = start;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var listItem = new ListItem();
                    foreach (var child in item)
                        if (Convert(child, tight: true) is { } converted) listItem.Blocks.Add(converted);
                    wpfList.ListItems.Add(listItem);
                }
                return wpfList;

            case CodeBlock code:
                var codeParagraph = new Paragraph
                {
                    FontFamily = Mono,
                    FontSize = 12.5,
                    LineHeight = 18,
                    Background = Brush("Bg.Console"),
                    BorderBrush = Brush("Border.Subtle"),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 9, 12, 9),
                    Margin = new Thickness(0, 2, 0, tight ? 4 : 12),
                };
                AddLines(codeParagraph.Inlines, code.Lines.ToString());
                return codeParagraph;

            case QuoteBlock quote:
                var section = new Section
                {
                    BorderBrush = Brush("Border.Strong"),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 0, 0, 0),
                    Margin = new Thickness(0, 0, 0, 10),
                    Foreground = Brush("Text.Secondary"),
                };
                foreach (var child in quote)
                    if (Convert(child, tight: true) is { } converted) section.Blocks.Add(converted);
                return section;

            case ThematicBreakBlock:
                return new Paragraph
                {
                    BorderBrush = Brush("Border.Subtle"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 4, 0, 14),
                    FontSize = 2,
                };

            case MdTable table:
                return Table(table);

            case HtmlBlock html:
                var raw = new Paragraph { Margin = new Thickness(0, 0, 0, 10), FontFamily = Mono, FontSize = 12.5 };
                AddLines(raw.Inlines, html.Lines.ToString());
                return raw;

            case LeafBlock leaf:
                var other = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                if (leaf.Inline != null) AddInlines(other.Inlines, leaf.Inline);
                else AddLines(other.Inlines, leaf.Lines.ToString());
                return other;

            case ContainerBlock container:
                var group = new Section();
                foreach (var child in container)
                    if (Convert(child, tight) is { } converted) group.Blocks.Add(converted);
                return group;
        }
        return null;
    }

    private static WpfTable Table(MdTable table)
    {
        var columns = table.OfType<MdRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
        var result = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 12), BorderBrush = Brush("Border.Subtle"), BorderThickness = new Thickness(1, 1, 0, 0) };
        for (var i = 0; i < columns; i++) result.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        foreach (var row in table.OfType<MdRow>())
        {
            var wpfRow = new WpfRow();
            if (row.IsHeader)
            {
                wpfRow.FontWeight = FontWeights.SemiBold;
                wpfRow.Background = Brush("Bg.PanelAlt");
            }
            foreach (var cell in row.OfType<MdCell>())
            {
                var wpfCell = new WpfCell
                {
                    Padding = new Thickness(8, 4, 8, 4),
                    BorderBrush = Brush("Border.Subtle"),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                };
                foreach (var child in cell)
                    if (Convert(child, tight: true) is { } converted)
                    {
                        converted.Margin = new Thickness(0);
                        wpfCell.Blocks.Add(converted);
                    }
                wpfRow.Cells.Add(wpfCell);
            }
            group.Rows.Add(wpfRow);
        }
        result.RowGroups.Add(group);
        return result;
    }

    private static void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container == null) return;
        foreach (var inline in container)
            if (Convert(inline) is { } converted) target.Add(converted);
    }

    private static WpfInline? Convert(MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                return new Run(literal.Content.ToString());

            case EmphasisInline emphasis:
                Span span = (emphasis.DelimiterChar, emphasis.DelimiterCount) switch
                {
                    ('~', 2) => new Span { TextDecorations = TextDecorations.Strikethrough },
                    ('~', 1) => new Span { BaselineAlignment = BaselineAlignment.Subscript, FontSize = 11 },
                    ('^', _) => new Span { BaselineAlignment = BaselineAlignment.Superscript, FontSize = 11 },
                    ('=', _) => new Span { Background = Brush("Accent.Muted") },
                    (_, >= 2) => new Bold(),
                    _ => new Italic(),
                };
                AddInlines(span.Inlines, emphasis);
                return span;

            case CodeInline code:
                return new Run(code.Content)
                {
                    FontFamily = Mono,
                    FontSize = 12.5,
                    Background = Brush("Bg.Hover"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xB0, 0x7D)),
                };

            case LinkInline { IsImage: true } image:
                return new Run($"[image: {image.Url}]") { Foreground = Brush("Text.Secondary") };

            case LinkInline link:
                var hyperlink = new Hyperlink { Foreground = Brush("Cyan"), ToolTip = link.Url };
                if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri)) hyperlink.NavigateUri = uri;
                AddInlines(hyperlink.Inlines, link);
                if (hyperlink.Inlines.Count == 0) hyperlink.Inlines.Add(new Run(link.Url));
                return hyperlink;

            case AutolinkInline autolink:
                var auto = new Hyperlink(new Run(autolink.Url)) { Foreground = Brush("Cyan") };
                if (Uri.TryCreate(autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url, UriKind.Absolute, out var autoUri)) auto.NavigateUri = autoUri;
                return auto;

            case LineBreakInline lineBreak:
                return lineBreak.IsHard ? new LineBreak() : new Run(" ");

            case TaskList task:
                return new Run(task.Checked ? "☑ " : "☐ ");

            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());

            case HtmlInline html:
                return new Run(html.Tag) { Foreground = Brush("Text.Secondary") };

            case ContainerInline container:
                var group = new Span();
                AddInlines(group.Inlines, container);
                return group;

            default:
                return new Run(inline.ToString());
        }
    }

    // Code keeps its line breaks: one run per line.
    private static void AddLines(InlineCollection target, string text)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) target.Add(new LineBreak());
            target.Add(new Run(lines[i]));
        }
    }

    private static Brush Brush(string key) => (Brush)System.Windows.Application.Current.Resources[key];
}
