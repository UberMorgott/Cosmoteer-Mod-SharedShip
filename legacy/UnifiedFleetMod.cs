using System.Runtime.InteropServices;
using HarmonyLib;
using Halfling.Logging;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Resources;
using Cosmoteer.Ships.Commands;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Modes;
using Cosmoteer.Crew;
using System.Collections.Generic;

// These attributes are required for the publicizer to work.
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Cosmoteer")]
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("HalflingCore")]

namespace unified_fleet_control
{
    public class Main
    {
        [UnmanagedCallersOnly]
        public static void InitializePatches()
        {
            var harmony = new Harmony("com.user.unifiedfleetcontrol");
            harmony.PatchAll();
            Logger.Log("Unified Fleet Control has been initialized! This mod enables shared resources and universal ship control for players 0-7.");
        }
    }

    // Patch to allow all human players (0-7) to share construction resources
    [HarmonyPatch(typeof(ResourceManager), "GetUsableConstructionResourceShips")]
    public class ResourceManager_GetUsableConstructionResourceShips_Patch
    {
        public static void Postfix(ResourceManager __instance, List<Ship> ships)
        {
            // If not in multiplayer, don't do anything.
            if (__instance.Ship.Sim.Game == null || !__instance.Ship.Sim.Game.NetManager.IsMultiplayer)
            {
                return;
            }

            // Use a HashSet for efficient checking of existing ships.
            var existingShips = new HashSet<Ship>(ships);

            // Iterate through all ships in the simulation.
            foreach (var ship in __instance.Ship.Sim.Ships.AllShips)
            {
                // Check if the ship belongs to players 0-7 and is not already in the list.
                // This ensures all players in the 0-7 range share construction resources
                if (ship.Metadata.PlayerIndex >= 0 && ship.Metadata.PlayerIndex <= 7 &&
                    !existingShips.Contains(ship))
                {
                    ships.Add(ship);
                    existingShips.Add(ship); // Keep the hash set updated.
                }
            }

            // Sort by distance to maintain proper priority as the original method does
            ships.Sort((s1, s2) => {
                float num = s1.DetWorldCenter.DistanceSquaredTo(__instance.Ship.DetWorldCenter);
                float value = s2.DetWorldCenter.DistanceSquaredTo(__instance.Ship.DetWorldCenter);
                return num.CompareTo(value);
            });
        }
    }

    // Also patch the CanShipHelpWithConstruction method to allow all allied ships to help with construction
    [HarmonyPatch(typeof(SimModeManager), "CanShipHelpWithConstruction")]
    public class SimModeManager_CanShipHelpWithConstruction_Patch
    {
        public static bool Prefix(SimModeManager __instance, Ship helper, Ship underConstruction, ref bool __result)
        {
            // If not in multiplayer, use original logic
            if (helper.Sim?.Game == null || !helper.Sim.Game.NetManager.IsMultiplayer)
            {
                return true; // Continue with original method
            }

            // For players 0-7, allow construction assistance regardless of normal rules
            if (helper.Metadata.PlayerIndex >= 0 && helper.Metadata.PlayerIndex <= 7 &&
                underConstruction.Metadata.PlayerIndex >= 0 && underConstruction.Metadata.PlayerIndex <= 7)
            {
                __result = true; // Allow helper to assist with construction
                return false; // Skip original method and return our result
            }

            return true; // Continue with original method
        }
    }

    // Patch to allow any human player to control any ship (crew related commands)
    [HarmonyPatch(typeof(CommandManager), "CanExecuteCrewCommandsFrom")]
    public class CommandManager_CanExecuteCrewCommandsFrom_Patch
    {
        public static bool Prefix(CommandManager __instance, int playerIndex, ref bool __result)
        {
            Ship ship = __instance.Ship; // Access the ship through the CommandManager

            // If not in multiplayer, use original logic
            if (ship.Sim?.Game == null || !ship.Sim.Game.NetManager.IsMultiplayer)
            {
                return true; // Continue with original method
            }

            // For human players (0-7), allow control of any ship in the same range
            if (playerIndex >= 0 && playerIndex <= 7 &&
                ship.Metadata.PlayerIndex >= 0 && ship.Metadata.PlayerIndex <= 7)
            {
                __result = true; // Allow player to execute commands on this ship
                return false; // Skip original method and return our result
            }

            return true; // Continue with original method
        }
    }

    // Patch to allow any human player to control any ship (general commands)
    [HarmonyPatch(typeof(CommandManager), "IsCommandableBy")]
    public class CommandManager_IsCommandableBy_Patch
    {
        public static bool Prefix(CommandManager __instance, int playerIndex, ref bool __result)
        {
            Ship ship = __instance.Ship; // Access the ship through the CommandManager

            // If not in multiplayer, use original logic
            if (ship.Sim?.Game == null || !ship.Sim.Game.NetManager.IsMultiplayer)
            {
                return true; // Continue with original method
            }

            // For human players (0-7), allow control of any ship in the same range
            if (playerIndex >= 0 && playerIndex <= 7 &&
                ship.Metadata.PlayerIndex >= 0 && ship.Metadata.PlayerIndex <= 7)
            {
                __result = true; // Allow player to control this ship
                return false; // Skip original method and return our result
            }

            return true; // Continue with original method
        }
    }


    // Patch to make all players share resources by making them allied
    [HarmonyPatch(typeof(SimModeManager), "AreAllies")]
    public class SimModeManager_AreAllies_Patch
    {
        public static bool Prefix(SimModeManager __instance, int player1, int player2, ref bool __result)
        {
            // If not in multiplayer, use original logic
            if (__instance.Game == null || !__instance.Game.NetManager.IsMultiplayer)
            {
                return true; // Continue with original method
            }

            // For human players 0-7, treat them as allies
            if ((player1 >= 0 && player1 <= 7) && (player2 >= 0 && player2 <= 7) && (player1 != player2))
            {
                __result = true; // Treat them as allies
                return false; // Skip original method and return our result
            }

            return true; // Continue with original method
        }
    }

    // Patch to make all players the same player for resource sharing
    [HarmonyPatch(typeof(SimModeManager), "AreSamePlayer")]
    public class SimModeManager_AreSamePlayer_Patch
    {
        public static bool Prefix(SimModeManager __instance, int player1, int player2, ref bool __result)
        {
            // If not in multiplayer, use original logic
            if (__instance.Game == null || !__instance.Game.NetManager.IsMultiplayer)
            {
                return true; // Continue with original method
            }

            // For human players 0-7, treat them as the same player
            if ((player1 >= 0 && player1 <= 7) && (player2 >= 0 && player2 <= 7))
            {
                __result = true; // Treat them as the same player
                return false; // Skip original method and return our result
            }

            return true; // Continue with original method
        }
    }

    // Patch to allow resource trading between allied players in the 0-7 range
    [HarmonyPatch(typeof(SimModeManager), "AllowResourceTrade")]
    public class SimModeManager_AllowResourceTrade_Patch
    {
        public static bool Prefix(SimModeManager __instance, Ship caller, Ship callee, ref bool __result)
        {
            // If not in multiplayer, use original logic
            if (caller?.Sim?.Game == null || !caller.Sim.Game.NetManager.IsMultiplayer)
            {
                return true; // Continue with original method
            }

            // For human players 0-7, allow resource trading between ships
            if ((caller.Metadata.PlayerIndex >= 0 && caller.Metadata.PlayerIndex <= 7) &&
                (callee.Metadata.PlayerIndex >= 0 && callee.Metadata.PlayerIndex <= 7))
            {
                __result = true; // Allow resource trading
                return false; // Skip original method and return our result
            }

            return true; // Continue with original method
        }
    }
}