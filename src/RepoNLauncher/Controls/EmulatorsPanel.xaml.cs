using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RepoNLauncher.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace RepoNLauncher.Controls;

/// <summary>
/// The emulators on this PC, each to start, shut down, rename or delete, and making a new one: in the Android emulators
/// window and in Settings. It reads them again each time it's shown, since one may have been made or closed elsewhere.
/// </summary>
public partial class EmulatorsPanel : UserControl
{
    public EmulatorsPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private EmulatorsViewModel? ViewModel => DataContext as EmulatorsViewModel;

    private void Refresh()
    {
        if (ViewModel is { } vm) _ = vm.RefreshAsync();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    // Renaming starts with the name selected, ready to type over.
    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } box) return;
        box.Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    // Enter keeps the name, Esc drops it. Handled here, so Esc doesn't close Settings as its Close button would.
    private void RenameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || (sender as FrameworkElement)?.DataContext is not EmulatorItemViewModel item) return;
        if (e.Key == Key.Enter)
        {
            vm.SaveRenameCommand.Execute(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelRenameCommand.Execute(item);
            e.Handled = true;
        }
    }
}
