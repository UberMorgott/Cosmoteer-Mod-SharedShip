using System.Collections.Generic;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer.MPInputs;
using Cosmoteer.Modes.Career;
using Cosmoteer.Ships;

namespace SharedShip;

/// <summary>
/// Local-player actions on the current selection. They only enqueue vanilla lockstep inputs; the effect happens when
/// those inputs execute on every client.
/// </summary>
internal static class ShareActions
{
    /// <summary>Selected ships the local player may command (owned or shared with them), snapshot.</summary>
    public static List<Ship> SelectedCommandable(GameRoot game)
    {
        var ships = new List<Ship>();
        foreach (Ship ship in game.Sim.PlayerInput.SelectedShips)
        {
            if (SharedRules.IsCommandableByLocalPlayer(ship.Commands))
            {
                ships.Add(ship);
            }
        }
        return ships;
    }

    /// <summary>True when every selected commandable ship is already shared (so the action is "make private").</summary>
    public static bool AllSelectedShared(GameRoot game)
    {
        bool any = false;
        foreach (Ship ship in game.Sim.PlayerInput.SelectedShips)
        {
            if (!SharedRules.IsCommandableByLocalPlayer(ship.Commands))
            {
                continue;
            }
            if (!SharedRules.HasTag(ship))
            {
                return false;
            }
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Shares every selected commandable ship, or, if all of them are already shared, makes them private again.
    /// Uses the vanilla rename input (MPSetShipInfoInput, same call as ShipInfoDialog.OnOkayClicked).
    /// </summary>
    public static void ToggleShared(GameRoot game)
    {
        bool makePrivate = AllSelectedShared(game);
        foreach (Ship ship in SelectedCommandable(game))
        {
            string name = ship.Metadata.PlainShipName;
            bool tagged = SharedRules.HasTag(ship);
            if (tagged == !makePrivate)
            {
                continue; // already in the requested state
            }
            string newName = makePrivate ? SharedRules.WithoutTag(name) : SharedRules.WithTag(name);
            game.NetManager.EnqueueInput(new MPSetShipInfoInput(ship, newName, ship.Metadata.Author, ship.Metadata.Description));
            Log.Info($"Rename '{name}' -> '{newName}'");
        }
    }

    /// <summary>
    /// Transfers every selected commandable ship to the local player through the vanilla MPTransferShipControlInput;
    /// <see cref="TransferShared"/> lets a teammate do this for a shared ship and removes the tag.
    /// </summary>
    public static void TakeSelected(GameRoot game)
    {
        foreach (Ship ship in SelectedCommandable(game))
        {
            game.NetManager.EnqueueInput(new MPTransferShipControlInput(ship, game.NetManager.LocalPlayerID));
        }
    }
}
