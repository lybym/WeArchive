using WeArchive.Core.Query;
using WeArchive.Core.Services;

namespace WeArchive.Tests;

/// <summary>
/// Unit tests of the citation-ready evidence-reference grammar (issue #107, docs/HARNESS.md
/// section 11, docs/CLI.md <c>wearchive context</c>). The convention must parse exactly the
/// documented canonical form and reject every deviation fail-closed, because a citation that
/// silently parses into a different window would resolve to evidence the agent did not cite.
/// </summary>
public sealed class EvidenceReferenceTests
{
    private const string MessageId = "m_0123456789abcdef";
    private const string ConversationId = "g_fedcba9876543210";

    [Fact]
    public void CanonicalFormRoundTripsThroughParse()
    {
        var reference = EvidenceReference.Create(MessageId, ConversationId, 5, 7);
        var canonical = reference.ToCanonicalForm();

        Assert.Equal(
            $"wearchive://message/{MessageId}?conversation={ConversationId}&before=5&after=7",
            canonical);

        Assert.True(
            EvidenceReference.TryParse(canonical, out var parsed, out var error),
            error);
        Assert.Null(error);
        Assert.Equal(reference, parsed);
        Assert.Equal(MessageId, parsed!.MessageId);
        Assert.Equal(ConversationId, parsed.ConversationId);
        Assert.Equal(5, parsed.Before);
        Assert.Equal(7, parsed.After);
        Assert.Equal(canonical, parsed.ToString());
    }

    [Fact]
    public void ZeroWindowAndDirectConversationAreCanonicalForms()
    {
        var direct = EvidenceReference.Create("m_0123456789abcdef", "u_fedcba9876543210", 0, 0);

        Assert.Equal(
            "wearchive://message/m_0123456789abcdef?conversation=u_fedcba9876543210&before=0&after=0",
            direct.ToCanonicalForm());
        Assert.True(
            EvidenceReference.TryParse(direct.ToCanonicalForm(), out var parsed, out _),
            "the zero window is a legal citation of a target-only window.");
        Assert.Equal(direct, parsed);
    }

    [Fact]
    public void ParseAcceptsTheMaximumDocumentedWindow()
    {
        var atMaximum = EvidenceReference.Create(MessageId, ConversationId, ArchiveQueryService.MaxContextMessages, 0);

        Assert.True(
            EvidenceReference.TryParse(atMaximum.ToCanonicalForm(), out var parsed, out _),
            "the maximum window a context resolution accepts must stay citable.");
        Assert.Equal(ArchiveQueryService.MaxContextMessages, parsed!.Before);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("m_0123456789abcdef")]
    [InlineData("WeArchive://message/m_0123456789abcdef?conversation=g_fedcba9876543210&before=1&after=1")]
    [InlineData("wearchive://conversation/m_0123456789abcdef?conversation=g_fedcba9876543210&before=1&after=1")]
    [InlineData("wearchive://message/x_0123456789abcdef?conversation=g_fedcba9876543210&before=1&after=1")]
    [InlineData("wearchive://message/m_0123456789ABCDEF?conversation=g_fedcba9876543210&before=1&after=1")]
    [InlineData("wearchive://message/m_0123456789abcde?conversation=g_fedcba9876543210&before=1&after=1")]
    [InlineData("wearchive://message/m_0123456789abcdeff?conversation=g_fedcba9876543210&before=1&after=1")]
    [InlineData("wearchive://message/m_0123456789abcdef?before=1&after=1")]
    [InlineData("wearchive://message/m_0123456789abcdef?conversation=x_fedcba9876543210&before=1&after=1")]
    [InlineData("wearchive://message/m_0123456789abcdef?conversation=g_fedcba9876543210&after=1")]
    [InlineData("wearchive://message/m_0123456789abcdef?conversation=g_fedcba9876543210&before=1")]
    [InlineData("wearchive://message/m_0123456789abcdef?conversation=g_fedcba9876543210")]
    [InlineData("wearchive://message/m_0123456789abcdef")]
    [InlineData("wearchive://message/m_0123456789abcdef?conversation=g_fedcba9876543210&before=1&after=1&source=s:1")]
    [InlineData("wearchive://message/m_0123456789abcdef?conversation=g_fedcba9876543210&before=1&after=1&before=2")]
    [InlineData("wearchive://message/m_0123456789abcdef?conversation=g_fedcba9876543210&before=-1&after=1")]
    [InlineData("wearchive://message/m_0123456789abcdef?conversation=g_fedcba9876543210&before=+1&after=1")]
    [InlineData("wearchive://message/m_0123456789abcdef?conversation=g_fedcba9876543210&before=101&after=1")]
    [InlineData("wearchive://message/m_0123456789abcdef?conversation=g_fedcba9876543210&before=one&after=1")]
    [InlineData("wearchive://message/m_0123456789abcdef?conversation=g_fedcba9876543210&before=1.5&after=1")]
    [InlineData("wearchive://message/m_0123456789abcdef?conversation=&before=1&after=1")]
    [InlineData("wearchive://message/m_0123456789abcdef?conversation=g_fedcba9876543210&before=&after=1")]
    [InlineData("wearchive://message/m_0123456789abcdef?conversation=g_fedcba9876543210&before")]
    [InlineData("wearchive://message/m_0123456789abcdef?")]
    public void ParseRejectsEveryDeviationFromTheDocumentedForm(string? text)
    {
        Assert.False(EvidenceReference.TryParse(text, out var reference, out var error));
        Assert.Null(reference);
        Assert.NotNull(error);
        Assert.NotEqual(string.Empty, error);
    }

    [Fact]
    public void CreateRejectsNonCanonicalParts()
    {
        Assert.Throws<ArgumentException>(
            () => EvidenceReference.Create("x_0123456789abcdef", ConversationId, 1, 1));
        Assert.Throws<ArgumentException>(
            () => EvidenceReference.Create(MessageId, "100200300@chatroom", 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EvidenceReference.Create(MessageId, ConversationId, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => EvidenceReference.Create(MessageId, ConversationId, 1, ArchiveQueryService.MaxContextMessages + 1));
    }
}
