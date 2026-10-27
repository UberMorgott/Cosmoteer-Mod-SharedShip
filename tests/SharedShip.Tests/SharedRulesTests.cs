using System.Collections.Generic;
using Xunit;

namespace SharedShip.Tests;

public class SharedRulesTests
{
    // Stands in for Halfling's MessengerID: a value-type player id.
    private readonly record struct PlayerId(int Value);

    private static readonly PlayerId Host = new(100), Guest = new(200), Gone = new(300);

    // GameRoot.HumanPlayersByID: connected players only.
    private static readonly IReadOnlyDictionary<PlayerId, int> Connected = new Dictionary<PlayerId, int> { [Host] = 0, [Guest] = 1 };

    [Theory]
    [InlineData("[S] Freighter", true)]
    [InlineData("Freighter [S]", true)]
    [InlineData("Big[S]Hauler", true)]
    [InlineData("[s] Freighter", false)] // case-sensitive
    [InlineData("[ S] Freighter", false)]
    [InlineData("S Freighter", false)]
    [InlineData("", false)]
    [InlineData(null, false)] // ship metadata without a name
    public void HasTag_FindsTheTagAnywhereCaseSensitive(string? name, bool expected)
    {
        Assert.Equal(expected, SharedRules.HasTag(name));
    }

    [Theory]
    [InlineData("[S] Freighter", "Freighter")]
    [InlineData("  Freighter [S] ", "Freighter")]
    [InlineData("[S]A[S]", "A")] // every occurrence, so untagging never leaves a tag behind
    [InlineData("[s] Freighter", "[s] Freighter")]
    [InlineData("[S]", "")]
    public void WithoutTag_RemovesEveryOccurrenceAndTrims(string name, string expected)
    {
        Assert.Equal(expected, SharedRules.WithoutTag(name));
    }

    [Theory]
    [InlineData("Freighter")]
    [InlineData("")]
    public void WithTag_IsUndoneByWithoutTag(string name)
    {
        string tagged = SharedRules.WithTag(name);
        Assert.True(SharedRules.HasTag(tagged));
        Assert.Equal(name, SharedRules.WithoutTag(tagged));
    }

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(3, 0, true)]
    [InlineData(1, 1, false)] // the owner is not its own teammate: vanilla access, not sharing
    [InlineData(-1, 0, false)] // AI-owned ship
    [InlineData(0, -1, false)] // AI asking
    [InlineData(0, -4, false)] // spectator/"all" index
    public void AreDistinctPlayers_OnlyTwoDifferentNonNegativeSlots(int owner, int player, bool expected)
    {
        Assert.Equal(expected, SharedRules.AreDistinctPlayers(owner, player));
    }

    [Fact]
    public void ConnectedPlayerIndex_ResolvesConnectedPlayers()
    {
        Assert.Equal(1, SharedRules.ConnectedPlayerIndex<PlayerId>(Guest, Connected));
    }

    [Fact]
    public void ConnectedPlayerIndex_NoIssuer_IsNull()
    {
        // Auto/global trade jobs carry no issuer: PayerIndex falls back to the owner.
        Assert.Null(SharedRules.ConnectedPlayerIndex<PlayerId>(null, Connected));
    }

    [Fact]
    public void ConnectedPlayerIndex_DisconnectedIssuer_IsNull()
    {
        // A player who left is not in the join snapshot either: PayerIndex falls back to the owner on every client.
        Assert.Null(SharedRules.ConnectedPlayerIndex<PlayerId>(Gone, Connected));
    }

    [Fact]
    public void IsHonestIssuer_AcceptsSenderAsIssuer()
    {
        Assert.True(SharedRules.IsHonestIssuer(SharedRules.ConnectedPlayerIndex<PlayerId>(Guest, Connected), 1));
    }

    [Fact]
    public void IsHonestIssuer_RejectsAnotherConnectedPlayerAsIssuer()
    {
        // Guest (index 1) claiming the host would make PayerIndex charge the host's money.
        Assert.False(SharedRules.IsHonestIssuer(SharedRules.ConnectedPlayerIndex<PlayerId>(Host, Connected), 1));
    }

    [Fact]
    public void IsHonestIssuer_AcceptsClaimsNobodyCouldBeChargedFor()
    {
        Assert.True(SharedRules.IsHonestIssuer(SharedRules.ConnectedPlayerIndex<PlayerId>(null, Connected), 1));
        Assert.True(SharedRules.IsHonestIssuer(SharedRules.ConnectedPlayerIndex<PlayerId>(Gone, Connected), 1));
    }
}
