using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RepoNLauncher.Controls;

/// <summary>
/// Runs <see cref="CommandProperty"/> when a list is scrolled to within a screenful of its end, for a list that loads
/// its items a page at a time. The new items coming in count as a scroll too, so a page too short to fill the view is
/// followed by the next one without waiting for the wheel.
/// </summary>
public static class LoadMoreOnScroll
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(LoadMoreOnScroll), new PropertyMetadata(null, OnCommandChanged));

    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject element, ICommand? value) => element.SetValue(CommandProperty, value);

    // Set once the list's items are watched, so that's done only once however often the command changes.
    private static readonly DependencyProperty WatchingItemsProperty = DependencyProperty.RegisterAttached(
        "WatchingItems", typeof(bool), typeof(LoadMoreOnScroll), new PropertyMetadata(false));

    private static readonly ScrollChangedEventHandler ScrollChanged = OnScrollChanged;

    private static void OnCommandChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ItemsControl list) return;
        list.RemoveHandler(ScrollViewer.ScrollChangedEvent, ScrollChanged);
        if (e.NewValue != null) list.AddHandler(ScrollViewer.ScrollChangedEvent, ScrollChanged);

        if ((bool)list.GetValue(WatchingItemsProperty)) return;
        list.SetValue(WatchingItemsProperty, true);
        // The items belong to the list, so this goes when the list does.
        ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, change) => OnItemsChanged(list, change);
    }

    // A list that starts over (a refresh, a new search, another project's) goes back to its top. Left where it was, it
    // would sit at the end of the new first page and load the next one straight away.
    private static void OnItemsChanged(ItemsControl list, NotifyCollectionChangedEventArgs change)
    {
        if (change.Action != NotifyCollectionChangedAction.Reset || GetCommand(list) == null) return;
        ScrollToItem.FindChild<ScrollViewer>(list)?.ScrollToTop();
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var list = (ItemsControl)sender;
        // Only the list's own scroller: a GridView's column headers sit in a scroller of their own.
        if (e.OriginalSource is not ScrollViewer viewer || viewer.TemplatedParent != list) return;

        // How far below the view the end is, in the scroller's units (rows for a virtualizing list).
        var belowView = viewer.ExtentHeight - viewer.VerticalOffset - viewer.ViewportHeight;
        if (belowView > viewer.ViewportHeight) return;

        if (GetCommand(list) is { } command && command.CanExecute(null)) command.Execute(null);
    }
}
