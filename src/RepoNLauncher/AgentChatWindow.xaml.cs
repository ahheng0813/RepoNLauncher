using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using RepoNLauncher.Services;
using RepoNLauncher.ViewModels;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace RepoNLauncher;

/// <summary>
/// Chats with Claude Code and Codex: saved chats on the left, the open chat on the right. The prompt box takes @ file
/// mentions, pasted or dropped images and files, and sends on Enter.
/// </summary>
public partial class AgentChatWindow : Window
{
    private readonly AgentChatViewModel _vm;
    private ScrollViewer? _scroller;
    private bool _stickToBottom = true;
    private int _mentionStart = -1;
    private Rect _mentionAt;
    private UserChatItem? _pinned;

    public AgentChatWindow(AgentChatViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;

        SourceInitialized += (_, _) => NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
        Activated += (_, _) => _vm.OnActivated();
        Loaded += (_, _) =>
        {
            _scroller = Transcript.Template.FindName("Scroller", Transcript) as ScrollViewer;
            if (_scroller != null) _scroller.ScrollChanged += Scroller_ScrollChanged;
            ScrollToEnd();
            FocusPrompt();
        };
        _vm.PropertyChanged += OnViewModelChanged;
        Closed += (_, _) => _vm.PropertyChanged -= OnViewModelChanged;

        // Under the Prompts button, right edges lined up: it sits at the right of the header, so the list opens inward.
        PromptsPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(PromptsButton);
            return [new CustomPopupPlacement(
                new System.Windows.Point(targetSize.Width - popupSize.Width, targetSize.Height + 6 * dpi.DpiScaleY),
                PopupPrimaryAxis.Horizontal)];
        };

        // Just above the @ being typed. Placed by hand: Top placement follows the pen-tablet "right-handed" setting,
        // which flips popups to the left. The callback works in device pixels.
        MentionPopup.CustomPopupPlacementCallback = (popupSize, _, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(Prompt);
            return [new CustomPopupPlacement(
                new System.Windows.Point(_mentionAt.X * dpi.DpiScaleX, _mentionAt.Y * dpi.DpiScaleY - popupSize.Height),
                PopupPrimaryAxis.Horizontal)];
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AgentChatViewModel.Current)) return;
        CloseMention();
        PromptsPopup.IsOpen = false;
        ShowPinnedPrompt(null);
        _stickToBottom = true;
        Dispatcher.InvokeAsync(() =>
        {
            ScrollToEnd();
            FocusPrompt();
        }, DispatcherPriority.Loaded);
    }

    // Follow new messages while the chat is scrolled to the bottom; stop following once the user scrolls up.
    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_scroller == null) return;
        if (e.ExtentHeightChange == 0) _stickToBottom = _scroller.VerticalOffset >= _scroller.ScrollableHeight - 40;
        else if (_stickToBottom) _scroller.ScrollToEnd();

        JumpToLatest.Visibility = !_stickToBottom && _scroller.ScrollableHeight - _scroller.VerticalOffset > 200
            ? Visibility.Visible
            : Visibility.Collapsed;
        ShowPinnedPrompt(PromptAboveView());
    }

    // ===== Prompts: the pinned prompt, the prompts list, and going to one =====

    /// <summary>
    /// The prompt that the part of the chat at the top of the view answers, when that prompt itself has scrolled off the
    /// top; null when it's still in view or nothing in view follows a prompt.
    /// </summary>
    private UserChatItem? PromptAboveView()
    {
        if (_scroller == null || _vm.Current is not { } chat || TranscriptPanel() is not { } panel) return null;

        // The item under the top edge (just below the scroller's padding); with a gap there, the first one below it.
        var probe = _scroller.Padding.Top + 2;
        int topIndex = -1;
        var bestTop = double.MaxValue;
        foreach (UIElement child in panel.Children)
        {
            var top = child.TransformToAncestor(_scroller).Transform(default).Y;
            var bottom = top + child.RenderSize.Height;
            if (bottom <= probe || top >= bestTop) continue;
            bestTop = top;
            topIndex = Transcript.ItemContainerGenerator.IndexFromContainer(child);
        }
        if (topIndex < 0 || topIndex >= chat.Items.Count) return null;

        // A prompt at the top is in view itself; one further up has scrolled off.
        if (chat.Items[topIndex] is UserChatItem) return null;
        for (var i = topIndex - 1; i >= 0; i--)
            if (chat.Items[i] is UserChatItem prompt) return prompt;
        return null;
    }

    private void ShowPinnedPrompt(UserChatItem? prompt)
    {
        if (prompt == _pinned) return;
        _pinned = prompt;
        PinnedPrompt.Visibility = prompt == null ? Visibility.Collapsed : Visibility.Visible;
        if (prompt == null || _vm.Current is not { } chat) return;

        var prompts = chat.Items.OfType<UserChatItem>().ToList();
        PinnedText.Text = prompt.Preview;
        PinnedNumber.Text = $"{prompts.IndexOf(prompt) + 1} of {prompts.Count}";
    }

    private void PinnedPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (_pinned != null) GoTo(_pinned);
    }

    private void JumpToLatest_Click(object sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        ScrollToEnd();
    }

    private void PromptsButton_Click(object sender, RoutedEventArgs e)
    {
        var current = _pinned ?? PromptAtTop();
        var prompts = (_vm.Current?.Items.OfType<UserChatItem>() ?? [])
            .Select((prompt, i) => new PromptLink(i + 1, prompt, prompt == current))
            .ToList();
        PromptsHeading.Text = prompts.Count switch
        {
            0 => "No prompts in this chat yet",
            1 => "1 prompt · pick it to go to it",
            _ => $"{prompts.Count} prompts · pick one to go to it",
        };
        PromptList.ItemsSource = prompts;
        PromptsPopup.IsOpen = true;

        // The list opens where you are: on the prompt being read, else the latest, with the keyboard on it.
        var here = prompts.FirstOrDefault(p => p.IsCurrent) ?? prompts.LastOrDefault();
        if (here == null) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (PromptList.ItemContainerGenerator.ContainerFromItem(here) is not ContentPresenter row ||
                FindChild<System.Windows.Controls.Button>(row) is not { } button) return;
            button.BringIntoView();
            button.Focus();
        }, DispatcherPriority.Loaded);
    }

    // With no pinned prompt, a prompt at the top of the view is the one being read.
    private UserChatItem? PromptAtTop()
    {
        if (_scroller == null || TranscriptPanel() is not { } panel) return null;
        return panel.Children.OfType<FrameworkElement>()
            .Where(c => c.TransformToAncestor(_scroller).Transform(default).Y + c.RenderSize.Height > _scroller.Padding.Top)
            .Select(c => c.DataContext)
            .OfType<UserChatItem>()
            .FirstOrDefault();
    }

    private void PromptLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PromptLink link) return;
        PromptsPopup.IsOpen = false;
        GoTo(link.Item);
    }

    /// <summary>Scrolls the chat so <paramref name="item"/> sits at the top.</summary>
    private void GoTo(ChatItemViewModel item)
    {
        if (_scroller == null || _vm.Current is not { } chat || TranscriptPanel() is not { } panel) return;
        var index = chat.Items.IndexOf(item);
        if (index < 0) return;

        _stickToBottom = false;
        panel.BringIndexIntoViewPublic(index);
        // That only brings it into view somewhere. Line its top up with the top once layout has caught up, and again
        // after: items measured for the first time on the way can still move it.
        Dispatcher.InvokeAsync(() => AlignTop(index), DispatcherPriority.Loaded);
        Dispatcher.InvokeAsync(() => AlignTop(index), DispatcherPriority.Background);
    }

    private void AlignTop(int index)
    {
        if (_scroller == null || Transcript.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container) return;
        var top = container.TransformToAncestor(_scroller).Transform(default).Y;
        _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + top - _scroller.Padding.Top);
    }

    private VirtualizingStackPanel? TranscriptPanel() => FindChild<VirtualizingStackPanel>(Transcript);

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    // Text boxes inside the chat (replies, tool output) take the wheel even with nothing to scroll. Unless one of them
    // can still scroll that way, the wheel scrolls the chat.
    private void Transcript_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_scroller == null) return;
        for (var element = e.OriginalSource as DependencyObject; element != null && element != _scroller; element = ParentOf(element))
        {
            if (element is ScrollViewer inner && inner.ScrollableHeight > 0 &&
                (e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight))
                return;
        }
        _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset - e.Delta * 0.6);
        e.Handled = true;
    }

    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    private void ScrollToEnd() => _scroller?.ScrollToEnd();

    private void FocusPrompt()
    {
        Prompt.Focus();
        Prompt.CaretIndex = Prompt.Text.Length;
    }

    private void Prompt_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (MentionPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    MoveMention(1);
                    e.Handled = true;
                    return;
                case Key.Up:
                    MoveMention(-1);
                    e.Handled = true;
                    return;
                case Key.Enter or Key.Tab:
                    if (MentionList.SelectedItem is string path) InsertMention(path);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    CloseMention();
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (_vm.Current?.SendCommand.CanExecute(null) == true) _vm.Current.SendCommand.Execute(null);
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && PasteAttachments())
        {
            e.Handled = true;
        }
    }

    // Files and pictures on the clipboard become attachments; text pastes as usual.
    private bool PasteAttachments()
    {
        if (_vm.Current is not { } chat) return false;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                chat.AddAttachments(Clipboard.GetFileDropList().Cast<string>());
                return true;
            }
            if (Clipboard.ContainsText() || !Clipboard.ContainsImage()) return false;

            var dir = Path.Combine(Path.GetTempPath(), "RepoNLauncher", "pasted");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"pasted-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            // Prefer the PNG the snipping tools put on the clipboard: the bitmap form can lose its transparency.
            if (Clipboard.GetData("PNG") is MemoryStream png)
            {
                File.WriteAllBytes(path, png.ToArray());
            }
            else if (Clipboard.GetImage() is { } image)
            {
                using var file = File.Create(path);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(file);
            }
            else
            {
                return false;
            }
            chat.AddAttachments([path]);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Composer_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Composer_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        _vm.Current?.AddAttachments(files);
        e.Handled = true;
        FocusPrompt();
    }

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach images or files",
            Multiselect = true,
            Filter = "All files|*.*|Images|*.png;*.jpg;*.jpeg;*.gif;*.webp",
        };
        if (dialog.ShowDialog(this) == true) _vm.Current?.AddAttachments(dialog.FileNames);
        FocusPrompt();
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Current is not { IsStarted: false } chat) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the folder this chat works in",
            InitialDirectory = Directory.Exists(chat.WorkingDirectory) ? chat.WorkingDirectory : string.Empty,
        };
        if (dialog.ShowDialog(this) != true) return;
        _vm.EnsureProject(dialog.FolderName);
        chat.WorkingDirectory = dialog.FolderName;
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // ===== @ mentions =====

    private void Mention_Click(object sender, RoutedEventArgs e)
    {
        var caret = Prompt.CaretIndex;
        var insert = caret > 0 && !char.IsWhiteSpace(Prompt.Text[caret - 1]) ? " @" : "@";
        Prompt.Focus();
        Prompt.Text = Prompt.Text.Insert(caret, insert);
        Prompt.CaretIndex = caret + insert.Length;
    }

    private async void Prompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        var (start, _) = MentionAtCaret();
        if (start < 0 || _vm.Current is not { } chat)
        {
            CloseMention();
            return;
        }

        var files = await _vm.Files.GetAsync(chat.WorkingDirectory);
        var (now, query) = MentionAtCaret();
        if (now != start) return; // typing moved on while the file list loaded

        var matches = ProjectFileIndex.Match(files, query);
        _mentionStart = start;
        MentionList.ItemsSource = matches;
        if (matches.Count == 0)
        {
            MentionPopup.IsOpen = false;
            return;
        }
        MentionList.SelectedIndex = 0;
        MentionList.ScrollIntoView(matches[0]);
        _mentionAt = Prompt.GetRectFromCharacterIndex(start);
        MentionPopup.IsOpen = false; // reopening re-runs the placement for where the @ is now
        MentionPopup.IsOpen = true;
    }

    /// <summary>Where the @ being typed starts, and what follows it; -1 when the caret isn't in a mention.</summary>
    private (int Start, string Query) MentionAtCaret()
    {
        var text = Prompt.Text;
        var caret = Prompt.CaretIndex;
        for (var i = caret - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c == '@') return i == 0 || char.IsWhiteSpace(text[i - 1]) ? (i, text[(i + 1)..caret]) : (-1, string.Empty);
            if (char.IsWhiteSpace(c) || c == '"') break;
        }
        return (-1, string.Empty);
    }

    private void MoveMention(int by)
    {
        if (MentionList.Items.Count == 0) return;
        MentionList.SelectedIndex = Math.Clamp(MentionList.SelectedIndex + by, 0, MentionList.Items.Count - 1);
        MentionList.ScrollIntoView(MentionList.SelectedItem);
    }

    private void MentionList_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is string path) InsertMention(path);
    }

    // Claude Code reads @path (or @"path with spaces") relative to the chat's folder; Codex sees the path and opens it.
    private void InsertMention(string path)
    {
        var text = Prompt.Text;
        var caret = Prompt.CaretIndex;
        if (_mentionStart < 0 || _mentionStart >= text.Length || text[_mentionStart] != '@' || caret < _mentionStart)
        {
            CloseMention();
            return;
        }
        var mention = path.Contains(' ') ? $"@\"{path}\" " : $"@{path} ";
        var start = _mentionStart;
        CloseMention();
        Prompt.Text = text[..start] + mention + text[caret..];
        Prompt.CaretIndex = start + mention.Length;
        Prompt.Focus();
    }

    private void CloseMention()
    {
        _mentionStart = -1;
        MentionPopup.IsOpen = false;
    }
}
