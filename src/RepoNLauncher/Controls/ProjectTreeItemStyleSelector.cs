using System.Windows;
using System.Windows.Controls;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher.Controls;

/// <summary>Gives the project tree's folder sections and project rows their own container style.</summary>
public class ProjectTreeItemStyleSelector : StyleSelector
{
    public Style? GroupStyle { get; set; }
    public Style? ProjectStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container) =>
        item is ProjectGroupViewModel ? GroupStyle : ProjectStyle;
}
