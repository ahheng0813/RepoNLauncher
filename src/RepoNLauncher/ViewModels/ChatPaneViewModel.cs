using CommunityToolkit.Mvvm.ComponentModel;

namespace RepoNLauncher.ViewModels;

/// <summary>One of the chats on screen in the AI chat window. The pane stays put while the chat in it changes.</summary>
public sealed partial class ChatPaneViewModel(AgentChatViewModel owner) : ObservableObject
{
    public AgentChatViewModel Owner { get; } = owner;

    [ObservableProperty] private ChatSessionViewModel? _chat;

    /// <summary>The pane last clicked or typed in; New chat and the sidebar open chats here.</summary>
    [ObservableProperty] private bool _isActive;
}
