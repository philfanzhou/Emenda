namespace Emenda.Domain.Models;

/// <summary>Values of a mistake's source kind (<c>mistake_item.source_kind</c>, HTTP <c>sourceKind</c>).</summary>
public static class MistakeSourceKinds
{
    /// <summary>Published by an automatic extraction task.</summary>
    public const string Extraction = "extraction";
    /// <summary>Collected from an AI Tutor conversation.</summary>
    public const string Conversation = "conversation";
    /// <summary>Created by the platform before either pipeline existed; owned by no caller.</summary>
    public const string Legacy = "legacy";
}
