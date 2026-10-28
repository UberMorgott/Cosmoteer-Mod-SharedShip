using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer;
using Cosmoteer.Crew;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Resources;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Crew.Jobs;
using Cosmoteer.Ships.Resources;
using HarmonyLib;
using Halfling.Serialization.Generic;
using StationKey = System.ValueTuple<Cosmoteer.Ships.Ship, Cosmoteer.Data.ID<Cosmoteer.Resources.ResourceRules>, int>;

namespace SharedShip;

/// <summary>
/// Who paid for pre-purchased construction resources, so that cancelling them refunds the payer (0.30.5 refunds the
/// buyer ship's owner or the canceller). Vanilla charges the sender when a blueprint pre-buys from stations
/// (MPUpdateShipInput.cs:219-222, the only charge) and then tracks the units, without a payer, in two places: on the
/// station (PrePurchaseHandler: buyer, resource, price, units) and in the crew job carrying them (TradeType.PrePurchased,
/// PrePurchasedReturn, or SellBack once cancelled). A <see cref="PayerLedger{TKey}"/> follows the units in each place:
///   - bought: <see cref="Charge"/> (the AddPrePurchaseResources call in PrePurchaseBuyableResources);
///   - station to job: the one removal that is a move (ResourceManager.cs:2743, <see cref="TransferRemove"/>);
///   - job to station: AddPrePurchaseResources inside a job's Cancel/delivery/removal (ResourceTransferJob.cs:1050/1487,
///     ResourceManager.cs:1205, <see cref="AddPrefix"/>); job to job: a cancelled job's carried units
///     (<see cref="CarriedPostfix"/>);
///   - delivered to the buyer: spent (<see cref="DeliverPostfix"/>); delivered as a sell-back: the sale is split over the
///     payers of the units sold;
///   - refunded: other station removals (vanilla picks which prices), a cancelled job's units that are not returned, and a
///     station leaving the sim move their payers to a stash; the refund that follows pays them
///     (<see cref="CancelRefundablePostfix"/> for MPUpdateShipInput.cs:230/307 and ConstructionManager.cs:580,
///     <see cref="RefundTranspiler"/> for ResourceManager.cs:2721/3082/3092/3833).
/// Units no ledger knows (older saves) are refunded as vanilla. Saved as optional paths (vanilla's path reader skips them)
/// and part of the integrity hash.
/// </summary>
internal static class PrePurchases
{
    private const string StationPath = "SharedShipPrePurchasePayers";
    private const string JobPath = "SharedShipPayerLots";

    private static readonly ConditionalWeakTable<PrePurchaseHandler, PayerLedger<StationKey>> StationLots = new();
    private static readonly ConditionalWeakTable<ResourceTransferJob, PayerLedger<int>> JobLots = new();

    // Sim thread only.
    [ThreadStatic] private static Dictionary<Ship, List<(int Slot, int Value)>>? _stash; // payers owed a refund, newest last
    [ThreadStatic] private static List<ResourceTransferJob>? _jobs; // jobs whose Cancel/delivery/removal is running
    [ThreadStatic] private static bool _charging, _moving;
    [ThreadStatic] private static ResourceTransferJob? _lastJob, _lastCancelled, _moveTo;
    [ThreadStatic] private static Ship? _refundBuyer;

    private static PayerLedger<StationKey> Station(PrePurchaseHandler h) => StationLots.GetOrCreateValue(h);

    private static PayerLedger<int> JobLedger(ResourceTransferJob j) => JobLots.GetOrCreateValue(j);

    public static ResourceTransferJob? CurrentJob => _jobs is { Count: > 0 } ? _jobs[^1] : null;

    public static void PushJob(ResourceTransferJob job) => (_jobs ??= new()).Add(job);

    public static void PopJob() => _jobs!.RemoveAt(_jobs.Count - 1);

    /// <summary>Payer with the most units in <paramref name="job"/>, or null (used to index a sell-back job).</summary>
    public static int? MajorPayerOf(ResourceTransferJob job) =>
        JobLots.TryGetValue(job, out var l) ? SharedRules.MajorPayer(l.Lots.Select(x => (x.Slot, x.Qty)).ToList()) : null;

    private static void ToStash(Ship buyer, int price, List<(int Slot, int Qty)> parts)
    {
        foreach (var (slot, qty) in parts)
        {
            if (slot >= 0 && qty > 0)
            {
                _stash ??= new();
                if (!_stash.TryGetValue(buyer, out var list))
                {
                    _stash[buyer] = list = new();
                }
                list.Add((slot, qty * price));
            }
        }
    }

    public static void ClearStash() => _stash?.Clear();

    /// <summary>Pays the stashed payers of <paramref name="buyer"/>, newest first, up to <paramref name="value"/>; returns the rest.</summary>
    private static int Pay(GameRoot? game, Ship? buyer, int value)
    {
        if (game == null || buyer == null || _stash == null || !_stash.TryGetValue(buyer, out var parts))
        {
            return value;
        }
        while (value > 0 && parts.Count > 0)
        {
            var (slot, owed) = parts[^1];
            int n = Math.Min(owed, value);
            game.GetPlayerState(slot, deterministic: true).AddMoney(n);
            value -= n;
            if (n == owed)
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts[^1] = (slot, owed - n);
            }
        }
        if (parts.Count == 0)
        {
            _stash.Remove(buyer);
        }
        return value;
    }

    // ---- Bought ------------------------------------------------------------------------------------------------------

    public static void Charge(ResourceManager station, Ship purchaser, ID<ResourceRules> res, int amount, int price)
    {
        _charging = true;
        try
        {
            station.AddPrePurchaseResources(purchaser, res, amount, price);
        }
        finally
        {
            _charging = false;
        }
        Station(station._prePurchaseHandler).Add((purchaser, res, price), Acting.Player ?? purchaser.Metadata.PlayerIndex, amount);
    }

    public static IEnumerable<CodeInstruction> ChargeTranspiler(IEnumerable<CodeInstruction> instructions) =>
        ReplaceCalls(instructions, AccessTools.Method(typeof(ResourceManager), nameof(ResourceManager.AddPrePurchaseResources)), nameof(Charge), null);

    // ---- Moves ---------------------------------------------------------------------------------------------------------

    /// <summary>ResourceManager.AddPrePurchaseResources outside a purchase: units come back to the station from a job.</summary>
    public static void AddPrefix(ResourceManager __instance, Ship purchaser, ID<ResourceRules> resourceType, int amount, int price)
    {
        if (!_charging && CurrentJob is ResourceTransferJob job && JobLots.TryGetValue(job, out var lots))
        {
            Station(__instance._prePurchaseHandler).AddParts((purchaser, resourceType, price), lots.Take(0, amount));
        }
    }

    public static void ManualTransferPostfix(ResourceTransferJob __result) => _lastJob = __result;

    /// <summary>ResourceManager.cs:2742-2743: a job was just made for pre-purchased units that now leave the station.</summary>
    public static CostedQuantityTotal TransferRemove(PrePurchaseHandler handler, Ship buyer, ID<ResourceRules> res, int qty)
    {
        _moving = true;
        try
        {
            return handler.RemovePrePurchases(buyer, res, qty);
        }
        finally
        {
            _moving = false;
        }
    }

    public static IEnumerable<CodeInstruction> TransferTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo remove = AccessTools.Method(typeof(PrePurchaseHandler), nameof(PrePurchaseHandler.RemovePrePurchases),
            new[] { typeof(Ship), typeof(ID<ResourceRules>), typeof(int) });
        // The move is the removal whose result is discarded (the refund at :2720 keeps it).
        return ReplaceCalls(instructions, remove, nameof(TransferRemove), next => next.opcode == OpCodes.Pop);
    }

    private static List<(ID<ResourceRules> Res, int Price, int Qty)> Snapshot(PrePurchaseHandler handler, Ship buyer)
    {
        var list = new List<(ID<ResourceRules>, int, int)>();
        foreach (var (res, quantities) in handler.GetPrePurchases(buyer))
        {
            foreach (CostedQuantity q in quantities)
            {
                list.Add((res, q.PricePerUnit, q.Quantity));
            }
        }
        return list;
    }

    public static void RemovePrefix(PrePurchaseHandler __instance, Ship buyer, out List<(ID<ResourceRules> Res, int Price, int Qty)>? __state)
    {
        __state = buyer == null ? null : Snapshot(__instance, buyer);
    }

    /// <summary>PrePurchaseHandler.RemovePrePurchases: the units vanilla removed go to the new job (a move) or to a refund.</summary>
    public static void RemovePostfix(PrePurchaseHandler __instance, Ship buyer, List<(ID<ResourceRules> Res, int Price, int Qty)>? __state)
    {
        if (__state == null)
        {
            return;
        }
        var after = Snapshot(__instance, buyer);
        ResourceTransferJob? redirectedTo = _moveTo;
        foreach (ID<ResourceRules> res in __state.Select(s => s.Res).Distinct())
        {
            foreach (var (price, qty) in SharedRules.RemovedByPrice(
                __state.Where(s => s.Res.Equals(res)).Select(s => (s.Price, s.Qty)),
                after.Where(s => s.Res.Equals(res)).Select(s => (s.Price, s.Qty))))
            {
                var parts = Station(__instance).Take((buyer, res, price), qty);
                if ((_moving ? _lastJob : _moveTo) is ResourceTransferJob to)
                {
                    JobLedger(to).AddParts(0, parts);
                }
                else
                {
                    ToStash(buyer, price, parts);
                }
            }
        }
        if (redirectedTo != null)
        {
            _moveTo = null;
        }
    }

    public static void CancelledFinalizer(ResourceTransferJob job) => _lastCancelled = job;

    /// <summary>End of TransferConstructionResourcesTo: a redirect (:2714) without the removal (:2720) must not leak.</summary>
    public static void TransferScopeFinalizer() => _moveTo = _lastCancelled = null;

    /// <summary>
    /// Carried units of a cancelled or delivering job become a new job (sell-back or return): their payers go with them.
    /// ResourceManager.cs:2709-2721 instead hands a cancelled sell-back's units to a buyer that pre-purchased the same
    /// resource from the sell-back's station: the buyer's station units move into the new job (the next removal, :2720),
    /// and the refund that follows (:2721) replaces the sale, so it pays the sell-back's payers.
    /// </summary>
    public static void CarriedPostfix(ResourceManager __instance, ResourceTransferJob __result, int amount, TradeType tradeType)
    {
        if (CurrentJob is ResourceTransferJob from)
        {
            if (from != __result && JobLots.TryGetValue(from, out var lots))
            {
                JobLedger(__result).AddParts(0, lots.Take(0, amount));
            }
        }
        else if (tradeType == TradeType.PrePurchased && _lastCancelled is ResourceTransferJob old)
        {
            // A sell-back from a cancelled buy has one payer and no lots.
            var lots = JobLots.TryGetValue(old, out var l) ? l : FromPayer(Payers.Get(old), amount);
            if (old.TradeType == TradeType.SellBack && __instance.Ship is Ship buyer)
            {
                ToStash(buyer, old.PricePerResource, lots.Take(0, amount));
                _moveTo = __result;
            }
            else if (old.TradeType == TradeType.PrePurchasedReturn)
            {
                JobLedger(__result).AddParts(0, lots.Take(0, amount)); // still the same purchase, now for this buyer
            }
        }
    }

    private static PayerLedger<int> FromPayer(int? payer, int qty)
    {
        var ledger = new PayerLedger<int>();
        if (payer is int p)
        {
            ledger.Add(0, p, qty);
        }
        return ledger;
    }

    /// <summary>A cancelled pre-purchase job whose units are not returned to the station is refunded.</summary>
    public static void CancelledRefund(ResourceTransferJob job, int units)
    {
        if (job.Ship is Ship buyer && JobLots.TryGetValue(job, out var lots))
        {
            ToStash(buyer, job.PricePerResource, lots.Take(0, units));
        }
    }

    /// <summary>A station leaving the sim refunds (or, while jumping, drops) everything pre-purchased from it.</summary>
    public static void DeactivatedPrefix(ResourceManager __instance)
    {
        if (StationLots.TryGetValue(__instance._prePurchaseHandler, out var ledger))
        {
            foreach (var ((buyer, _, price), slot, qty) in ledger.Lots)
            {
                ToStash(buyer, price, new() { (slot, qty) });
            }
            ledger.Lots.Clear();
        }
    }

    public static void DeactivatedFinalizer() => ClearStash();

    // ---- Delivery ------------------------------------------------------------------------------------------------------

    public static void DeliverPrefix(ResourceTransferJob __instance, CrewSoul crew, out (int Carried, int Money) __state)
    {
        PushJob(__instance);
        bool split = __instance.TradeType == TradeType.SellBack && JobLots.TryGetValue(__instance, out _) && __instance.Game != null;
        __state = (crew.CarriedResource.Quantity, split ? __instance.Game!.GetPlayerState(SharedRules.PayerIndex(__instance), deterministic: true).Money : 0);
    }

    /// <summary>
    /// Delivered pre-purchased units are spent. A delivered sell-back was credited to the job's payer
    /// (ResourceTransferJob.cs:1480-1484); the share of units other players paid for moves on to them.
    /// </summary>
    public static void DeliverPostfix(ResourceTransferJob __instance, CrewSoul crew, (int Carried, int Money) __state)
    {
        if (!JobLots.TryGetValue(__instance, out var lots))
        {
            return;
        }
        int units = __state.Carried - crew.CarriedResource.Quantity;
        if (__instance.TradeType == TradeType.PrePurchased)
        {
            lots.Take(0, units);
        }
        else if (__instance.TradeType == TradeType.SellBack && __instance.Game is GameRoot game)
        {
            PlayerState credited = game.GetPlayerState(SharedRules.PayerIndex(__instance), deterministic: true);
            // Vanilla credits price * received (ResourceTransferJob.cs:1480-1484); without lost excess that is every unit
            // that left the crew. Measured only for lost excess, where the received count is not visible here.
            int received = credited.Money - __state.Money; // what actually reached the wallet (AddMoney may clamp)
            int income = __instance.ResourceTypeRules.LoseExcessOnDelivery ? received : units * __instance.PricePerResource;
            int total = income;
            var others = new List<(int Slot, int Value)>();
            foreach (var (slot, qty) in lots.Take(0, units))
            {
                int n = Math.Min(income, qty * __instance.PricePerResource);
                if (slot >= 0 && slot != credited.Index && n > 0)
                {
                    others.Add((slot, n));
                }
                income -= Math.Max(n, 0);
            }
            // The job's payer keeps its own share (what is left of the income); only the rest of what it received moves on.
            int own = total - others.Sum(o => o.Value);
            credited.AddMoney(-Math.Clamp(received - own, 0, others.Sum(o => o.Value)));
            foreach (var (slot, value) in others)
            {
                game.GetPlayerState(slot, deterministic: true).AddMoney(value);
            }
        }
    }

    public static void JobScopeFinalizer() => PopJob();

    public static void JobScopePrefix(ResourceTransferJob job) => PushJob(job);

    // ---- Refunds -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Refunds of CancelRefundableResources go to the payers; the caller (MPUpdateShipInput.cs:230/307,
    /// ConstructionManager.cs:580) then only credits what no ledger knows.
    /// </summary>
    public static void CancelRefundablePostfix(ResourceManager __instance, ref CostedQuantityTotal __result)
    {
        __result.TotalValue = Pay(__instance.Game, __instance.Ship, __result.TotalValue);
    }

    public static PlayerState RefundState(GameRoot game, Ship buyer, bool deterministic)
    {
        _refundBuyer = buyer;
        return game.GetPlayerState(buyer.Metadata.PlayerIndex, deterministic);
    }

    public static PlayerState RefundStateOfJob(GameRoot game, Job job, bool deterministic)
    {
        _refundBuyer = job.Ship;
        return game.GetPlayerState(job is ResourceTransferJob rtj ? SharedRules.PayerIndex(rtj) : job.Ship!.Metadata.PlayerIndex, deterministic);
    }

    public static int AddRefund(PlayerState fallback, int value)
    {
        Ship? buyer = _refundBuyer;
        _refundBuyer = null;
        return fallback.AddMoney(Pay(fallback.Game, buyer, value));
    }

    /// <summary>Methods that refund pre-purchases to "ship.Metadata.PlayerIndex", with their refund count (0.30.5).</summary>
    public static readonly (string Name, int Refunds)[] RefundMethods =
    {
        ("TransferConstructionResourcesTo", 1),
        ("OnComponentDeactivated", 2),
        ("RefundExpiredPrePurchasedResources", 1),
    };

    /// <summary>
    /// "GetPlayerState([job.]Ship.Metadata.PlayerIndex, true) ... AddMoney(v)" becomes
    /// "RefundState(game, ship|job, true) ... AddRefund(state, v)".
    /// </summary>
    public static IEnumerable<CodeInstruction> RefundTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        MethodInfo getShip = AccessTools.PropertyGetter(typeof(Job), nameof(Job.Ship));
        MethodInfo getMetadata = AccessTools.PropertyGetter(typeof(Ship), nameof(Ship.Metadata));
        MethodInfo getPlayerIndex = AccessTools.PropertyGetter(typeof(ShipMetadata), nameof(ShipMetadata.PlayerIndex));
        MethodInfo getState = AccessTools.Method(typeof(GameRoot), nameof(GameRoot.GetPlayerState), new[] { typeof(int), typeof(bool) });
        MethodInfo addMoney = AccessTools.Method(typeof(PlayerState), nameof(PlayerState.AddMoney));
        var list = instructions.ToList();
        int replaced = 0;
        for (int i = 1; i + 3 < list.Count; i++)
        {
            if (!(list[i].Calls(getMetadata) && list[i + 1].Calls(getPlayerIndex) && list[i + 2].opcode == OpCodes.Ldc_I4_1 && list[i + 3].Calls(getState)))
            {
                continue;
            }
            int add = list.FindIndex(i + 4, ins => ins.Calls(addMoney));
            if (add < 0)
            {
                continue;
            }
            bool ofJob = list[i - 1].Calls(getShip);
            if (ofJob)
            {
                list[i - 1] = new CodeInstruction(OpCodes.Nop).MoveLabelsFrom(list[i - 1]);
            }
            list[i] = new CodeInstruction(OpCodes.Nop).MoveLabelsFrom(list[i]);
            list[i + 1] = new CodeInstruction(OpCodes.Nop).MoveLabelsFrom(list[i + 1]);
            list[i + 3] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PrePurchases), ofJob ? nameof(RefundStateOfJob) : nameof(RefundState))).MoveLabelsFrom(list[i + 3]);
            list[add] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PrePurchases), nameof(AddRefund))).MoveLabelsFrom(list[add]);
            replaced++;
        }
        int expected = RefundMethods.First(m => m.Name == original.Name).Refunds;
        return replaced == expected
            ? list
            : throw new InvalidOperationException($"ResourceManager.{original.Name}: {replaced} refunds, expected {expected}");
    }

    // ---- Save and hash -------------------------------------------------------------------------------------------------

    public static void WriteToPostfix(ResourceManager __instance, GenericSerialWriter writer)
    {
        if (StationLots.TryGetValue(__instance._prePurchaseHandler, out var ledger) && ledger.Lots.Count > 0)
        {
            GenericSerialWriter w = writer.GetWriterForPath(StationPath);
            w.WriteToPath("Buyers", ledger.Lots.Select(l => l.Key.Item1).ToList());
            w.WriteToPath("Resources", ledger.Lots.Select(l => l.Key.Item2).ToList());
            w.WriteToPath("Lots", ledger.Lots.SelectMany(l => new[] { l.Key.Item3, l.Slot, l.Qty }).ToList());
        }
    }

    public static void ReadFromPostfix(ResourceManager __instance, GenericSerialReader reader)
    {
        if (!reader.TryGetReaderForPath(StationPath, out GenericSerialReader? r)
            || !r.TryReadFromPath("Buyers", out List<Ship>? buyers) || !r.TryReadFromPath("Resources", out List<ID<ResourceRules>>? res)
            || !r.TryReadFromPath("Lots", out List<int>? lots)
            || buyers == null || res == null || lots == null || res.Count != buyers.Count || lots.Count != res.Count * 3)
        {
            return;
        }
        var ledger = Station(__instance._prePurchaseHandler);
        ledger.Lots.Clear();
        for (int i = 0; i < res.Count; i++)
        {
            if (buyers[i] != null)
            {
                ledger.Lots.Add(((buyers[i], res[i], lots[3 * i]), lots[3 * i + 1], lots[3 * i + 2]));
            }
        }
    }

    public static void HashPostfix(ResourceManager __instance, IntegrityHash iHash)
    {
        if (StationLots.TryGetValue(__instance._prePurchaseHandler, out var ledger) && ledger.Lots.Count > 0)
        {
            foreach (var ((buyer, res, _), _, _) in ledger.Lots)
            {
                buyer.UniqueID.GetIntegrityHash(iHash);
                res.GetIntegrityHash(iHash);
            }
            Hash(ledger.Lots.Select(l => (l.Key.Item3, l.Slot, l.Qty)), iHash);
        }
    }

    public static void WriteJob(ResourceTransferJob job, GenericSerialWriter writer)
    {
        if (JobLots.TryGetValue(job, out var ledger) && ledger.Lots.Count > 0)
        {
            writer.WriteToPath(JobPath, ledger.Lots.SelectMany(l => new[] { l.Slot, l.Qty }).ToList());
        }
    }

    public static void ReadJob(ResourceTransferJob job, GenericSerialReader reader)
    {
        if (reader.TryReadFromPath(JobPath, out List<int>? lots) && lots != null)
        {
            var ledger = JobLedger(job);
            ledger.Lots.Clear();
            for (int i = 0; i + 1 < lots.Count; i += 2)
            {
                ledger.Lots.Add((0, lots[i], lots[i + 1]));
            }
        }
    }

    public static void HashJob(ResourceTransferJob job, IntegrityHash iHash)
    {
        if (JobLots.TryGetValue(job, out var ledger) && ledger.Lots.Count > 0)
        {
            Hash(ledger.Lots.Select(l => (0, l.Slot, l.Qty)), iHash);
        }
    }

    // Absent and empty ledgers hash the same (nothing), so a save/load never changes the hash.
    private static void Hash(IEnumerable<(int Price, int Slot, int Qty)> lots, IntegrityHash iHash)
    {
        foreach (var (price, slot, qty) in lots)
        {
            price.GetIntegrityHash(iHash);
            slot.GetIntegrityHash(iHash);
            qty.GetIntegrityHash(iHash);
        }
    }

    // ---- Shared --------------------------------------------------------------------------------------------------------

    /// <summary>Replaces the one call of <paramref name="target"/> (where <paramref name="followedBy"/> holds) with a static helper.</summary>
    private static IEnumerable<CodeInstruction> ReplaceCalls(IEnumerable<CodeInstruction> instructions, MethodInfo target, string helper,
        Func<CodeInstruction, bool>? followedBy)
    {
        var list = instructions.ToList();
        int replaced = 0;
        for (int i = 0; i + 1 < list.Count; i++)
        {
            if (list[i].Calls(target) && (followedBy == null || followedBy(list[i + 1])))
            {
                list[i] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PrePurchases), helper)).MoveLabelsFrom(list[i]);
                replaced++;
            }
        }
        return replaced == 1
            ? list
            : throw new InvalidOperationException($"{target.DeclaringType?.Name}.{target.Name}: {replaced} calls, expected 1");
    }
}
