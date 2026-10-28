using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer;
using HarmonyLib;
using Halfling.Network;

namespace SharedShip;

/// <summary>
/// The acting player: the lockstep sender of the input being executed. The single chokepoint is the game's input
/// dispatch (BaseMPManager/SP managers call MPInput.Execute / ExecuteLocalImmediate); <see cref="DispatchTranspiler"/>
/// routes those calls through <see cref="Execute"/>, which sets <see cref="Player"/> for everything the input does
/// synchronously. Deferred work outside an input sees null and keeps vanilla (the owner).
/// Deterministic: the sender index is the same argument vanilla passes to Execute on every client. Local previews
/// (ExecuteLocalImmediate) run only on the sender's client and use its own index, so they match the confirmed input.
/// </summary>
internal static class Acting
{
    /// <summary>Methods that dispatch inputs (0.30.5).</summary>
    public static readonly (Type Type, string Method)[] Dispatchers =
    {
        (typeof(BaseMPManager), "AdvanceNetworkTime"),
        (typeof(BaseMPManager), "Update"),
        (typeof(BaseMPManager), "EnqueueInput"),
        (typeof(BaseSPManager), "EnqueueInput"),
        (typeof(RecordableSPManager), "AdvanceNetworkTime"),
        (typeof(SPManager), "Update"),
    };

    [ThreadStatic] private static int? _player;

    /// <summary>Slot of the acting player, or null outside an input.</summary>
    public static int? Player => _player;

    /// <summary>Sets <see cref="Player"/> and returns the previous value (restore it in a finally/finalizer).</summary>
    public static int? Swap(int? player)
    {
        int? old = _player;
        _player = player;
        return old;
    }

    public static void Execute(MPInput input, GameRoot game, int playerIndex, MessengerID playerID, bool force)
    {
        int? old = Swap(playerIndex);
        try
        {
            input.Execute(game, playerIndex, playerID, force);
        }
        finally
        {
            _player = old;
            PrePurchases.ClearStash();
        }
    }

    public static void ExecuteLocalImmediate(MPInput input, GameRoot game)
    {
        int? old = Swap(game.LocalPlayerIndex);
        try
        {
            input.ExecuteLocalImmediate(game);
        }
        finally
        {
            _player = old;
            PrePurchases.ClearStash();
        }
    }

    public static IEnumerable<CodeInstruction> DispatchTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        MethodInfo execute = AccessTools.Method(typeof(MPInput), nameof(MPInput.Execute));
        MethodInfo local = AccessTools.Method(typeof(MPInput), nameof(MPInput.ExecuteLocalImmediate));
        int replaced = 0;
        foreach (CodeInstruction ins in instructions)
        {
            bool isExecute = ins.Calls(execute);
            if (isExecute || ins.Calls(local))
            {
                ins.opcode = OpCodes.Call;
                ins.operand = AccessTools.Method(typeof(Acting), isExecute ? nameof(Execute) : nameof(ExecuteLocalImmediate));
                replaced++;
            }
            yield return ins;
        }
        if (replaced == 0)
        {
            throw new InvalidOperationException($"{original.DeclaringType?.Name}.{original.Name}: no input dispatch found");
        }
    }
}
