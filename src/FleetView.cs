using System;
using System.Collections.Generic;
using Cosmoteer.Ships;
using Cosmoteer.Simulation;

namespace SharedShip;

/// <summary>
/// Makes teammates' shared ships part of SimShipsManager.LocalPlayerShips ("my fleet": control-group recall,
/// select-all-own on load, FTL readiness/fuel UI, comms caller pick, breadcrumbs...).
///
/// Only the *local-player* view is touched. GetShipsForPlayer / _shipsByPlayer stay keyed by real owner because
/// deterministic sim code iterates them (CareerSimModeManager:489/948, HumanPlayerShips) and would double-count a ship
/// listed under two players. LocalPlayerShips depends on LocalPlayerIndex, so vanilla never uses it for synced state.
/// </summary>
internal static class FleetView
{
    // One cache: there is a single live SimShipsManager per game. Rebuilt when the frame, the local player or any
    // relevant list size changes, so a ship added/removed mid-frame is never served stale. No allocation after warm-up.
    private static SimShipsManager? _manager;
    private static long _frame = -1;
    private static int _localPlayer;
    private static int _allCount = -1;
    private static int _ownCount = -1;
    private static IReadOnlyList<Ship>? _ownList;
    private static IReadOnlyList<Ship>? _result;
    private static readonly List<Ship> Combined = new();

    /// <summary>Incremented by Main once per rendered frame.</summary>
    public static long Frame;

    public static void LocalPlayerShipsPostfix(SimShipsManager __instance, ref IReadOnlyList<Ship> __result)
    {
        var game = __instance.Game;
        // -4 already returns every ship; negative indexes are never Career humans. Off the frame thread we return
        // vanilla's list rather than share the static cache across threads.
        if (game == null || game.LocalPlayerIndex < 0 || Environment.CurrentManagedThreadId != Main.MainThreadId)
        {
            return;
        }
        int local = game.LocalPlayerIndex;
        List<Ship> all = __instance._ships;
        IReadOnlyList<Ship> own = __result;
        if (ReferenceEquals(__instance, _manager) && _frame == Frame && _localPlayer == local
            && _allCount == all.Count && ReferenceEquals(_ownList, own) && _ownCount == own.Count && _result != null)
        {
            __result = _result;
            return;
        }

        _manager = __instance;
        _frame = Frame;
        _localPlayer = local;
        _allCount = all.Count;
        _ownList = own;
        _ownCount = own.Count;

        Combined.Clear();
        for (int i = 0; i < all.Count; i++)
        {
            Ship ship = all[i];
            if (SharedRules.IsSharedWith(ship, local))
            {
                if (Combined.Count == 0)
                {
                    for (int j = 0; j < own.Count; j++)
                    {
                        Combined.Add(own[j]);
                    }
                }
                Combined.Add(ship);
            }
        }
        // Nothing shared with us: hand back vanilla's own list untouched.
        _result = Combined.Count == 0 ? own : Combined;
        __result = _result;
    }
}
