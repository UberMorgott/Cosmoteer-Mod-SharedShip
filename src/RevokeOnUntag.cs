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
/// Deterministic inputs only: tag before/after (synced name), Metadata.PlayerIndex, the command's author
/// (CommandID.PlayerID, part of the synced command) mapped through GameRoot.GetHumanPlayerIndex, which BaseMPManager
/// itself uses for every input. Only confirmed commands are touched; a null author (system/AI) is kept, as is anything
/// the owner authored.
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
        if (!__state || ship == null || SharedRules.HasTag(ship))
        {
            return;
        }
        int owner = ship.Metadata.PlayerIndex;
        int removed = ship.Commands.RemoveAll(c => c is DirectControlCommand
            && c.IsConfirmed
            && c.UniqueID.PlayerID is { } author
            && game.GetHumanPlayerIndex(author, allowUnknown: true) != owner);
        if (removed > 0)
        {
            Log.Info($"Untagged '{ship.Metadata.PlainShipName}': cancelled {removed} teammate direct-control command(s)");
        }
    }
}
