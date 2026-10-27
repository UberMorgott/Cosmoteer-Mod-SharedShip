using System;
using System.Collections.Generic;

namespace SharedShip;

/// <summary>
/// The decisions of <see cref="SharedRules"/> that need no game state, kept free of game types so the test project
/// compiles this file on its own.
/// </summary>
internal static partial class SharedRules
{
    /// <summary>Ship-name tag that marks a ship as shared. Case-sensitive, anywhere in the name.</summary>
    public const string Tag = "[S]";

    /// <summary>
    /// True when the name carries <see cref="Tag"/>. No cache on purpose: an ordinal search for a 3-char needle in a
    /// short name is vectorized and allocation-free, i.e. no slower than any per-ship cache lookup (ConditionalWeakTable
    /// needs a hash + lookup), and it can never go stale.
    /// </summary>
    public static bool HasTag(string? name) => name != null && name.Contains(Tag, StringComparison.Ordinal);

    /// <summary>Name with every occurrence of the tag removed and trimmed (may be empty; sanitize before use).</summary>
    public static string WithoutTag(string name) => name.Replace(Tag, string.Empty, StringComparison.Ordinal).Trim();

    /// <summary>Name with the tag prepended.</summary>
    public static string WithTag(string name) => Tag + " " + name;

    /// <summary>
    /// Owner and <paramref name="playerIndex"/> are two different player slots and neither is an AI faction or the
    /// spectator/"all" index (all negative). The Career human-slot check comes on top of this.
    /// </summary>
    public static bool AreDistinctPlayers(int owner, int playerIndex) => playerIndex >= 0 && owner >= 0 && owner != playerIndex;

    /// <summary>
    /// Player index of a connected human player (GameRoot.HumanPlayersByID), or null when there is no id or the player is
    /// not connected (never was, or left).
    /// </summary>
    public static int? ConnectedPlayerIndex<TId>(TId? id, IReadOnlyDictionary<TId, int> players)
        where TId : struct
    {
        return id is TId key && players.TryGetValue(key, out int index) ? index : null;
    }

    /// <summary>
    /// A trade may name only its sender as issuer (vanilla clients always send their own ID). A claim that resolves to no
    /// connected player names nobody PayerIndex could charge, so it passes.
    /// </summary>
    public static bool IsHonestIssuer(int? claimedIndex, int senderIndex) => claimedIndex is not int claimed || claimed == senderIndex;
}
