using System;
using Cosmoteer.Modes.Career;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Commands;

namespace SharedShip;

/// <summary>
/// The single source of truth for "may player P command ship S".
///
/// Determinism: in lockstep MP every client runs AllowMPInput + Execute for every input and compares state hashes
/// (BaseMPManager), and Execute bodies call CommandManager.IsCommandableBy(senderIndex). So the answer must be
/// identical on all clients. It only reads synced sim state:
///   - ShipMetadata.PlainShipName (changed only through the lockstep MPSetShipInfoInput),
///   - ShipMetadata.PlayerIndex,
///   - the sim mode type and CareerSimModeManager.IsHumanPlayerAllegiance (0 &lt;= index &lt; Rules.MaxPlayers).
/// Nothing local (LocalPlayerIndex, settings, wall clock) is used unless the caller passes it in, exactly like vanilla.
/// </summary>
internal static class SharedRules
{
    /// <summary>Ship-name tag that marks a ship as shared. Case-sensitive, anywhere in the name.</summary>
    public const string Tag = "[S]";

    /// <summary>
    /// True when the ship's name carries <see cref="Tag"/>. No cache on purpose: an ordinal search for a 3-char needle in a
    /// short name is vectorized and allocation-free, i.e. no slower than any per-ship cache lookup (ConditionalWeakTable
    /// needs a hash + lookup), and it can never go stale.
    /// </summary>
    public static bool HasTag(Ship ship)
    {
        string? name = ship.Metadata.PlainShipName;
        return name != null && name.Contains(Tag, StringComparison.Ordinal);
    }

    /// <summary>Name with every occurrence of the tag removed and trimmed (may be empty; sanitize before use).</summary>
    public static string WithoutTag(string name) => name.Replace(Tag, string.Empty, StringComparison.Ordinal).Trim();

    /// <summary>Name with the tag prepended.</summary>
    public static string WithTag(string name) => Tag + " " + name;

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
        if (playerIndex < 0 || owner < 0 || owner == playerIndex)
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
