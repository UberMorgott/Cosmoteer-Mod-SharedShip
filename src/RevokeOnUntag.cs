using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer.MPInputs;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Commands;

namespace SharedShip;

/// <summary>
/// When a rename removes the [S] tag, teammates lose command rights, but a DirectControlCommand they issued earlier
/// would keep thrusting/firing (later inputs are refused, the running command is not). This cancels those commands in the
/// same lockstep input that removed the tag, so every client does it at the same tick.
///
/// Scope: only commands whose author had access *solely* through sharing, i.e. the ship was tagged before the rename
/// and the author passes <see cref="SharedRules.IsTeammateOfOwner"/> (Career, other human slot than the owner). Owner,
/// spectator/creative "all players" index -4, AI/system (null author) and every non-Career mode are never touched.
/// Rename does not change owner, mode or human slots, so evaluating IsTeammateOfOwner after Execute equals evaluating
/// IsSharedWith before it.
///
/// Deterministic inputs only: tag before/after (synced name), Metadata.PlayerIndex, the command's author
/// (CommandID.PlayerID, part of the synced command) mapped through GameRoot.GetHumanPlayerIndex, which BaseMPManager
/// itself uses for every input. Only confirmed (synced) commands are removed.
/// </summary>
internal static class RevokeOnUntag
{
    public static void Prefix(MPSetShipInfoInput __instance, out bool __state)
    {
        Ship? ship = __instance.Ship;
        __state = ship != null && SharedRules.HasTag(ship);
    }

    public static void Postfix(MPSetShipInfoInput __instance, GameRoot game, bool __state)
    {
        Ship? ship = __instance.Ship;
        if (__state && ship != null && !SharedRules.HasTag(ship))
        {
            CancelTeammateDirectControl(ship, game);
        }
    }

    /// <summary>
    /// Removes confirmed DirectControlCommands on <paramref name="ship"/> authored by a Career teammate of its current
    /// owner. Call only at a point where the ship has just stopped being shared (lockstep input execution).
    /// </summary>
    public static void CancelTeammateDirectControl(Ship ship, GameRoot game)
    {
        int removed = ship.Commands.RemoveAll(c => c is DirectControlCommand
            && c.IsConfirmed
            && c.UniqueID.PlayerID is { } author
            && SharedRules.IsTeammateOfOwner(ship, game.GetHumanPlayerIndex(author, allowUnknown: true)));
        if (removed > 0)
        {
            Log.Info($"'{ship.Metadata.PlainShipName}' no longer shared: cancelled {removed} teammate direct-control command(s)");
        }
    }
}
