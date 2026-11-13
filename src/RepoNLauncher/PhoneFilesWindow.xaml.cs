using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using RepoNLauncher.Services;
using RepoNLauncher.ViewModels;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragAction = System.Windows.DragAction;
using DragDropEffects = System.Windows.DragDropEffects;
using DragDropKeyStates = System.Windows.DragDropKeyStates;
using DragEventArgs = System.Windows.DragEventArgs;
using IDataObject = System.Windows.IDataObject;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListViewItem = System.Windows.Controls.ListViewItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using QueryContinueDragEventArgs = System.Windows.QueryContinueDragEventArgs;

namespace RepoNLauncher;

/// <summary>
/// A device's files: double-click a folder to go in and a file to open a copy here, drop or paste files from File
/// Explorer or another device's files to send them, and drag or copy them out to File Explorer, the desktop or another
/// device's files, or Save to PC, to copy them off. Beside them, a preview of what's selected. One window per device.
/// </summary>
public partial class PhoneFilesWindow : Window
{
    private static readonly Dictionary<string, PhoneFilesWindow> OpenWindows = new();

    // Edge's own data for the preview, beside the settings folder but in Local, as it's no use on another PC.
    private static readonly string WebViewData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.GetFileName(DashboardConfigService.AppDataDir), "WebView2");

    // The preview keeps the width it was dragged to while it's hidden, and for the next window.
    private static double _previewWidth = 340;

    private readonly PhoneFilesViewModel _viewModel;
    private readonly GridView _details;
    private readonly ItemsPanelTemplate _rows;
    private Task? _webViewReady;

    // Dragging out: where the button went down, and a row pressed among others selected, which is selected alone if it
    // isn't dragged. Then the program the files were dropped on, when it isn't the app.
    private Point? _dragStart;
    private ListViewItem? _pressed;
    private IntPtr _droppedOn;

    // Dragging in: the data dragged, the device files in it, and the folder row they'd be dropped in.
    private IDataObject? _dragData;
    private DeviceFileSet? _dragged;
    private ListViewItem? _dropRow;

    private PhoneFilesWindow(PhoneFilesViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Title = $"{viewModel.Title} - Repo'N'Launcher";
        _details = (GridView)FileList.View;
        _rows = FileList.ItemsPanel;
        ShowSortArrow();
        ShowView();
        ShowPreviewPane();

        SourceInitialized += (_, _) => NativeMethods.UseThemedTitleBar(new WindowInteropHelper(this).Handle);
        ThemeService.Changed += OnThemeChanged;
        Loaded += (_, _) =>
        {
            FileList.Focus();
            _ = viewModel.LoadAsync();
        };
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.Preview.PropertyChanged += OnPreviewChanged;
        viewModel.ConfirmPaste = preview => PasteWindow.Ask(this, preview);
        PreviewKeyDown += (_, e) =>
        {
            // Copying and pasting files, unless a name is being typed.
            var files = Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase;
            if (e.Key == Key.F5) viewModel.RefreshCommand.Execute(null);
            // Alt+P, as in File Explorer.
            else if (e.Key == Key.System && e.SystemKey == Key.P) viewModel.ShowPreview = !viewModel.ShowPreview;
            else if (files && e.Key == Key.C) Run(viewModel.CopyCommand);
            else if (files && e.Key == Key.V) Run(viewModel.PasteCommand);
            else return;
            e.Handled = true;
        };
    }

    /// <summary>Brings the device's files window to the front, opening one if it has none.</summary>
    public static void ShowFor(string serial, Func<PhoneFilesViewModel?> create)
    {
        if (OpenWindows.TryGetValue(serial, out var open))
        {
            NativeMethods.BringToFront(open);
            return;
        }
        if (create() is not { } viewModel) return;

        // Owned by the dashboard, not a popped-out phone screen, so it stays when that's docked back.
        var window = new PhoneFilesWindow(viewModel) { Owner = System.Windows.Application.Current.MainWindow };
        OpenWindows[serial] = window;
        window.Closed += (_, _) => OpenWindows.Remove(serial);
        window.Show();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.ShowPreview) _previewWidth = PreviewColumn.ActualWidth;
        _viewModel.Close();
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.Preview.PropertyChanged -= OnPreviewChanged;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeService.Changed -= OnThemeChanged;
        MediaView.Dispose();
        base.OnClosed(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => MediaView.DefaultBackgroundColor = PanelColor();

    // Bg.Panel, in the WebView's own kind of color
    private System.Drawing.Color PanelColor() =>
        TryFindResource("Bg.Panel") is System.Windows.Media.SolidColorBrush { Color: var c }
            ? System.Drawing.Color.FromArgb(c.R, c.G, c.B)
            : System.Drawing.Color.FromArgb(0x0C, 0x0C, 0x0C);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PhoneFilesViewModel.Naming) when _viewModel.IsNaming:
                Dispatcher.BeginInvoke(() =>
                {
                    NameBox.Focus();
                    // The name without its extension, ready to type over, as File Explorer does.
                    var extension = _viewModel.Naming == FileNaming.Rename ? Path.GetExtension(NameBox.Text) : "";
                    NameBox.Select(0, NameBox.Text.Length - (extension.Length < NameBox.Text.Length ? extension.Length : 0));
                }, DispatcherPriority.Input);
                break;
            case nameof(PhoneFilesViewModel.Naming):
                FileList.Focus();
                break;
            case nameof(PhoneFilesViewModel.Sort) or nameof(PhoneFilesViewModel.SortDescending):
                ShowSortArrow();
                break;
            case nameof(PhoneFilesViewModel.View):
                ShowView();
                break;
            case nameof(PhoneFilesViewModel.ShowPreview):
                ShowPreviewPane();
                break;
            // Another folder starts at its top, once its files are in the list.
            case nameof(PhoneFilesViewModel.Folder):
                Dispatcher.BeginInvoke(() =>
                {
                    if (FileList.Items.Count > 0) FileList.ScrollIntoView(FileList.Items[0]);
                }, DispatcherPriority.Loaded);
                break;
        }
    }

    private void OnPreviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhoneFilePreviewViewModel.MediaFile)) _ = ShowMediaAsync(_viewModel.Preview.MediaFile);
    }

    // Details are the list's GridView. The icon view has none, so the list shows its tiles in the panel and template
    // they're given.
    private void ShowView()
    {
        var icons = _viewModel.View == FileView.Icons;
        FileList.View = icons ? null : _details;
        FileList.ItemsPanel = icons ? (ItemsPanelTemplate)Resources["TilePanel"] : _rows;
        FileList.ItemTemplate = icons ? (DataTemplate)Resources["FileTile"] : null;
        FileList.ItemContainerStyle = (Style)Resources[icons ? "FileTileItem" : "FileRow"];
        ScrollViewer.SetHorizontalScrollBarVisibility(FileList, icons ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);

        // What was selected stays in view.
        Dispatcher.BeginInvoke(() =>
        {
            if (FileList.SelectedItem != null) FileList.ScrollIntoView(FileList.SelectedItem);
        }, DispatcherPriority.Loaded);
    }

    private void ShowPreviewPane()
    {
        var show = _viewModel.ShowPreview;
        if (!show && PreviewColumn.ActualWidth > 0) _previewWidth = PreviewColumn.ActualWidth;
        PreviewColumn.MinWidth = show ? 220 : 0;
        PreviewColumn.Width = new GridLength(show ? _previewWidth : 0);
        SplitterColumn.Width = new GridLength(show ? 14 : 0);
        PreviewSplitter.Visibility = PreviewPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // Shows the copy of a video or PDF in Edge; null empties it, stopping a video that's playing.
    private async Task ShowMediaAsync(string? file)
    {
        if (file == null)
        {
            MediaView.Visibility = Visibility.Collapsed;
            MediaView.CoreWebView2?.Navigate("about:blank");
            return;
        }

        try
        {
            await (_webViewReady ??= StartWebViewAsync());
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or ExternalException or IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidOperationException)
        {
            _viewModel.Preview.MediaFailed(e is WebView2RuntimeNotFoundException
                ? "Previewing videos and PDFs needs Microsoft Edge WebView2, which this PC doesn't have. Double-click it to open it instead."
                : $"Couldn't preview it here: {e.Message.TrimEnd('.')}. Double-click it to open it instead.");
            return;
        }

        if (_viewModel.Preview.MediaFile != file) return; // another was selected meanwhile
        MediaView.CoreWebView2.Navigate(new Uri(file).AbsoluteUri);
        MediaView.Visibility = Visibility.Visible;
    }

    private async Task StartWebViewAsync()
    {
        MediaView.DefaultBackgroundColor = PanelColor(); // so it doesn't flash another color
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: WebViewData);
        await MediaView.EnsureCoreWebView2Async(environment);
        var web = MediaView.CoreWebView2;
        web.Settings.AreDevToolsEnabled = false;
        web.Settings.IsStatusBarEnabled = false;
        // It shows the copy and nothing else: a link in a PDF opens in the browser.
        web.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || e.Uri == "about:blank") return;
            e.Cancel = true;
            OpenInBrowser(e.Uri);
        };
        web.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenInBrowser(e.Uri);
        };
    }

    private static void OpenInBrowser(string uri)
    {
        if (!uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return;
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    // The icon view makes only the tiles in view, so a tile being made is one to show a thumbnail on.
    private void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PhoneFileItem item }) _viewModel.Thumbnails.Show(item);
    }

    private void Tile_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PhoneFileItem item }) _viewModel.Thumbnails.Hide(item);
    }

    private void ShowSortArrow()
    {
        var arrow = _viewModel.SortDescending ? "  ↓" : "  ↑";
        NameHeader.Content = "Name" + (_viewModel.Sort == FileSort.Name ? arrow : "");
        SizeHeader.Content = "Size" + (_viewModel.Sort == FileSort.Size ? arrow : "");
        ModifiedHeader.Content = "Modified" + (_viewModel.Sort == FileSort.Modified ? arrow : "");
    }

    // The name takes the room the other columns leave, so there's no scrolling sideways.
    private void FileList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (FileList.View is not GridView details) return;
        var others = details.Columns.Where(c => c != NameColumn).Sum(c => c.ActualWidth);
        NameColumn.Width = Math.Max(160, FileList.ActualWidth - others - SystemParameters.VerticalScrollBarWidth - 12);
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.Selection = FileList.SelectedItems.Cast<PhoneFileItem>().ToList();

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // A row, not the column headers or the scroll bar.
        if (ItemsControl.ContainerFromElement(FileList, (DependencyObject)e.OriginalSource) is not ListViewItem { Content: PhoneFileItem item }) return;
        if (_viewModel.OpenItemCommand.CanExecute(item)) _viewModel.OpenItemCommand.Execute(item);
        e.Handled = true;
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _viewModel.OpenSelectedCommand.Execute(null);
                break;
            case Key.Back:
                if (_viewModel.UpCommand.CanExecute(null)) _viewModel.UpCommand.Execute(null);
                break;
            case Key.Delete:
                if (_viewModel.DeleteCommand.CanExecute(null)) _viewModel.DeleteCommand.Execute(null);
                break;
            case Key.F2:
                if (_viewModel.StartRenameCommand.CanExecute(null)) _viewModel.StartRenameCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void FileList_HeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Tag: FileSort sort }) _viewModel.SortByCommand.Execute(sort);
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.ConfirmNameCommand.CanExecute(null)) _viewModel.ConfirmNameCommand.Execute(null);
        else if (e.Key == Key.Escape) _viewModel.CancelNameCommand.Execute(null);
        else return;
        e.Handled = true;
    }

    private static void Run(ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    // Pressing one of several selected rows keeps them all selected, so they can all be dragged, as in File Explorer.
    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _pressed = null;
        if (ItemsControl.ContainerFromElement(FileList, (DependencyObject)e.OriginalSource) is not ListViewItem row) return;
        _dragStart = e.GetPosition(FileList);
        if (e.ClickCount == 1 && row.IsSelected && FileList.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _pressed = row;
            e.Handled = true;
        }
    }

    private void FileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressed is { Content: PhoneFileItem item } row)
        {
            FileList.SelectedItem = item;
            row.Focus();
        }
        _pressed = null;
        _dragStart = null;
    }

    private void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var moved = e.GetPosition(FileList) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        _pressed = null;
        DragOut();
    }

    // What's selected goes as device files, for another device's files (or this one's) to paste, and with their names
    // for File Explorer, so it shows it would copy them.
    private void DragOut()
    {
        var items = _viewModel.Selection.ToList();
        if (items.Count == 0) return;
        var data = new DataObject();
        new DeviceFileSet(_viewModel.Device.Serial, _viewModel.Device.Model, _viewModel.Folder, items.Select(i => i.File).ToList()).AddTo(data);
        data.SetData("FileGroupDescriptorW", WindowsShell.FileDescriptors(items.Select(i => (i.Name, i.IsFolder)).ToList()));
        data.SetData("FileContents", new MemoryStream());

        _droppedOn = IntPtr.Zero;
        DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy);
        if (_droppedOn != IntPtr.Zero) _ = _viewModel.DropOnPcAsync(items, WindowsShell.FolderOf(_droppedOn));
    }

    // Dropped on another program, the drop is called off, and the app copies the files itself once it's asked: File
    // Explorer would want the files from the drag, and they're still on the device.
    private void FileList_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed) e.Action = DragAction.Cancel;
        else if ((e.KeyStates & DragDropKeyStates.LeftMouseButton) != 0) e.Action = DragAction.Continue;
        else e.Action = (_droppedOn = WindowsShell.OtherWindowAtPointer()) != IntPtr.Zero ? DragAction.Cancel : DragAction.Drop;
        e.Handled = true;
    }

    private void Files_DragOver(object sender, DragEventArgs e)
    {
        var folder = DropFolder(e, out var row);
        ShowDropRow(row);
        e.Effects = folder != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Files_DragLeave(object sender, DragEventArgs e) => ShowDropRow(null);

    // Asked about once the drop is over, so the program the files came from (File Explorer, say) isn't left waiting.
    private void Files_Drop(object sender, DragEventArgs e)
    {
        var folder = DropFolder(e, out _);
        var set = _dragged;
        var paths = set == null ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
        ShowDropRow(null);
        _dragData = null;
        _dragged = null;
        e.Handled = true;
        if (folder == null) return;
        Dispatcher.BeginInvoke(() => _ = set != null
            ? _viewModel.PasteFromDeviceAsync(set, folder)
            : _viewModel.PasteFromPcAsync(paths ?? [], folder));
    }

    // Where files dropped here go: into the folder row they're over, or else the folder shown. Null when they can't be
    // dropped: they aren't files, something's under way, or they'd go back into the folder they're from.
    private string? DropFolder(DragEventArgs e, out ListViewItem? row)
    {
        row = null;
        if (!ReferenceEquals(e.Data, _dragData))
        {
            _dragData = e.Data;
            _dragged = DeviceFileSet.From(e.Data);
        }
        if (_viewModel.IsBusy || _dragged == null && !e.Data.GetDataPresent(DataFormats.FileDrop)) return null;

        var fromHere = _dragged?.Serial == _viewModel.Device.Serial;
        if (ItemsControl.ContainerFromElement(FileList, (DependencyObject)e.OriginalSource) is ListViewItem { Content: PhoneFileItem { IsFolder: true } folder } over
            && !(fromHere && _dragged!.Files.Any(f => f.Path == folder.File.Path)))
        {
            row = over;
            return folder.File.Path;
        }
        return fromHere && _dragged!.Folder == _viewModel.Folder ? null : _viewModel.Folder;
    }

    // The folder row files would be dropped in is lit.
    private void ShowDropRow(ListViewItem? row)
    {
        if (row == _dropRow) return;
        _dropRow?.ClearValue(BackgroundProperty);
        _dropRow = row;
        row?.SetResourceReference(BackgroundProperty, "Bg.Hover");
    }
}
