using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer.MPInputs;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Crew.Jobs;
using HarmonyLib;

namespace SharedShip;

/// <summary>
/// Per-player wallet for trades on a shared ship (0.30.5). Everything else economic (construction sourcing, hauling,
/// assist) follows Ship.IsSamePlayerAs, see <see cref="OwnershipPostfixes"/>.
///   - Trade money: ResourceTransferJob credits/debits job.Ship.Metadata.PlayerIndex (ResourceTransferJob.cs:189-197,
///     1030, 1471-1483, 1698-1706, 1729-1737), i.e. the owner of the ship holding the job; selling from a shared ship puts
///     the job on the shared ship (MPMoveResourcesInput.GetJobOwner), so the owner got paid. Those reads now use
///     <see cref="SharedRules.PayerIndex"/> (the issuing teammate).
///   - <see cref="RejectForgedIssuer"/>: PayerIndex trusts ManualID.PlayerID, which the sending client fills in, so a
///     trade claiming another known player as issuer is refused (vanilla clients always send their own ID).
/// Deterministic: every value read is synced sim state (names, owners, mode, lockstep player list) or part of the input /
/// serialized job, the same on every client; nothing local is used.
/// </summary>
internal static class SharedResources
{
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
        MethodInfo payer = typeof(SharedRules).GetMethod(nameof(SharedRules.PayerIndex))!;
        var list = instructions.ToList();
        int replaced = 0;
        for (int i = 1; i + 2 < list.Count; i++)
        {
            if (list[i - 1].opcode == OpCodes.Ldarg_0 && list[i].Calls(getShip) && list[i + 1].Calls(getMetadata) && list[i + 2].Calls(getPlayerIndex))
            {
                list[i].opcode = OpCodes.Call;
                list[i].operand = payer;
                list[i + 1].opcode = OpCodes.Nop;
                list[i + 1].operand = null;
                list[i + 2].opcode = OpCodes.Nop;
                list[i + 2].operand = null;
                replaced++;
            }
        }
        int expected = MoneyMethods.First(m => m.Name == original.Name).OwnerReads;
        // Partial rewrites would debit one player and refund another: require the exact 0.30.5 count.
        return replaced == expected
            ? list
            : throw new InvalidOperationException($"ResourceTransferJob.{original.Name}: {replaced} owner reads, expected {expected}");
    }

    /// <summary>Prefix of MPMoveResourcesInput.Execute: skip trades whose ManualID names another known player.</summary>
    public static bool RejectForgedIssuer(MPMoveResourcesInput __instance, GameRoot game, int playerIndex)
    {
        return __instance.TradeType is not (TradeType.Buy or TradeType.Sell)
            || SharedRules.IsHonestIssuer(SharedRules.ConnectedPlayerIndex(__instance.ManualID.PlayerID, game.HumanPlayersByID), playerIndex);
    }
}
