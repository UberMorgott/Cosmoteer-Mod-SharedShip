using System;
using System.Collections.Generic;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer.MPInputs;
using Cosmoteer.Ships;
using Halfling;
using Halfling.Input;

namespace SharedShip;

/// <summary>
/// Entry point called by Yet Another Mod Loader (public static void GameLoadInitializer()). The loader calls it from its
/// Application.Main prefix, i.e. before any game code has run.
/// </summary>
public static class Main
{
    private static bool _initialized;

    public static void GameLoadInitializer()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        Log.Init();
        Patcher.Apply();
        if (!Patcher.Enabled)
        {
            return; // rolled back; Patcher logged why
        }
        App.Director.FrameEnded += OnFrameEnded;
        Log.Info("SharedShip initialized");
    }

    private static void OnFrameEnded(object? sender, EventArgs e)
    {
        try
        {
            // Only while the game itself is on top (no dialog / text box focused there).
            if (App.Director.PeekState() is GameRoot game && App.Keyboard.HotkeyPressed(ViKey.Control, ViKey.Shift, ViKey.H))
            {
                ToggleSelected(game);
            }
        }
        catch (Exception ex)
        {
            Log.Error("frame", ex);
        }
    }

    /// <summary>
    /// Ctrl+Shift+H: add/remove the [S] tag on every selected ship the local player may command, through the vanilla
    /// lockstep rename input (MPSetShipInfoInput, same call as ShipInfoDialog.OnOkayClicked) so all clients agree.
    /// </summary>
    private static void ToggleSelected(GameRoot game)
    {
        var ships = new List<Ship>();
        foreach (Ship ship in game.Sim.PlayerInput.SelectedShips)
        {
            if (SharedRules.IsCommandableByLocalPlayer(ship.Commands))
            {
                ships.Add(ship);
            }
        }
        foreach (Ship ship in ships)
        {
            string name = ship.Metadata.PlainShipName;
            string newName = SharedRules.HasTag(ship)
                ? name.Replace(SharedRules.Tag, string.Empty, StringComparison.Ordinal).Trim()
                : SharedRules.Tag + " " + name;
            game.NetManager.EnqueueInput(new MPSetShipInfoInput(ship, newName, ship.Metadata.Author, ship.Metadata.Description));
            Log.Info($"Rename '{name}' -> '{newName}'");
        }
    }
}
