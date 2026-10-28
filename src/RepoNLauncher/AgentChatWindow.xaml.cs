using System.Windows;
using System.Windows.Interop;
using RepoNLauncher.ViewModels;

namespace RepoNLauncher;

/// <summary>
/// Chats with Claude Code and Codex: saved chats on the left, the open chats on the right, up to four side by side.
/// Each open chat is a <see cref="Controls.ChatPane"/>.
/// </summary>
public partial class AgentChatWindow : Window
{
    public AgentChatWindow(AgentChatViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        SourceInitialized += (_, _) => NativeMethods.UseImmersiveDarkMode(new WindowInteropHelper(this).Handle);
        Activated += (_, _) => viewModel.OnActivated();
    }
}
