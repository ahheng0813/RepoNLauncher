using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using RepoNLauncher.ViewModels;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListViewItem = System.Windows.Controls.ListViewItem;

namespace RepoNLauncher;

/// <summary>
/// A device's files: double-click a folder to go in and a file to open a copy here, drop files from File Explorer to send
/// them, and Save to PC to copy them off. One window per device.
/// </summary>
public partial class PhoneFilesWindow : Window
{
    private static readonly Dictionary<string, PhoneFilesWindow> OpenWindows = new();

    private readonly PhoneFilesViewModel _viewModel;

    private PhoneFilesWindow(PhoneFilesViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Title = $"{viewModel.Title} - Repo'N'Launcher";
        ShowSortArrow();

        SourceInitialized += (_, _) => NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            FileList.Focus();
            _ = viewModel.LoadAsync();
        };
        viewModel.PropertyChanged += OnViewModelChanged;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F5) return;
            viewModel.RefreshCommand.Execute(null);
            e.Handled = true;
        };
    }

    /// <summary>Brings the device's files window to the front, opening one if it has none.</summary>
    public static void ShowFor(string serial, Func<PhoneFilesViewModel?> create)
    {
        if (OpenWindows.TryGetValue(serial, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
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
        _viewModel.Close();
        _viewModel.PropertyChanged -= OnViewModelChanged;
        base.OnClosing(e);
    }

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
        }
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
        var others = ((GridView)FileList.View).Columns.Where(c => c != NameColumn).Sum(c => c.ActualWidth);
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

    private void Files_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !_viewModel.IsBusy ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Files_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = _viewModel.SendAsync(paths);
    }
}
