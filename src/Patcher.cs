using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Reflection.Emit;
using Cosmoteer.Crew;
using Cosmoteer.Game.Gui.Resources;
using Cosmoteer.Game.Multiplayer.MPInputs;
using Cosmoteer.Modes;
using Cosmoteer.Modes.Career;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Commands;
using Cosmoteer.Ships.Crew.Jobs;
using Cosmoteer.Ships.Resources;
using Cosmoteer.Simulation;
using HarmonyLib;
using Halfling.Serialization.Generic;

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
            Install(harmony, out int patchedCount, out int inlinerCount);
            Enabled = true;
            Log.Info($"Patched {patchedCount} methods + {inlinerCount} R2R inliners in {sw.ElapsedMilliseconds} ms");
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

    private static void Install(Harmony harmony, out int patchedCount, out int inlinerCount)
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
        MethodInfo transfer = Method(typeof(MPTransferShipControlInput), nameof(MPTransferShipControlInput.Execute));
        MethodInfo careerGui = Method(typeof(CareerGameModeManager), nameof(CareerGameModeManager.CreateModeSpecificGui));

        // (vanilla ownership predicate, postfix adding shared access to it)
        var ownershipTargets = new (MethodInfo Original, string Postfix)[]
        {
            (typeof(Ship).GetMethod(nameof(Ship.IsSamePlayerAs), new[] { typeof(Ship) }) ?? throw new MissingMethodException(nameof(Ship), nameof(Ship.IsSamePlayerAs)),
                nameof(OwnershipPostfixes.IsSamePlayerAs)),
            (Method(typeof(SimModeManager), nameof(SimModeManager.CanSalvage)), nameof(OwnershipPostfixes.CanSalvage)),
            (Getter(typeof(CrewSoul), nameof(CrewSoul.IsLocalPlayerCrew)), nameof(OwnershipPostfixes.IsLocalPlayerCrew)),
        };
        var moneyMethods = new List<MethodInfo>();
        foreach (var (name, _) in Payers.MoneyMethods)
        {
            moneyMethods.Add(name switch
            {
                "set_ResourcesRequested" => AccessTools.PropertySetter(Rtj, nameof(ResourceTransferJob.ResourcesRequested)),
                "Cancel" => RtjCancel,
                _ => Method(Rtj, name),
            } ?? throw new MissingMethodException(Rtj.FullName, name));
        }
        List<Patch> economy = EconomyPatches();

        var patched = new List<MethodBase> { localPlayerShips, setShipInfo, transfer, careerGui };
        patched.AddRange(moneyMethods);
        patched.AddRange(economy.Select(p => p.Original));
        patched.AddRange(ownershipTargets.Select(t => t.Original));
        foreach (var t in commandTargets)
        {
            Redirects[t.Original.MethodHandle] = Method(typeof(SharedRules), t.Replacement);
            patched.Add(t.Original);
        }

        // Inlined copies must be known before anything is patched (pure read of the game DLL).
        List<MethodBase> inliners = FindR2RInliners(patched.Distinct().ToList());

        foreach (var t in commandTargets)
        {
            harmony.Patch(t.Original, prefix: new HarmonyMethod(Method(typeof(OriginalPrefixes), t.Prefix)));
        }
        harmony.Patch(localPlayerShips, postfix: new HarmonyMethod(Method(typeof(FleetView), nameof(FleetView.LocalPlayerShipsPostfix))));
        harmony.Patch(setShipInfo,
            prefix: new HarmonyMethod(Method(typeof(RevokeOnUntag), nameof(RevokeOnUntag.Prefix))),
            postfix: new HarmonyMethod(Method(typeof(RevokeOnUntag), nameof(RevokeOnUntag.Postfix))));
        harmony.Patch(transfer, prefix: new HarmonyMethod(Method(typeof(TransferShared), nameof(TransferShared.Prefix))));
        harmony.Patch(careerGui, postfix: new HarmonyMethod(Method(typeof(ShareMenu), nameof(ShareMenu.CreateModeSpecificGuiPostfix))));
        foreach (var t in ownershipTargets)
        {
            harmony.Patch(t.Original, postfix: new HarmonyMethod(Method(typeof(OwnershipPostfixes), t.Postfix)));
        }
        var payerTranspiler = new HarmonyMethod(Method(typeof(Payers), nameof(Payers.PayerTranspiler)));
        foreach (MethodInfo m in moneyMethods)
        {
            harmony.Patch(m, transpiler: payerTranspiler);
        }
        foreach (Patch p in economy)
        {
            harmony.Patch(p.Original, H(p.Prefix), H(p.Postfix), H(p.Transpiler), H(p.Finalizer));
        }
        patchedCount = patched.Distinct().Count();
        var transpiler = new HarmonyMethod(Method(typeof(Patcher), nameof(RedirectTranspiler)));
        foreach (MethodBase inliner in inliners)
        {
            harmony.Patch(inliner, transpiler: transpiler);
            Log.Info($"  R2R inliner patched: {inliner.DeclaringType?.FullName}.{inliner.Name}");
        }
        inlinerCount = inliners.Count;
    }

    private static readonly Type Rtj = typeof(ResourceTransferJob);

    private static MethodInfo RtjCancel =>
        AccessTools.Method(Rtj, nameof(ResourceTransferJob.Cancel), new[] { typeof(int), typeof(bool) })
        ?? throw new MissingMethodException(Rtj.FullName, nameof(ResourceTransferJob.Cancel));

    /// <summary>One Harmony patch: the original and the helpers that patch it (any may be null).</summary>
    private sealed record Patch(MethodBase Original, MethodInfo? Prefix = null, MethodInfo? Postfix = null,
        MethodInfo? Transpiler = null, MethodInfo? Finalizer = null);

    private static HarmonyMethod? H(MethodInfo? m) => m == null ? null : new HarmonyMethod(m);

    /// <summary>Wallets (acting player, payers, pre-purchase ledger) and crew (split, moves, adoption, salvage).</summary>
    private static List<Patch> EconomyPatches()
    {
        Type rm = typeof(ResourceManager);
        Type sink = AccessTools.Inner(rm, "SinkInfo") ?? throw new MissingMemberException(rm.FullName, "SinkInfo");
        MethodInfo refund = Method(typeof(PrePurchases), nameof(PrePurchases.RefundTranspiler));
        MethodInfo merge = Method(typeof(Payers), nameof(Payers.MergeTranspiler));
        MethodInfo salvage = Method(typeof(SharedCrew), nameof(SharedCrew.SalvageTranspiler));
        var list = new List<Patch>();
        foreach (var (type, name) in Acting.Dispatchers)
        {
            list.Add(new(Method(type, name), Transpiler: Method(typeof(Acting), nameof(Acting.DispatchTranspiler))));
        }
        foreach (string name in new[] { "CreateOrIncreaseManualJob", "RegisterJob", "UnregisterJob" })
        {
            list.Add(new(Method(sink, name), Transpiler: merge));
        }
        list.Add(new(RtjCancel, Prefix: Method(typeof(Payers), nameof(Payers.CancelPrefix)),
            Postfix: Method(typeof(Payers), nameof(Payers.CancelPostfix)), Finalizer: Method(typeof(Payers), nameof(Payers.CancelFinalizer))));
        list.Add(new(Method(rm, nameof(ResourceManager.CarriedResourceTransfer)), Prefix: Method(typeof(Payers), nameof(Payers.CarriedPrefix)),
            Postfix: Method(typeof(PrePurchases), nameof(PrePurchases.CarriedPostfix)), Finalizer: Method(typeof(Payers), nameof(Payers.CarriedFinalizer))));
        list.Add(new(Method(rm, nameof(ResourceManager.ManualResourceTransfer)), Postfix: Method(typeof(PrePurchases), nameof(PrePurchases.ManualTransferPostfix))));
        list.Add(new(Method(rm, nameof(ResourceManager.RemoveManualTransferJob)), Prefix: Method(typeof(PrePurchases), nameof(PrePurchases.JobScopePrefix)),
            Finalizer: Method(typeof(PrePurchases), nameof(PrePurchases.JobScopeFinalizer))));
        list.Add(new(Method(rm, nameof(ResourceManager.AddPrePurchaseResources)), Prefix: Method(typeof(PrePurchases), nameof(PrePurchases.AddPrefix))));
        list.Add(new(Method(Rtj, nameof(ResourceTransferJob.WriteTo)), Postfix: Method(typeof(Payers), nameof(Payers.WriteToPostfix))));
        list.Add(new(AccessTools.Constructor(Rtj, new[] { typeof(GenericSerialReader) }) ?? throw new MissingMethodException(Rtj.FullName, ".ctor(reader)"),
            Postfix: Method(typeof(Payers), nameof(Payers.ReadPostfix))));
        list.Add(new(Method(Rtj, nameof(ResourceTransferJob.GetIntegrityHash)), Postfix: Method(typeof(Payers), nameof(Payers.HashPostfix))));
        list.Add(new(Method(Rtj, "DeliverResources"), Prefix: Method(typeof(PrePurchases), nameof(PrePurchases.DeliverPrefix)),
            Postfix: Method(typeof(PrePurchases), nameof(PrePurchases.DeliverPostfix)), Finalizer: Method(typeof(PrePurchases), nameof(PrePurchases.JobScopeFinalizer))));
        list.Add(new(Method(rm, nameof(ResourceManager.PrePurchaseBuyableResources)), Transpiler: Method(typeof(PrePurchases), nameof(PrePurchases.ChargeTranspiler))));
        var removals = typeof(PrePurchaseHandler).GetMethods().Where(m => m.Name == nameof(PrePurchaseHandler.RemovePrePurchases)).ToList();
        if (removals.Count != 3)
        {
            throw new MissingMethodException(nameof(PrePurchaseHandler), $"RemovePrePurchases: {removals.Count} overloads, expected 3");
        }
        foreach (MethodInfo m in removals)
        {
            list.Add(new(m, Prefix: Method(typeof(PrePurchases), nameof(PrePurchases.RemovePrefix)), Postfix: Method(typeof(PrePurchases), nameof(PrePurchases.RemovePostfix))));
        }
        MethodInfo transferTo = Method(rm, nameof(ResourceManager.TransferConstructionResourcesTo));
        list.Add(new(transferTo, Transpiler: Method(typeof(PrePurchases), nameof(PrePurchases.TransferTranspiler))));
        list.Add(new(transferTo, Transpiler: refund, Finalizer: Method(typeof(PrePurchases), nameof(PrePurchases.TransferScopeFinalizer))));
        list.Add(new(Method(rm, "OnComponentDeactivated"), Prefix: Method(typeof(PrePurchases), nameof(PrePurchases.DeactivatedPrefix)),
            Transpiler: refund, Finalizer: Method(typeof(PrePurchases), nameof(PrePurchases.DeactivatedFinalizer))));
        list.Add(new(Method(rm, "RefundExpiredPrePurchasedResources"), Transpiler: refund));
        list.Add(new(Method(rm, nameof(ResourceManager.CancelRefundableResources)), Postfix: Method(typeof(PrePurchases), nameof(PrePurchases.CancelRefundablePostfix))));
        list.Add(new(Method(rm, nameof(ResourceManager.WriteTo)), Postfix: Method(typeof(PrePurchases), nameof(PrePurchases.WriteToPostfix))));
        list.Add(new(Method(rm, nameof(ResourceManager.ReadFrom)), Postfix: Method(typeof(PrePurchases), nameof(PrePurchases.ReadFromPostfix))));
        list.Add(new(Method(rm, nameof(ResourceManager.GetIntegrityHash)), Postfix: Method(typeof(PrePurchases), nameof(PrePurchases.HashPostfix))));

        list.Add(new(Method(typeof(SimCrewManager), nameof(SimCrewManager.CountCrewForPlayer)), Postfix: Method(typeof(SharedCrew), nameof(SharedCrew.CountPostfix))));
        list.Add(new(Method(typeof(CareerGameModeManager), nameof(CareerGameModeManager.OnSwitchingSimulation)),
            Postfix: Method(typeof(SharedCrew), nameof(SharedCrew.SwitchingPostfix))));
        list.Add(new(CrewLimitLambda(), Transpiler: Method(typeof(SharedCrew), nameof(SharedCrew.OwnerPairTranspiler))));
        list.Add(new(Method(typeof(CrewAndResourceTransferWindow), "GetCrewCapacityWithCrewLimitWhenReceivingFrom"),
            Prefix: Method(typeof(SharedCrew), nameof(SharedCrew.CapacityPrefix))));
        list.Add(new(Method(typeof(CrewSoul), "FindNewHomeShip"), Transpiler: Method(typeof(SharedCrew), nameof(SharedCrew.AdoptTranspiler))));
        list.Add(new(Method(typeof(SalvageCommand), nameof(SalvageCommand.IsValid)), Transpiler: salvage));
        list.Add(new(Method(typeof(MPSalvagePartInput), nameof(MPSalvagePartInput.Execute)), Transpiler: salvage));
        return list;
    }

    /// <summary>The compiler-generated lambda of MPSetCrewHomeShipInput.Execute that counts crew changing owner (:86-90).</summary>
    private static MethodInfo CrewLimitLambda()
    {
        Type input = typeof(MPSetCrewHomeShipInput);
        MethodInfo homeShip = AccessTools.PropertyGetter(input, nameof(MPSetCrewHomeShipInput.HomeShip));
        return input.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Single(m => m.Name.StartsWith("<Execute>", StringComparison.Ordinal) && m.ReturnType == typeof(bool)
                && PatchProcessor.GetOriginalInstructions(m).Any(i => i.Calls(homeShip)));
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW", SetLastError = true)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private const uint MbOk = 0x0, MbIconError = 0x10, MbTopMost = 0x40000;

    [DoesNotReturn]
    private static void FatalExit(string message)
    {
        try
        {
            // MessageBoxW returns 0 when the box could not be shown: record that the user never saw the reason.
            if (MessageBox(IntPtr.Zero, message, "SharedShip - fatal", MbOk | MbIconError | MbTopMost) == 0)
            {
                Log.Info("fatal message box could not be shown (MessageBoxW error " + Marshal.GetLastWin32Error() + ")");
            }
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

/// <summary>
/// Postfixes that extend vanilla ownership predicates with shared access (vanilla true stays true). Only CommandManager
/// (player-to-ship) and Ship.IsSamePlayerAs (ship-to-ship) decide ownership in vanilla sim code; CanSalvage and
/// IsLocalPlayerCrew are the two predicates that compare owners directly instead of calling either.
/// Crew counts and wallets are per player (<see cref="SharedCrew"/>, <see cref="Payers"/>); GetShipsForPlayer stays per owner.
/// </summary>
internal static class OwnershipPostfixes
{
    /// <summary>
    /// Sim: Owned construction/repair assist (SimModeManager.cs:356), construction sourcing both ways and crew-carried
    /// resources (ResourceManager.cs:3871-3991), not buying from own ships (:4016), hauling sink (ResourceTransferJob.cs:805).
    /// </summary>
    public static void IsSamePlayerAs(Ship __instance, Ship ship, ref bool __result)
    {
        __result = __result || SharedRules.IsSharedPair(__instance, ship);
    }

    /// <summary>Sim (SalvageCommand) and UI: salvaging parts of one's own ship covers shared ships.</summary>
    public static void CanSalvage(int sourcePlayer, Ship ship, bool allowSalvageOwnShip, ref bool __result)
    {
        __result = __result || (allowSalvageOwnShip && SharedRules.IsSharedWith(ship, sourcePlayer));
    }

    /// <summary>UI only (crew picking, O2/distance warnings): crew of a ship shared with the local player.</summary>
    public static void IsLocalPlayerCrew(CrewSoul __instance, ref bool __result)
    {
        __result = __result || (__instance.HomeShip is Ship home && __instance.Game is { } game && SharedRules.IsSharedWith(home, game.LocalPlayerIndex));
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
