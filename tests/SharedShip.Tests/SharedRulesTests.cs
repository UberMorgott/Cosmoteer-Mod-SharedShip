using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SharedShip.Tests;

public class SharedRulesTests
{
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

    [Theory]
    [InlineData(true, 0, false, 1, true)] // shared ship of 0 is "own" for 1's private ship
    [InlineData(true, 0, true, 1, true)] // two shared ships of different players
    [InlineData(false, 0, false, 1, false)] // two private ships of different players stay apart
    [InlineData(true, 0, false, 0, false)] // same owner: vanilla decides, sharing adds nothing
    [InlineData(true, 0, false, -1, false)] // AI-owned ship
    [InlineData(true, -1, false, 0, false)] // tagged AI ship
    [InlineData(true, 0, false, -4, false)] // spectator/"all" index
    public void IsSharedPair_OneSharedShipOfTwoDistinctPlayers(bool aTagged, int aOwner, bool bTagged, int bOwner, bool expected)
    {
        Assert.Equal(expected, SharedRules.IsSharedPair(aTagged, aOwner, bTagged, bOwner));
        Assert.Equal(expected, SharedRules.IsSharedPair(bTagged, bOwner, aTagged, aOwner)); // symmetric
    }

    // ---- Crew split ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(10, 0, 5)] // the user's example: a 10-crew shared ship counts 5 for each of two players
    [InlineData(10, 1, 5)]
    [InlineData(7, 0, 4)] // odd: the remainder goes to the lowest slot
    [InlineData(7, 1, 3)]
    [InlineData(7, 2, 0)] // not a player of this career
    public void CrewShare_SplitsEquallyRemainderToLowestSlots(int crew, int player, int expected)
    {
        Assert.Equal(expected, SharedRules.CrewShare(crew, new[] { 0, 1 }, player));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(11)]
    public void CrewShare_PartsAddUpToTheCrew(int crew)
    {
        int[] roster = { 0, 2, 3 };
        Assert.Equal(crew, SharedRules.CrewShare(crew, roster, 0) + SharedRules.CrewShare(crew, roster, 2) + SharedRules.CrewShare(crew, roster, 3));
        Assert.Equal(new[] { 4, 4, 3 }, new[] { 0, 2, 3 }.Select(p => SharedRules.CrewShare(11, roster, p)));
    }

    [Fact]
    public void CrewShare_UserExample_PrivateShipsPlusHalfOfTheShared()
    {
        int[] roster = { 0, 1 };
        Assert.Equal(8, 3 + SharedRules.CrewShare(10, roster, 0)); // A: own 3-crew ship + half of 10
        Assert.Equal(10, 5 + SharedRules.CrewShare(10, roster, 1)); // B: own 5-crew ship + half of 10
    }

    [Fact]
    public void AddSorted_KeepsDistinctAscendingSlots()
    {
        var roster = new List<int>();
        foreach (int p in new[] { 3, 0, 3, 1 })
        {
            SharedRules.AddSorted(roster, p);
        }
        Assert.Equal(new[] { 0, 1, 3 }, roster);
    }

    // ---- Payer selection -------------------------------------------------------------------------------------------

    [Fact]
    public void MajorPayer_MostUnitsTiesToLowestSlotUnknownIgnored()
    {
        Assert.Equal(1, SharedRules.MajorPayer(new() { (0, 2), (1, 5), (-1, 9) }));
        Assert.Equal(0, SharedRules.MajorPayer(new() { (2, 3), (0, 3) }));
        Assert.Null(SharedRules.MajorPayer(new() { (-1, 4) }));
    }

    // ---- Pre-purchase ledger -----------------------------------------------------------------------------------------

    [Fact]
    public void Ledger_TakesNewestPurchaseOfThatPriceFirst()
    {
        var ledger = new PayerLedger<(string, int)>();
        ledger.Add(("steel", 10), 0, 4); // A buys first
        ledger.Add(("steel", 10), 1, 3); // then B
        Assert.Equal(new List<(int, int)> { (1, 3), (0, 2) }, ledger.Take(("steel", 10), 5));
        Assert.Equal(new List<(int, int)> { (0, 2) }, ledger.Take(("steel", 10), 9)[..1]);
    }

    [Fact]
    public void Ledger_OnlyTheCancelledResourceAndPriceRefundsItsPayer()
    {
        // Codex's case: A paid steel, B paid copper; cancelling steel must not touch B.
        var ledger = new PayerLedger<(string, int)>();
        ledger.Add(("steel", 10), 0, 4);
        ledger.Add(("copper", 10), 1, 4);
        ledger.Add(("steel", 25), 1, 2);
        Assert.Equal(new List<(int, int)> { (0, 4) }, ledger.Take(("steel", 10), 4));
        Assert.Equal(new List<(int, int)> { (1, 4) }, ledger.Take(("copper", 10), 4));
        Assert.Equal(new List<(int, int)> { (1, 2) }, ledger.Take(("steel", 25), 2));
        Assert.Empty(ledger.Lots);
    }

    [Fact]
    public void Ledger_UnknownUnitsComeBackAsNobody()
    {
        var ledger = new PayerLedger<(string, int)>();
        ledger.Add(("steel", 10), 2, 1);
        Assert.Equal(new List<(int, int)> { (2, 1), (-1, 3) }, ledger.Take(("steel", 10), 4));
    }

    [Fact]
    public void Ledger_SamePayerInARowMerges()
    {
        var ledger = new PayerLedger<(string, int)>();
        ledger.Add(("steel", 10), 0, 1);
        ledger.Add(("steel", 10), 0, 2);
        ledger.Add(("steel", 10), 0, 0); // nothing bought
        Assert.Single(ledger.Lots);
        Assert.Equal(3, ledger.Lots[0].Qty);
    }

    [Fact]
    public void Ledger_UnitsMovedToAJobKeepTheirPayers()
    {
        // Codex's case: A's units leave the station in a crew job, then B buys the same resource at the same price.
        // Delivering A's job must spend A's lot, and cancelling B's purchase must refund B.
        var station = new PayerLedger<(string, int)>();
        var job = new PayerLedger<int>();
        station.Add(("steel", 10), 0, 3);
        job.AddParts(0, station.Take(("steel", 10), 3));
        station.Add(("steel", 10), 1, 3);
        Assert.Equal(new List<(int, int)> { (0, 3) }, job.Take(0, 3));
        Assert.Equal(new List<(int, int)> { (1, 3) }, station.Take(("steel", 10), 3));
    }

    [Fact]
    public void Ledger_MixedSellBackKeepsEachPayersShare()
    {
        var job = new PayerLedger<int>();
        job.Add(0, 0, 2);
        job.Add(0, 1, 1);
        Assert.Equal(new List<(int, int)> { (1, 1), (0, 2) }, job.Take(0, 3));
    }
    [Fact]
    public void RemovedByPrice_ReadsVanillasHighestPriceFirstCancel()
    {
        // Vanilla cancels the most expensive units first (ResourceManager.CancelRefundableResources); the ledger follows
        // whatever prices vanilla actually removed.
        var before = new[] { (10, 5), (25, 2), (10, 1) };
        var after = new[] { (10, 4) };
        Assert.Equal(new List<(int, int)> { (10, 2), (25, 2) }, SharedRules.RemovedByPrice(before, after));
        Assert.Empty(SharedRules.RemovedByPrice(before, before));
    }
}