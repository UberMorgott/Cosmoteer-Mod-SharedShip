using System;
using Cosmoteer.Game;
using Cosmoteer.Modes.Career;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Commands;
using Cosmoteer.Ships.Crew.Jobs;

namespace SharedShip;

/// <summary>
/// The single source of truth for "ship S is player P's own" (player-to-ship, via CommandManager and CanSalvage) and
/// "ships A and B belong to one player" (ship-to-ship, via Ship.IsSamePlayerAs). Both reduce to <see cref="IsSharedWith"/>.
///
/// Determinism: in lockstep MP every client runs AllowMPInput + Execute for every input and compares state hashes
/// (BaseMPManager), and Execute bodies call CommandManager.IsCommandableBy(senderIndex). So the answer must be
/// identical on all clients. It only reads synced sim state:
///   - ShipMetadata.PlainShipName (changed only through the lockstep MPSetShipInfoInput),
///   - ShipMetadata.PlayerIndex,
///   - the sim mode type and CareerSimModeManager.IsHumanPlayerAllegiance (0 &lt;= index &lt; Rules.MaxPlayers).
/// Nothing local (LocalPlayerIndex, settings, wall clock) is used unless the caller passes it in, exactly like vanilla.
/// </summary>
internal static partial class SharedRules
{
    /// <summary>True when the ship's name carries <see cref="Tag"/>.</summary>
    public static bool HasTag(Ship ship) => HasTag(ship.Metadata.PlainShipName);

    /// <summary>
    /// Extra permission granted by this mod on top of vanilla ownership: <paramref name="playerIndex"/> is another human
    /// player of a Career game and the ship is a [S]-tagged ship owned by a human player. AI factions (negative indexes or
    /// indexes &gt;= MaxPlayers) are never involved, and other game modes (PvP, creative) stay vanilla.
    /// </summary>
    public static bool IsSharedWith(Ship ship, int playerIndex)
    {
        return HasTag(ship) && IsTeammateOfOwner(ship, playerIndex);
    }

    /// <summary>
    /// The tag-independent half of <see cref="IsSharedWith"/>: Career game, owner and <paramref name="playerIndex"/> are
    /// two different human slots. Used to tell "had access only through sharing" apart from vanilla access.
    /// </summary>
    public static bool IsTeammateOfOwner(Ship ship, int playerIndex)
    {
        int owner = ship.Metadata.PlayerIndex;
        if (!AreDistinctPlayers(owner, playerIndex))
        {
            return false;
        }
        // Sim.Mode throws while a ship is being deserialized before the mode is set; IsSharedWith's tag check runs first,
        // so this is rare on the hot path.
        try
        {
            return ship.Sim?.Mode is CareerSimModeManager career
                && career.IsHumanPlayerAllegiance(owner)
                && career.IsHumanPlayerAllegiance(playerIndex);
        }
        catch (NullReferenceException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ship-to-ship half of the rule, added to vanilla Ship.IsSamePlayerAs(Ship): two ships of different Career humans
    /// count as one player's when either is shared. Symmetric.
    /// </summary>
    public static bool IsSharedPair(Ship a, Ship b)
    {
        int bOwner = b.Metadata.PlayerIndex;
        return IsSharedPair(HasTag(a), a.Metadata.PlayerIndex, HasTag(b), bOwner) && IsTeammateOfOwner(a, bOwner);
    }

    /// <summary>
    /// Player whose money a trade job moves (replaces job.Ship.Metadata.PlayerIndex in ResourceTransferJob's money code):
    /// the job's recorded payer (<see cref="Payers"/>: the lockstep sender that created it), else the owner (vanilla; auto
    /// jobs and jobs from older saves). Independent of the [S] tag and of later owner changes on purpose: debit and refund
    /// of one job must hit the same player.
    /// </summary>
    public static int PayerIndex(ResourceTransferJob job) => Payers.Get(job) ?? job.Ship!.Metadata.PlayerIndex;

    /// <summary>A shared ship of a Career human: [S] tag and a human-slot owner.</summary>
    public static bool IsShared(Ship ship) =>
        HasTag(ship) && ship.Metadata.PlayerIndex >= 0 && ship.Sim?.Mode is CareerSimModeManager career
        && career.IsHumanPlayerAllegiance(ship.Metadata.PlayerIndex);

    /// <summary>"<paramref name="ship"/> is <paramref name="playerIndex"/>'s own": owner, or shared with them.</summary>
    public static bool IsOwnFor(Ship ship, int playerIndex) =>
        ship.Metadata.PlayerIndex == playerIndex || IsSharedWith(ship, playerIndex);
    // ---- Replacements for the vanilla CommandManager members (bodies mirror CommandManager.cs:327-737 of 0.30.5). ----
    // Signatures are "instance as first argument" so a transpiler can swap call/callvirt for call 1:1 on the IL stack.

    /// <summary>Vanilla: playerIndex == -4 (spectator/"all") || playerIndex == owner. Plus: shared with playerIndex.</summary>
    public static bool IsCommandableBy(CommandManager commands, int playerIndex)
    {
        Ship ship = commands.Ship!;
        return playerIndex == -4 || playerIndex == ship.Metadata.PlayerIndex || IsSharedWith(ship, playerIndex);
    }

    public static bool CanExecuteCommandsFrom(CommandManager commands, int playerIndex)
    {
        return commands.CanExecuteCommands && IsCommandableBy(commands, playerIndex);
    }

    public static bool CanExecuteCrewCommandsFrom(CommandManager commands, int playerIndex)
    {
        return commands.Ship!.Crew.Assigned.Count > 0 && IsCommandableBy(commands, playerIndex);
    }

    public static bool IsCommandableByLocalPlayer(CommandManager commands)
    {
        var game = commands.Game;
        return game != null && IsCommandableBy(commands, game.LocalPlayerIndex);
    }

    public static bool CanExecuteCommandsFromLocalPlayer(CommandManager commands)
    {
        var game = commands.Game;
        return game != null && CanExecuteCommandsFrom(commands, game.LocalPlayerIndex);
    }

    public static bool CanExecuteCrewCommandsFromLocalPlayer(CommandManager commands)
    {
        var game = commands.Game;
        return game != null && CanExecuteCrewCommandsFrom(commands, game.LocalPlayerIndex);
    }
}
