using System.Collections.Generic;
using Cosmoteer.Ships;
using Cosmoteer.Simulation;

namespace SharedShip;

/// <summary>
/// Makes teammates' shared ships part of SimShipsManager.LocalPlayerShips ("my fleet": control-group recall,
/// select-all-own on load, direct-control cancel, FTL readiness/fuel UI, comms caller pick, breadcrumbs...).
///
/// Only the *local-player* view is touched. GetShipsForPlayer / _shipsByPlayer stay keyed by real owner because
/// deterministic sim code iterates them (CareerSimModeManager:489/948, HumanPlayerShips) and would double-count a ship
/// listed under two players. LocalPlayerShips depends on LocalPlayerIndex, so vanilla never uses it for synced state.
///
/// No cache and no static state: the answer always matches the current names/owners/ship set, nothing outlives a game,
/// and nested enumerations cannot see a list being rebuilt. Cost is one pass over the ship list per call (owner match
/// and tag test per ship); a new list is allocated only when something is actually shared with the local player.
/// </summary>
internal static class FleetView
{
    public static void LocalPlayerShipsPostfix(SimShipsManager __instance, ref IReadOnlyList<Ship> __result)
    {
        var game = __instance.Game;
        // -4 already returns every ship; negative indexes are never Career humans.
        if (game == null || game.LocalPlayerIndex < 0)
        {
            return;
        }
        int local = game.LocalPlayerIndex;
        List<Ship> all = __instance._ships;
        IReadOnlyList<Ship> own = __result;
        List<Ship>? combined = null;
        for (int i = 0; i < all.Count; i++)
        {
            Ship ship = all[i];
            if (SharedRules.IsSharedWith(ship, local))
            {
                if (combined == null)
                {
                    combined = new List<Ship>(own.Count + 4);
                    for (int j = 0; j < own.Count; j++)
                    {
                        combined.Add(own[j]);
                    }
                }
                combined.Add(ship);
            }
        }
        if (combined != null)
        {
            __result = combined;
        }
    }
}
