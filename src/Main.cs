using System;
using Cosmoteer.Game;
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
            if (App.Director.PeekState() is GameRoot game)
            {
                ShareMenu.Frame(game);
                // Ctrl+Shift+H = the menu's "Everyone (shared ship)" / "Make private" entry.
                if (App.Keyboard.HotkeyPressed(ViKey.Control, ViKey.Shift, ViKey.H))
                {
                    ShareActions.ToggleShared(game);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("frame", ex);
        }
    }
}
