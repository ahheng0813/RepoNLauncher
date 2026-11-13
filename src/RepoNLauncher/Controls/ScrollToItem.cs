using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ListBox = System.Windows.Controls.ListBox;

namespace RepoNLauncher.Controls;

/// <summary>
/// Scrolls a list to <see cref="ItemProperty"/> whenever it changes, for a selection made in code that may be out of
/// view. With <see cref="CenterProperty"/>, the item goes to the middle of the view rather than just inside its edge.
/// </summary>
public static class ScrollToItem
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.RegisterAttached(
        "Item", typeof(object), typeof(ScrollToItem), new PropertyMetadata(null, OnItemChanged));

    public static readonly DependencyProperty CenterProperty = DependencyProperty.RegisterAttached(
        "Center", typeof(bool), typeof(ScrollToItem), new PropertyMetadata(false));

    public static object? GetItem(DependencyObject element) => element.GetValue(ItemProperty);
    public static void SetItem(DependencyObject element, object? value) => element.SetValue(ItemProperty, value);

    public static bool GetCenter(DependencyObject element) => (bool)element.GetValue(CenterProperty);
    public static void SetCenter(DependencyObject element, bool value) => element.SetValue(CenterProperty, value);

    private static void OnItemChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ListBox list || e.NewValue is not { } item) return;

        // Once the list has laid out its items: a diff that has just loaded has none yet.
        list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!ReferenceEquals(GetItem(list), item) || !list.Items.Contains(item)) return; // moved on already
            list.ScrollIntoView(item);
            if (!GetCenter(list)) return;

            list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container
                || FindChild<ScrollViewer>(list) is not { } viewer) return;
            var top = container.TransformToAncestor(viewer).Transform(default).Y;
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + top - (viewer.ViewportHeight - container.ActualHeight) / 2);
        });
    }

    internal static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
