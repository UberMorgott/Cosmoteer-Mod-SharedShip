using System;
using Cosmoteer.Game;
using Cosmoteer.Localization;
using Cosmoteer.Modes.Career;
using Halfling.Gui;

namespace SharedShip;

/// <summary>
/// Adds SharedShip entries to the vanilla Career ship menu "Transfer ship control..." that
/// CareerGameModeManager.CreateModeSpecificGui builds (CareerGameModeManager.cs:1858-1890, only when the game has more
/// than one player). Vanilla lists every other player; we append:
///   - "Myself": take the selected ships (MPTransferShipControlInput to the local player; for a teammate's shared ship
///     this works through <see cref="TransferShared"/>, and the ship becomes the taker's private ship);
///   - "Everyone (shared ship)" / "Make private": toggle the [S] tag (same as Ctrl+Shift+H).
/// The vanilla per-player items keep working and, for shared ships, also strip the tag (TransferShared).
/// </summary>
internal static class ShareMenu
{
    private const string KeyShare = "SharedShip/ShareAll";
    private const string KeyPrivate = "SharedShip/MakePrivate";
    private const string KeySelf = "SharedShip/ToSelf";

    // Weak: must not keep a finished game's GUI alive. Only touched on the frame thread.
    private static WeakReference<MenuItem>? _toggleItem;
    private static WeakReference<GameRoot>? _toggleGame;
    private static bool _showsPrivate;

    public static void CreateModeSpecificGuiPostfix(CareerGameModeManager __instance)
    {
        try
        {
            AddItems(__instance);
        }
        catch (Exception ex)
        {
            // GUI only: a failure here must not break the game; sharing still works via the name tag / hotkey.
            Log.Error("menu", ex);
        }
    }

    private static void AddItems(CareerGameModeManager mode)
    {
        var players = mode.Init.MPPlayers;
        if (players == null || players.Count <= 1)
        {
            return; // vanilla did not create the transfer menu either
        }
        GameRoot game = mode.Game;
        var card = game.Gui.ShipGui.ShipsCard;
        var children = card.Menu.MenuBox.Children;
        // Vanilla moved its sub-category right after the "transfer crew or resources" button (line 1863).
        int index = children.IndexOf(card.TransferCrewOrResourcesButton) + 1;
        if (index <= 0 || index >= children.Count || children[index] is not MenuSubCategory transferMenu)
        {
            Log.Info("WARN transfer-control menu not found; menu entries not added (Ctrl+Shift+H still works)");
            return;
        }

        transferMenu.AddSeparator();
        transferMenu.AddItem(Strings.KeyString(KeySelf), null, (_, _) => ShareActions.TakeSelected(game));
        MenuItem toggle = transferMenu.AddItem(Strings.KeyString(KeyShare), null, (_, _) => ShareActions.ToggleShared(game));
        _toggleItem = new WeakReference<MenuItem>(toggle);
        _toggleGame = new WeakReference<GameRoot>(game);
        _showsPrivate = false;
        Log.Info("Transfer-control menu entries added");
    }

    /// <summary>Called once per frame: label follows the current selection ("share" vs "make private").</summary>
    public static void Frame(GameRoot game)
    {
        if (_toggleItem == null || _toggleGame == null
            || !_toggleGame.TryGetTarget(out GameRoot? owner) || !ReferenceEquals(owner, game)
            || !_toggleItem.TryGetTarget(out MenuItem? item))
        {
            return;
        }
        bool showPrivate = ShareActions.AllSelectedShared(game);
        if (showPrivate != _showsPrivate)
        {
            _showsPrivate = showPrivate;
            item.TextProvider = Strings.KeyString(showPrivate ? KeyPrivate : KeyShare);
        }
    }
}
