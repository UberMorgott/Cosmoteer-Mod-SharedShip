using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cosmoteer;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Crew.Jobs;
using Cosmoteer.Ships.Resources;
using HarmonyLib;
using Halfling.Serialization.Generic;
using JobKey = System.ValueTuple<Cosmoteer.Ships.Resources.IResourceSource, Cosmoteer.Ships.Crew.Jobs.TradeType, int>;

namespace SharedShip;

/// <summary>
/// The payer of every ResourceTransferJob: the acting player (<see cref="Acting"/>) when the job is created, so a trade
/// pays from and into the wallet of whoever made it (0.30.5 charged the ship owner, ResourceTransferJob.cs:189-197,
/// 1030, 1471-1483, 1698-1706, 1729-1737; <see cref="PayerTranspiler"/> rewrites those reads).
///   - Sell-backs created while a job is cancelled (ResourceTransferJob.cs:1060-1072) inherit its payer; for a cancelled
///     pre-purchase the payer comes from <see cref="PrePurchases"/>.
///   - Jobs of different payers are never merged (ResourceManager.SinkInfo, one job per (source, trade type, price),
///     ResourceManager.cs:154-207): <see cref="MergeTranspiler"/> keeps extra jobs of the same key in an overflow list.
///   - Saved as the optional path "SharedShipPayer" (vanilla's path reader skips it) and part of the integrity hash.
/// Old saves / auto jobs have no payer: PayerIndex falls back to the owner, exactly vanilla.
/// </summary>
internal static class Payers
{
    private const string SavePath = "SharedShipPayer";

    private sealed class Slot
    {
        public int Value;
    }

    private static readonly ConditionalWeakTable<ResourceTransferJob, Slot> PayerOf = new();
    private static readonly ConditionalWeakTable<Dictionary<JobKey, ResourceTransferJob>, List<ResourceTransferJob>> Overflow = new();

    public static int? Get(ResourceTransferJob job) => PayerOf.TryGetValue(job, out Slot? s) ? s.Value : null;

    private static void Set(ResourceTransferJob job, int? payer)
    {
        PayerOf.Remove(job);
        if (payer is int p)
        {
            PayerOf.Add(job, new Slot { Value = p });
        }
    }

    // ---- Money reads -------------------------------------------------------------------------------------------

    /// <summary>ResourceTransferJob members that move money, with how many owner reads each must contain (0.30.5).</summary>
    public static readonly (string Name, int OwnerReads)[] MoneyMethods =
    {
        ("set_ResourcesRequested", 3),
        ("Cancel", 1),
        ("DeliverResources", 3),
        ("OnAddedToSim", 3),
        ("OnRemovingFromSim", 3),
    };

    /// <summary>Rewrites "this.Ship.Metadata.PlayerIndex" into "SharedRules.PayerIndex(this)".</summary>
    public static IEnumerable<CodeInstruction> PayerTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        MethodInfo getShip = AccessTools.PropertyGetter(typeof(Job), nameof(Job.Ship));
        MethodInfo getMetadata = AccessTools.PropertyGetter(typeof(Ship), nameof(Ship.Metadata));
        MethodInfo getPlayerIndex = AccessTools.PropertyGetter(typeof(ShipMetadata), nameof(ShipMetadata.PlayerIndex));
        var list = instructions.ToList();
        int replaced = 0;
        for (int i = 1; i + 2 < list.Count; i++)
        {
            if (list[i - 1].opcode == OpCodes.Ldarg_0 && list[i].Calls(getShip) && list[i + 1].Calls(getMetadata) && list[i + 2].Calls(getPlayerIndex))
            {
                list[i] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SharedRules), nameof(SharedRules.PayerIndex))).MoveLabelsFrom(list[i]);
                list[i + 1] = new CodeInstruction(OpCodes.Nop).MoveLabelsFrom(list[i + 1]);
                list[i + 2] = new CodeInstruction(OpCodes.Nop).MoveLabelsFrom(list[i + 2]);
                replaced++;
            }
        }
        int expected = MoneyMethods.First(m => m.Name == original.Name).OwnerReads;
        // Partial rewrites would debit one player and refund another: require the exact 0.30.5 count.
        return replaced == expected
            ? list
            : throw new InvalidOperationException($"ResourceTransferJob.{original.Name}: {replaced} owner reads, expected {expected}");
    }

    // ---- One job per payer -------------------------------------------------------------------------------------

    private static bool IsMoney(TradeType t) => t is TradeType.Buy or TradeType.Sell or TradeType.SellBack;

    private static JobKey KeyOf(ResourceTransferJob j) => (j.Source!, j.TradeType, j.PricePerResource);

    private static bool Matches(ResourceTransferJob j, JobKey key) => !IsMoney(key.Item2) || Get(j) == Acting.Player;

    /// <summary>SinkInfo.CreateOrIncreaseManualJob lookup: the job of this key that the acting player may extend.</summary>
    public static bool TryGetJob(Dictionary<JobKey, ResourceTransferJob> dict, JobKey key, out ResourceTransferJob? job)
    {
        if (dict.TryGetValue(key, out job) && Matches(job, key))
        {
            return true;
        }
        if (Overflow.TryGetValue(dict, out List<ResourceTransferJob>? extra))
        {
            foreach (ResourceTransferJob j in extra)
            {
                if (KeyOf(j).Equals(key) && Matches(j, key))
                {
                    job = j;
                    return true;
                }
            }
        }
        job = null;
        return false;
    }

    /// <summary>
    /// SinkInfo index add (new job, or a job registered on load). A new job gets the acting player as payer. A second
    /// money job of another payer goes to the overflow list; anything else keeps vanilla's Dictionary.Add (which throws
    /// on a duplicate, caught by ResourceManager.ReadFrom).
    /// </summary>
    public static void AddJob(Dictionary<JobKey, ResourceTransferJob> dict, JobKey key, ResourceTransferJob job)
    {
        if (Get(job) == null && Acting.Player is int p)
        {
            Set(job, p);
        }
        if (dict.TryGetValue(key, out ResourceTransferJob? existing) && IsMoney(key.Item2) && Get(existing) != Get(job))
        {
            Overflow.GetOrCreateValue(dict).Add(job);
            return;
        }
        dict.Add(key, job);
    }

    /// <summary>SinkInfo index remove: drops <paramref name="job"/> and promotes the oldest overflow job of its key.</summary>
    public static bool RemoveJob(Dictionary<JobKey, ResourceTransferJob> dict, JobKey key, ResourceTransferJob job)
    {
        Overflow.TryGetValue(dict, out List<ResourceTransferJob>? extra);
        if (!dict.TryGetValue(key, out ResourceTransferJob? indexed) || indexed != job)
        {
            return extra?.Remove(job) ?? false;
        }
        dict.Remove(key);
        int next = extra?.FindIndex(j => KeyOf(j).Equals(key)) ?? -1;
        if (next >= 0)
        {
            dict.Add(key, extra![next]);
            extra.RemoveAt(next);
        }
        return true;
    }

    /// <summary>Routes SinkInfo's TryGetValue/Add/Remove on its manual-job index through the three helpers above.</summary>
    public static IEnumerable<CodeInstruction> MergeTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        Type dict = typeof(Dictionary<JobKey, ResourceTransferJob>);
        var map = new Dictionary<MethodInfo, string>
        {
            [AccessTools.Method(dict, "TryGetValue")] = nameof(TryGetJob),
            [AccessTools.Method(dict, "Add")] = nameof(AddJob),
            [AccessTools.Method(dict, "Remove", new[] { typeof(JobKey) })] = nameof(RemoveJob),
        };
        int replaced = 0;
        foreach (CodeInstruction ins in instructions)
        {
            if (ins.operand is MethodInfo m && map.TryGetValue(m, out string? helper))
            {
                if (helper == nameof(RemoveJob))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_1); // UnregisterJob(ResourceTransferJob job)
                }
                ins.opcode = OpCodes.Call;
                ins.operand = AccessTools.Method(typeof(Payers), helper);
                replaced++;
            }
            yield return ins;
        }
        if (replaced == 0)
        {
            throw new InvalidOperationException($"SinkInfo.{original.Name}: manual job index not found");
        }
    }

    // ---- Payer of jobs created while another is cancelled ------------------------------------------------------

    public static void CancelPrefix(ResourceTransferJob __instance) => PrePurchases.PushJob(__instance);

    public static void CancelFinalizer(ResourceTransferJob __instance)
    {
        PrePurchases.PopJob();
        PrePurchases.CancelledFinalizer(__instance);
    }

    /// <summary>A cancelled pre-purchase that is not returned to the seller is refunded: hand its payers to the refund.</summary>
    public static void CancelPostfix(ResourceTransferJob __instance, bool returnPrePurchased, CostedQuantityTotal __result)
    {
        if (!returnPrePurchased && __instance.TradeType == TradeType.PrePurchased && __instance.PricePerResource > 0)
        {
            PrePurchases.CancelledRefund(__instance, __result.TotalValue / __instance.PricePerResource);
        }
    }

    /// <summary>
    /// ResourceManager.CarriedResourceTransfer prefix: inside a job's Cancel or delivery, the new job (a sell-back or a
    /// return) is indexed under that job's payer; a pre-purchase turned into a sell-back under its main payer (the sale
    /// itself is split over all of them, <see cref="PrePurchases.DeliverPostfix"/>).
    /// </summary>
    public static void CarriedPrefix(TradeType tradeType, out int? __state)
    {
        __state = Acting.Player;
        if (PrePurchases.CurrentJob is ResourceTransferJob job)
        {
            Acting.Swap(job.TradeType == TradeType.PrePurchased && tradeType == TradeType.SellBack ? PrePurchases.MajorPayerOf(job) : Get(job));
        }
    }

    public static void CarriedFinalizer(int? __state) => Acting.Swap(__state);
    // ---- Save and hash -----------------------------------------------------------------------------------------

    public static void WriteToPostfix(ResourceTransferJob __instance, GenericSerialWriter writer)
    {
        if (Get(__instance) is int p)
        {
            writer.WriteToPath(SavePath, p);
        }
        PrePurchases.WriteJob(__instance, writer);
    }

    public static void ReadPostfix(ResourceTransferJob __instance, GenericSerialReader reader)
    {
        if (reader.TryReadFromPath(SavePath, out int p))
        {
            Set(__instance, p);
        }
        PrePurchases.ReadJob(__instance, reader);
    }

    public static void HashPostfix(ResourceTransferJob __instance, IntegrityHash iHash)
    {
        (Get(__instance) ?? -1).GetIntegrityHash(iHash);
        PrePurchases.HashJob(__instance, iHash);
    }
}
