using WeArchive.Core.Domain;

namespace WeArchive.Tests;

/// <summary>
/// Stable identity is the foundation of path stability: docs/EXPORT_PRD.md section 3.1 and
/// docs/PRD.md NFR-09.
/// </summary>
public sealed class StableIdTests
{
    [Fact]
    public void DirectConversationIdEqualsPeerIdentity()
    {
        var account = StableIds.Account("wechat-windows", "acct");

        Assert.Equal(
            StableIds.Participant(account, "wxid_alice"),
            StableIds.DirectConversation(account, "wxid_alice"));
    }

    [Fact]
    public void GroupConversationIdUsesGroupPrefix()
    {
        var account = StableIds.Account("wechat-windows", "acct");
        var id = StableIds.GroupConversation(account, "100200300@chatroom");

        Assert.StartsWith("g_", id, StringComparison.Ordinal);
    }

    [Fact]
    public void IdsAreDeterministicAcrossCalls()
    {
        var account = StableIds.Account("wechat-windows", "acct");

        Assert.Equal(
            StableIds.Message(account, "s:12345"),
            StableIds.Message(account, "s:12345"));
    }

    [Fact]
    public void IdsDoNotDependOnDisplayNames()
    {
        // The same upstream identity must produce the same stable id no matter what the
        // current remark, nickname or group title happens to be.
        var account = StableIds.Account("wechat-windows", "acct");

        Assert.Equal(
            StableIds.Participant(account, "wxid_alice"),
            StableIds.Participant(account, "wxid_alice"));

        Assert.NotEqual(
            StableIds.Participant(account, "wxid_alice"),
            StableIds.Participant(account, "wxid_bob"));
    }

    [Fact]
    public void DifferentAccountsProduceDifferentIdsForTheSameSourceUser()
    {
        Assert.NotEqual(
            StableIds.Participant(StableIds.Account("wechat-windows", "a"), "wxid_x"),
            StableIds.Participant(StableIds.Account("wechat-windows", "b"), "wxid_x"));
    }

    [Fact]
    public void IdsCarryTheDocumentedPrefixes()
    {
        var account = StableIds.Account("wechat-windows", "acct");

        Assert.StartsWith("a_", account, StringComparison.Ordinal);
        Assert.StartsWith("u_", StableIds.Participant(account, "wxid_x"), StringComparison.Ordinal);
        Assert.StartsWith("m_", StableIds.Message(account, "s:1"), StringComparison.Ordinal);
    }
}

/// <summary>docs/EXPORT_PRD.md section 5.1 — the default display-name rule.</summary>
public sealed class IdentityRuleTests
{
    private static ArchiveParticipant Participant(string? remark, string? nickname, string? overrideName = null) =>
        new()
        {
            Id = "u_1",
            AccountId = "a_1",
            SourceParticipantId = "wxid_x",
            LatestRemark = remark,
            Nickname = nickname,
            UserDisplayName = overrideName,
        };

    [Fact]
    public void LatestRemarkBecomesTheDisplayName()
    {
        Assert.Equal("张三", Participant("张三", "三哥").ResolveDisplayName());
    }

    [Fact]
    public void NicknameNeverFillsAMissingRemark()
    {
        Assert.Equal(string.Empty, Participant(null, "Kevin").ResolveDisplayName());
        Assert.Equal(string.Empty, Participant(string.Empty, "Kevin").ResolveDisplayName());
    }

    [Fact]
    public void UserMaintainedOverrideWins()
    {
        Assert.Equal("自定义", Participant("张三", "三哥", "自定义").ResolveDisplayName());
    }
}
