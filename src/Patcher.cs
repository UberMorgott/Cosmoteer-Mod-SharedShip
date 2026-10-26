using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Reflection.Emit;
using Cosmoteer.Game.Multiplayer.MPInputs;
using Cosmoteer.Ships.Commands;
using Cosmoteer.Simulation;
using HarmonyLib;

namespace SharedShip;

/// <summary>
/// Installs every patch, all-or-nothing.
///
/// Inlining: Cosmoteer.dll is ReadyToRun. Harmony detours a method's entry point and MonoMod marks it NoInline
/// (FxCoreBaseRuntime.DisableInlining), which covers every caller compiled after patching. The loader runs
/// GameLoadInitializer from its Application.Main prefix (ModLoader.ApplicationMainPrefix), before game code runs, so
/// nothing has been tier-1 re-jitted yet. What remains are precompiled bodies that already contain an inlined copy of a
/// patched method. Those are read exactly from the image's R2R inlining table (<see cref="R2RInlineInfo"/>) and patched
/// too: a Harmony patch rebuilds the method from IL, dropping the stale copy, and the transpiler additionally calls
/// <see cref="SharedRules"/> directly. Shared generic code is never patched (Harmony bakes one T into the canonical
/// body shared by all reference-type instantiations -> InvalidCastException for the others); if the table ever lists a
/// generic inliner, loading is refused.
///
/// Any failure rolls back all of this mod's patches and leaves the game vanilla (<see cref="Enabled"/> = false); if
/// that rollback itself fails, the process shows an error and exits before the game starts.
/// </summary>
internal static class Patcher
{
    public const string HarmonyId = "morgott.sharedship";

    /// <summary>True only when every required patch is installed.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>Vanilla CommandManager member (by method handle) -> replacement static in SharedRules.</summary>
    private static readonly Dictionary<RuntimeMethodHandle, MethodInfo> Redirects = new();

    public static void Apply()
    {
        var sw = Stopwatch.StartNew();
        var harmony = new Harmony(HarmonyId);
        try
        {
            Install(harmony, out int inlinerCount);
            Enabled = true;
            Log.Info($"Patched {Redirects.Count + 2} methods + {inlinerCount} R2R inliners in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            Enabled = false;
            Log.Error("patch", ex);
            try
            {
                harmony.UnpatchAll(HarmonyId);
                if (Harmony.HasAnyPatches(HarmonyId))
                {
                    throw new InvalidOperationException("patches of " + HarmonyId + " still present after UnpatchAll");
                }
            }
            catch (Exception unpatchEx)
            {
                // Partially patched game: neither vanilla nor SharedShip, so lockstep MP would desync. Throwing is not
                // enough: ModLoader.ApplicationMainPrefix (ModLoader.decompiled.cs:851-877) catches initializer exceptions,
                // logs them and starts the game anyway. We run inside that prefix, before Cosmoteer's Main, so nothing of
                // the game is initialized yet: tell the user and terminate the process.
                Log.Error("unpatch", unpatchEx);
                Log.Info("SharedShip FATAL: rollback failed, game is PARTIALLY PATCHED. Exiting; remove or update the mod.");
                FatalExit("SharedShip could not undo its partial patches after a failed install.\n\n"
                    + "Cosmoteer will now close to avoid multiplayer desyncs. Remove or update the SharedShip mod.\n\n"
                    + "Details: " + Log.ModFolder + "\\SharedShip.log");
            }
            Log.Info("SharedShip DISABLED: all patches rolled back, game runs vanilla. Teammates who still run the mod will desync.");
        }
    }

    private static void Install(Harmony harmony, out int inlinerCount)
    {
        Type cm = typeof(CommandManager);
        // (vanilla member, SharedRules replacement, prefix that makes the vanilla member answer like the replacement)
        var commandTargets = new (MethodInfo Original, string Replacement, string Prefix)[]
        {
            (Method(cm, nameof(CommandManager.IsCommandableBy)), nameof(SharedRules.IsCommandableBy), nameof(OriginalPrefixes.IsCommandableBy)),
            (Method(cm, nameof(CommandManager.CanExecuteCommandsFrom)), nameof(SharedRules.CanExecuteCommandsFrom), nameof(OriginalPrefixes.CanExecuteCommandsFrom)),
            (Method(cm, nameof(CommandManager.CanExecuteCrewCommandsFrom)), nameof(SharedRules.CanExecuteCrewCommandsFrom), nameof(OriginalPrefixes.CanExecuteCrewCommandsFrom)),
            (Getter(cm, nameof(CommandManager.IsCommandableByLocalPlayer)), nameof(SharedRules.IsCommandableByLocalPlayer), nameof(OriginalPrefixes.IsCommandableByLocalPlayer)),
            (Getter(cm, nameof(CommandManager.CanExecuteCommandsFromLocalPlayer)), nameof(SharedRules.CanExecuteCommandsFromLocalPlayer), nameof(OriginalPrefixes.CanExecuteCommandsFromLocalPlayer)),
            (Getter(cm, nameof(CommandManager.CanExecuteCrewCommandsFromLocalPlayer)), nameof(SharedRules.CanExecuteCrewCommandsFromLocalPlayer), nameof(OriginalPrefixes.CanExecuteCrewCommandsFromLocalPlayer)),
        };
        MethodInfo localPlayerShips = Getter(typeof(SimShipsManager), nameof(SimShipsManager.LocalPlayerShips));
        MethodInfo setShipInfo = Method(typeof(MPSetShipInfoInput), nameof(MPSetShipInfoInput.Execute));

        var patched = new List<MethodBase> { localPlayerShips, setShipInfo };
        foreach (var t in commandTargets)
        {
            Redirects[t.Original.MethodHandle] = Method(typeof(SharedRules), t.Replacement);
            patched.Add(t.Original);
        }

        // Inlined copies must be known before anything is patched (pure read of the game DLL).
        List<MethodBase> inliners = FindR2RInliners(patched);

        foreach (var t in commandTargets)
        {
            harmony.Patch(t.Original, prefix: new HarmonyMethod(Method(typeof(OriginalPrefixes), t.Prefix)));
        }
        harmony.Patch(localPlayerShips, postfix: new HarmonyMethod(Method(typeof(FleetView), nameof(FleetView.LocalPlayerShipsPostfix))));
        harmony.Patch(setShipInfo,
            prefix: new HarmonyMethod(Method(typeof(RevokeOnUntag), nameof(RevokeOnUntag.Prefix))),
            postfix: new HarmonyMethod(Method(typeof(RevokeOnUntag), nameof(RevokeOnUntag.Postfix))));

        var transpiler = new HarmonyMethod(Method(typeof(Patcher), nameof(RedirectTranspiler)));
        foreach (MethodBase inliner in inliners)
        {
            harmony.Patch(inliner, transpiler: transpiler);
            Log.Info($"  R2R inliner patched: {inliner.DeclaringType?.FullName}.{inliner.Name}");
        }
        inlinerCount = inliners.Count;
    }

    /// <summary>
    /// Precompiled methods of Cosmoteer.dll that contain an inlined copy of any method in <paramref name="patched"/>.
    /// Throws if the table cannot be read or lists a method we cannot safely patch.
    /// </summary>
    internal static List<MethodBase> FindR2RInliners(List<MethodBase> patched)
    {
        Module module = typeof(CommandManager).Module;
        Dictionary<int, List<int>>? table = R2RInlineInfo.Read(module.Assembly.Location);
        var result = new List<MethodBase>();
        if (table == null)
        {
            return result; // pure IL image: nothing was inlined ahead of time
        }
        var patchedRids = new HashSet<int>();
        foreach (MethodBase m in patched)
        {
            patchedRids.Add(R2RInlineInfo.Rid(m));
        }
        var seen = new HashSet<int>();
        foreach (MethodBase m in patched)
        {
            if (!table.TryGetValue(R2RInlineInfo.Rid(m), out List<int>? inlinerRids))
            {
                continue;
            }
            foreach (int rid in inlinerRids)
            {
                if (patchedRids.Contains(rid) || !seen.Add(rid))
                {
                    continue; // already replaced by its own prefix/postfix patch
                }
                MethodBase inliner = module.ResolveMethod(0x06000000 | rid)
                    ?? throw new InvalidOperationException($"cannot resolve inliner RID {rid}");
                if (inliner.ContainsGenericParameters)
                {
                    throw new NotSupportedException(
                        $"{inliner.DeclaringType?.FullName}.{inliner.Name} is shared generic code with an inlined ownership check; refusing to patch it");
                }
                result.Add(inliner);
            }
        }
        return result;
    }

    /// <summary>Rewrites call/callvirt of a redirected CommandManager member into a call of its SharedRules twin.</summary>
    public static IEnumerable<CodeInstruction> RedirectTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction ins in instructions)
        {
            if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt)
                && ins.operand is MethodInfo callee
                && Redirects.TryGetValue(callee.MethodHandle, out MethodInfo? replacement))
            {
                // Instance-method stack shape (this, args...) == static replacement shape (CommandManager, args...).
                ins.opcode = OpCodes.Call;
                ins.operand = replacement;
            }
            yield return ins;
        }
    }

    // Neither the game nor HalflingCore exposes a message-box API, and none is usable before Cosmoteer's Main anyway.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private const uint MbOk = 0x0, MbIconError = 0x10, MbTopMost = 0x40000;

    [DoesNotReturn]
    private static void FatalExit(string message)
    {
        try
        {
            MessageBox(IntPtr.Zero, message, "SharedShip - fatal", MbOk | MbIconError | MbTopMost);
        }
        catch (Exception)
        {
            // No UI available (e.g. headless); exiting is what matters.
        }
        Environment.Exit(1);
        throw new InvalidOperationException("unreachable");
    }

    private static MethodInfo Method(Type type, string name)
    {
        return type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, name);
    }

    private static MethodInfo Getter(Type type, string name)
    {
        return type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetMethod
            ?? throw new MissingMethodException(type.FullName, "get_" + name);
    }
}

/// <summary>Prefixes for the vanilla CommandManager members: answer via SharedRules and skip the original.</summary>
internal static class OriginalPrefixes
{
    public static bool IsCommandableBy(CommandManager __instance, int playerIndex, ref bool __result)
    {
        __result = SharedRules.IsCommandableBy(__instance, playerIndex);
        return false;
    }

    public static bool CanExecuteCommandsFrom(CommandManager __instance, int playerIndex, ref bool __result)
    {
        __result = SharedRules.CanExecuteCommandsFrom(__instance, playerIndex);
        return false;
    }

    public static bool CanExecuteCrewCommandsFrom(CommandManager __instance, int playerIndex, ref bool __result)
    {
        __result = SharedRules.CanExecuteCrewCommandsFrom(__instance, playerIndex);
        return false;
    }

    public static bool IsCommandableByLocalPlayer(CommandManager __instance, ref bool __result)
    {
        __result = SharedRules.IsCommandableByLocalPlayer(__instance);
        return false;
    }

    public static bool CanExecuteCommandsFromLocalPlayer(CommandManager __instance, ref bool __result)
    {
        __result = SharedRules.CanExecuteCommandsFromLocalPlayer(__instance);
        return false;
    }

    public static bool CanExecuteCrewCommandsFromLocalPlayer(CommandManager __instance, ref bool __result)
    {
        __result = SharedRules.CanExecuteCrewCommandsFromLocalPlayer(__instance);
        return false;
    }
}
