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
    /// Ships of two different players count as one player's ships when either of them is shared: a shared ship is "own"
    /// for every teammate. Two private ships of different players stay apart. Not transitive on purpose.
    /// </summary>
    public static bool IsSharedPair(bool aTagged, int aOwner, bool bTagged, int bOwner) =>
        (aTagged || bTagged) && AreDistinctPlayers(aOwner, bOwner);

    /// <summary>
    /// <paramref name="player"/>'s part of a shared ship's <paramref name="crew"/>, split equally over the sorted, distinct
    /// <paramref name="roster"/>: crew / N each, and the remainder one each to the lowest slots. The parts add up to
    /// <paramref name="crew"/> exactly; a player outside the roster gets 0.
    /// </summary>
    public static int CrewShare(int crew, IReadOnlyList<int> roster, int player)
    {
        int rank = IndexOf(roster, player);
        if (rank < 0)
        {
            return 0;
        }
        return crew / roster.Count + (rank < crew % roster.Count ? 1 : 0);
    }

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Adds <paramref name="slot"/> to a sorted list of distinct slots if missing.</summary>
    public static void AddSorted(List<int> sorted, int slot)
    {
        int i = sorted.BinarySearch(slot);
        if (i < 0)
        {
            sorted.Insert(~i, slot);
        }
    }

    /// <summary>Payer of a part list: the slot holding the most units, ties to the lowest slot; null when none is known.</summary>
    public static int? MajorPayer(List<(int Slot, int Qty)> parts)
    {
        int? best = null;
        int bestQty = 0;
        foreach (var (slot, qty) in parts)
        {
            if (slot >= 0 && (qty > bestQty || (qty == bestQty && best is int b && slot < b)))
            {
                best = slot;
                bestQty = qty;
            }
        }
        return best;
    }

    /// <summary>
    /// Units removed per price between two snapshots of one costed stock (price, quantity), in the order of
    /// <paramref name="before"/>. Vanilla picks which prices go (CostedQuantities); this only reads the result.
    /// </summary>
    public static List<(int Price, int Qty)> RemovedByPrice(IEnumerable<(int Price, int Qty)> before, IEnumerable<(int Price, int Qty)> after)
    {
        var left = new Dictionary<int, int>();
        foreach (var (price, qty) in after)
        {
            left[price] = left.GetValueOrDefault(price) + qty;
        }
        var removed = new List<(int, int)>();
        var seen = new HashSet<int>();
        var had = new Dictionary<int, int>();
        foreach (var (price, qty) in before)
        {
            had[price] = had.GetValueOrDefault(price) + qty;
        }
        foreach (var (price, _) in before)
        {
            if (seen.Add(price) && had[price] - left.GetValueOrDefault(price) is int gone and > 0)
            {
                removed.Add((price, gone));
            }
        }
        return removed;
    }
}

/// <summary>
/// Who paid for pre-purchased units still owed to buyers: lots of (key, payer slot, units). Vanilla keeps only
/// (buyer, resource, price, units) per station (PrePurchaseHandler) and (resource, price, units) per crew job; one ledger
/// per station (key = buyer, resource, price) and per job (one key) adds the payer. Within a key the newest lot goes first
/// (LIFO); which key and how many units go is always vanilla's choice. A list, not a dictionary: tiny, and its order is
/// the same on every client and after a save/load.
/// </summary>
internal sealed class PayerLedger<TKey> where TKey : IEquatable<TKey>
{
    public readonly List<(TKey Key, int Slot, int Qty)> Lots = new();

    public void Add(TKey key, int slot, int qty)
    {
        if (qty <= 0 || slot < 0)
        {
            return;
        }
        int last = Lots.FindLastIndex(l => l.Key.Equals(key));
        if (last >= 0 && Lots[last].Slot == slot)
        {
            Lots[last] = (key, slot, Lots[last].Qty + qty);
        }
        else
        {
            Lots.Add((key, slot, qty));
        }
    }

    /// <summary>Removes up to <paramref name="qty"/> units, newest first; units nobody recorded come back as slot -1.</summary>
    public List<(int Slot, int Qty)> Take(TKey key, int qty)
    {
        var parts = new List<(int Slot, int Qty)>();
        for (int i = Lots.Count - 1; i >= 0 && qty > 0; i--)
        {
            var lot = Lots[i];
            if (!lot.Key.Equals(key))
            {
                continue;
            }
            int n = Math.Min(qty, lot.Qty);
            parts.Add((lot.Slot, n));
            qty -= n;
            if (n == lot.Qty)
            {
                Lots.RemoveAt(i);
            }
            else
            {
                Lots[i] = (lot.Key, lot.Slot, lot.Qty - n);
            }
        }
        if (qty > 0)
        {
            parts.Add((-1, qty));
        }
        return parts;
    }

    /// <summary>Adds the known parts of a <see cref="Take"/> under <paramref name="key"/> (units moving to another place).</summary>
    public void AddParts(TKey key, List<(int Slot, int Qty)> parts)
    {
        foreach (var (slot, qty) in parts)
        {
            Add(key, slot, qty);
        }
    }
}