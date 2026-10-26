using Cosmoteer;
using Cosmoteer.Game;
using Cosmoteer.Modes.Career;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Commands;

namespace SharedShip;

/// <summary>
/// Replaces MPTransferShipControlInput.Execute (MPTransferShipControlInput.cs, 0.30.5) so that:
///   - besides the owner, a player with shared access (<see cref="SharedRules.IsSharedWith"/>) may transfer the ship,
///     including to themselves ("take it");
///   - a transferred ship stops being shared: the [S] tag is removed in the same input, and teammates' direct control
///     (anyone but the new owner, via <see cref="RevokeOnUntag.CancelTeammateDirectControl"/>) is cancelled.
/// Everything else is vanilla: unknown target -> no-op, PlayerIndex and OriginalPlayerIndex set, current confirmed
/// direct control removed. Non-shared ships of other players stay non-transferable by non-owners.
/// Deterministic: sender index, ship name/owner and target ID are all part of synced state or the input itself, and
/// the name is changed inside the lockstep input exactly like MPSetShipInfoInput.Execute does.
/// </summary>
internal static class TransferShared
{
    public static bool Prefix(MPTransferShipControlInput __instance, GameRoot game, int playerIndex)
    {
        Ship? ship = __instance.Ship;
        if (ship == null)
        {
            return false;
        }
        if (ship.Metadata.PlayerIndex != playerIndex && !SharedRules.IsSharedWith(ship, playerIndex))
        {
            return false;
        }
        int to = game.GetHumanPlayerIndex(__instance.ToPlayerID, allowUnknown: true);
        if (to == -5)
        {
            return false;
        }
        bool wasShared = SharedRules.HasTag(ship);
        if (wasShared)
        {
            ship.Metadata.PlainShipName = TextUtils.SanitizeShipName(SharedRules.WithoutTag(ship.Metadata.PlainShipName));
        }
        ship.Metadata.PlayerIndex = to;
        ship.Metadata.OriginalPlayerIndex = to;
        if (ship.Commands.CurConfirmedCommand is DirectControlCommand command)
        {
            ship.Commands.RemoveCommand(command);
        }
        if (wasShared)
        {
            RevokeOnUntag.CancelTeammateDirectControl(ship, game);
        }
        return false;
    }
}
