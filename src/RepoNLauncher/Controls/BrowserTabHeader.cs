using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace RepoNLauncher.Controls;

/// <summary>
/// A tab along a browser window's tabs. Screen readers and UI Automation see it as a tab, named by its
/// AutomationProperties.Name, that says whether it's the one shown and can be picked.
/// </summary>
public class BrowserTabHeader : ContentControl
{
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(BrowserTabHeader));

    /// <summary>The tab its window shows.</summary>
    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>Picked through UI Automation, such as by a screen reader; the window shows it.</summary>
    public event EventHandler? SelectRequested;

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(BrowserTabHeader owner) : FrameworkElementAutomationPeer(owner), ISelectionItemProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TabItem;

        protected override string GetClassNameCore() => nameof(BrowserTabHeader);

        public override object GetPattern(PatternInterface pattern) =>
            pattern == PatternInterface.SelectionItem ? this : base.GetPattern(pattern);

        public bool IsSelected => owner.IsSelected;

        public IRawElementProviderSimple? SelectionContainer => null;

        public void Select() => owner.SelectRequested?.Invoke(owner, EventArgs.Empty);

        public void AddToSelection() => throw new InvalidOperationException("Only one tab shows at a time.");

        public void RemoveFromSelection() => throw new InvalidOperationException("A window always shows one of its tabs.");
    }
}
