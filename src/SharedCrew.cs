using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Crew;
using Cosmoteer.Game;
using Cosmoteer.Modes;
using Cosmoteer.Modes.Career;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Commands;
using Cosmoteer.Simulation;
using HarmonyLib;

namespace SharedShip;

/// <summary>
/// Crew limit and crew moves for shared ships (0.30.5).
/// Vanilla: a player's crew = SimCrewManager.CountCrewForPlayer (crew of the ships they own + their homeless crew,
/// SimCrewManager.cs:421) + the counts saved per sector when the fleet left it (MapNodeData.HumanPlayerCrew,
/// CareerGameModeManager.cs:633, summed at :1552); the limit is one fame-based number for everyone
/// (CareerSimModeManager.GetMaxAdditionalPlayerCrew, :844). <see cref="CountPostfix"/> is the one chokepoint: a shared
/// ship's crew counts split equally over every player of the career (<see cref="SharedRules.CrewShare"/>), so crew may
/// move freely between a player's ships and shared ships (<see cref="OwnerPairTranspiler"/>, <see cref="CapacityPrefix"/>)
/// and homeless crew may adopt shared ships (<see cref="AdoptTranspiler"/>).
/// Players of the career (<see cref="Roster"/>): connected players, owners of ships in this sector or mid-jump, and every
/// player saved in another sector's crew counts; <see cref="SwitchingPostfix"/> saves offline players too, so somebody who
/// left keeps their share. All of it is synced and saved vanilla state.
/// </summary>
internal static class SharedCrew
{
    private static IEnumerable<Ship> ShipsWithCrew(SimRoot sim) => sim.Ships.Union(FtlLocalJumpCommand.GetJumpingShips(sim));

    /// <summary>Sorted distinct human slots of the career, offline players included.</summary>
    public static List<int> Roster(GameRoot game, SimRoot sim)
    {
        var roster = new List<int>();
        if (sim.Mode is not CareerSimModeManager career)
        {
            return roster;
        }
        foreach (int p in game.HumanPlayersIndexes)
        {
            if (career.IsHumanPlayerAllegiance(p))
            {
                SharedRules.AddSorted(roster, p);
            }
        }
        foreach (Ship ship in ShipsWithCrew(sim))
        {
            if (career.IsHumanPlayerAllegiance(ship.Metadata.PlayerIndex))
            {
                SharedRules.AddSorted(roster, ship.Metadata.PlayerIndex);
            }
        }
        if (game.Mode is CareerGameModeManager mode)
        {
            foreach (var (node, data) in mode._nodeData)
            {
                if (node != game.CurrentMapNode)
                {
                    foreach (int p in data.HumanPlayerCrew.Keys)
                    {
                        SharedRules.AddSorted(roster, p);
                    }
                }
            }
        }
        return roster;
    }

    /// <summary>SimCrewManager.CountCrewForPlayer: a shared ship counts its split instead of all of it for the owner.</summary>
    public static void CountPostfix(SimCrewManager __instance, int playerIndex, ref int __result)
    {
        SimRoot sim = __instance.Sim;
        if (sim?.Game is not GameRoot game)
        {
            return;
        }
        List<int>? roster = null;
        foreach (Ship ship in ShipsWithCrew(sim))
        {
            if (SharedRules.IsShared(ship))
            {
                roster ??= Roster(game, sim);
                int crew = ship.Crew.Assigned.Count;
                __result += SharedRules.CrewShare(crew, roster, playerIndex) - (ship.Metadata.PlayerIndex == playerIndex ? crew : 0);
            }
        }
    }

    /// <summary>Leaving a sector also saves the crew counts of offline players (vanilla saves connected ones, :631-633).</summary>
    public static void SwitchingPostfix(CareerGameModeManager __instance)
    {
        GameRoot game = __instance.Game;
        var counts = __instance.CurrentMapNodeData.HumanPlayerCrew;
        foreach (int p in Roster(game, __instance.Sim))
        {
            if (!game.HumanPlayersIndexes.Contains(p))
            {
                counts[p] = __instance.Sim.Crew.CountCrewForPlayer(p);
            }
        }
    }

    // ---- Moves, adoption, salvage ------------------------------------------------------------------------------------

    private static readonly MethodInfo GetMetadata = AccessTools.PropertyGetter(typeof(Ship), nameof(Ship.Metadata));
    private static readonly MethodInfo GetPlayerIndex = AccessTools.PropertyGetter(typeof(ShipMetadata), nameof(ShipMetadata.PlayerIndex));
    private static readonly MethodInfo IsSamePlayerAs = typeof(Ship).GetMethod(nameof(Ship.IsSamePlayerAs), new[] { typeof(Ship) })!;

    private static bool IsOwnerRead(List<CodeInstruction> list, int i) => list[i].Calls(GetMetadata) && list[i + 1].Calls(GetPlayerIndex);

    private static void Nop(List<CodeInstruction> list, int i) => list[i] = new CodeInstruction(OpCodes.Nop).MoveLabelsFrom(list[i]);

    /// <summary>
    /// "a.Metadata.PlayerIndex ... b.Metadata.PlayerIndex; ceq" becomes "a ... b; IsSamePlayerAs" (vanilla equality plus
    /// shared pairs). MPSetCrewHomeShipInput's crew-limit lambda (MPSetCrewHomeShipInput.cs:89).
    /// </summary>
    public static IEnumerable<CodeInstruction> OwnerPairTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var list = instructions.ToList();
        int first = Enumerable.Range(0, list.Count - 1).FirstOrDefault(i => IsOwnerRead(list, i), -1);
        int second = first < 0 ? -1 : Enumerable.Range(first + 2, list.Count - first - 3).FirstOrDefault(i => IsOwnerRead(list, i), -1);
        if (second < 0 || list[second + 2].opcode != OpCodes.Ceq)
        {
            throw new InvalidOperationException($"{original.DeclaringType?.Name}.{original.Name}: owner comparison not found");
        }
        Nop(list, first);
        Nop(list, first + 1);
        Nop(list, second);
        Nop(list, second + 1);
        list[second + 2] = new CodeInstruction(OpCodes.Callvirt, IsSamePlayerAs).MoveLabelsFrom(list[second + 2]);
        return list;
    }

    /// <summary>UI twin of the crew-limit check (CrewAndResourceTransferWindow.cs:984-994): own pairs have no limit.</summary>
    public static bool CapacityPrefix(Ship ship, Ship? fromShip, ref int __result)
    {
        if (fromShip == null || !fromShip.IsSamePlayerAs(ship))
        {
            return true;
        }
        __result = ship.Crew.Assigned.Count + ship.Crew.RemainingCrewCapacity;
        return false;
    }

    /// <summary>CrewSoul.FindNewHomeShip (:1347): "ship owner == crew player" becomes <see cref="SharedRules.IsOwnFor"/>.</summary>
    public static IEnumerable<CodeInstruction> AdoptTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo crewPlayer = AccessTools.PropertyGetter(typeof(CrewSoul), nameof(CrewSoul.PlayerIndex));
        var list = instructions.ToList();
        int i = Enumerable.Range(0, list.Count - 4).FirstOrDefault(k => IsOwnerRead(list, k) && list[k + 2].opcode == OpCodes.Ldarg_0
            && list[k + 3].Calls(crewPlayer) && (list[k + 4].opcode == OpCodes.Bne_Un_S || list[k + 4].opcode == OpCodes.Bne_Un), -1);
        if (i < 0)
        {
            throw new InvalidOperationException("CrewSoul.FindNewHomeShip: owner check not found");
        }
        Nop(list, i);
        Nop(list, i + 1);
        list[i + 4].opcode = OpCodes.Brfalse;
        list.Insert(i + 4, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SharedRules), nameof(SharedRules.IsOwnFor))));
        return list;
    }

    /// <summary>Vanilla own-ship salvage, with "own" judged ship-to-ship (a shared ship salvages a teammate's own ship).</summary>
    public static bool CanSalvageFrom(SimModeManager mode, Ship source, Ship target, bool allowSalvageOwnShip) =>
        mode.CanSalvage(source.Metadata.PlayerIndex, target, allowSalvageOwnShip) || (allowSalvageOwnShip && source.IsSamePlayerAs(target));

    /// <summary>
    /// "CanSalvage(source.Metadata.PlayerIndex, target, own)" becomes "CanSalvageFrom(mode, source, target, own)"
    /// (SalvageCommand.IsValid :71, MPSalvagePartInput.Execute :79).
    /// </summary>
    public static IEnumerable<CodeInstruction> SalvageTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        MethodInfo canSalvage = AccessTools.Method(typeof(SimModeManager), nameof(SimModeManager.CanSalvage));
        var list = instructions.ToList();
        int call = list.FindIndex(ins => ins.Calls(canSalvage));
        int read = call < 0 ? -1 : Enumerable.Range(0, call - 1).LastOrDefault(k => IsOwnerRead(list, k), -1);
        if (read < 0 || list.FindIndex(call + 1, ins => ins.Calls(canSalvage)) >= 0)
        {
            throw new InvalidOperationException($"{original.DeclaringType?.Name}.{original.Name}: salvage check not found");
        }
        Nop(list, read);
        Nop(list, read + 1);
        list[call] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SharedCrew), nameof(CanSalvageFrom))).MoveLabelsFrom(list[call]);
        return list;
    }
}
