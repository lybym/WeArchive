using WeArchive.Core.Domain;

namespace WeArchive.App.ViewModels;

/// <summary>Conversation row shown in the selection list.</summary>
public sealed class ConversationItemViewModel(SourceConversation conversation) : ObservableObject
{
    public SourceConversation Model { get; } = conversation;

    public string SourceConversationId => Model.SourceConversationId;

    public string Title => string.IsNullOrWhiteSpace(Model.Title) ? Model.SourceConversationId : Model.Title!;

    public ConversationKind Kind => Model.Kind;

    public string KindLabel => Model.Kind switch
    {
        ConversationKind.Group => "群聊",
        ConversationKind.Direct => "好友",
        ConversationKind.Official => "公众号",
        ConversationKind.System => "系统",
        _ => "其他",
    };

    public string LastMessageLabel =>
        Model.LastMessageAt is null ? string.Empty : Model.LastMessageAt.Value.ToString("yyyy-MM-dd HH:mm");

    public string Subtitle => $"{KindLabel} · {SourceConversationId}";
}
