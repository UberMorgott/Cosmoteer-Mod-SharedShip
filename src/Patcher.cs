using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.IO;
using Cosmoteer.Ships.Commands;
using Cosmoteer.Simulation;
using HarmonyLib;

namespace SharedShip;

/// <summary>
/// Applies all Harmony patches.
///
/// Why callers are patched too: Cosmoteer.dll ships as ReadyToRun (CorFlags ILLibrary). crossgen2 inlines small
/// same-assembly methods into their callers ahead of time, and CommandManager.IsCommandableBy (~25 bytes IL) and the
/// *LocalPlayer getters are textbook inline candidates. Harmony detours the method entry point and MonoMod marks the
/// patched method NoInline (FxCoreBaseRuntime.DisableInlining), which fixes every caller compiled AFTER patching - but
/// precompiled R2R callers that already inlined the old body would keep the vanilla ownership check (cold MPInput
/// Execute methods may never be re-jitted by tiering). So every method that calls a target is also patched:
///   - a transpiler swaps call/callvirt of the six CommandManager members for <see cref="SharedRules"/> statics, and
///   - patching forces that caller to be compiled from IL again, which drops any stale inlined copy.
/// Small callers (&lt;= <see cref="InlineCandidateMaxIl"/> bytes IL, e.g. Ship.get_IsLocalPlayerShip) could themselves be
/// inlined into their own callers, so the caller search is repeated transitively through them.
/// </summary>
internal static class Patcher
{
    public const string HarmonyId = "morgott.sharedship";

    /// <summary>
    /// IL size up to which a caller is treated as a possible R2R inlinee (the JIT always inlines &lt;= 16 bytes and
    /// usually inlines simple wrappers up to a few dozen bytes; 32 covers property chains like IsLocalPlayerShip).
    /// </summary>
    private const int InlineCandidateMaxIl = 32;

    private const int MaxClosureDepth = 4;

    /// <summary>Vanilla member (by method handle) -> replacement static in SharedRules.</summary>
    private static readonly Dictionary<RuntimeMethodHandle, MethodInfo> Redirects = new();

    public static void Apply()
    {
        var sw = Stopwatch.StartNew();
        var harmony = new Harmony(HarmonyId);
        Type cm = typeof(CommandManager);
        Type rules = typeof(SharedRules);

        // (vanilla member, replacement, prefix that makes the vanilla member itself answer like the replacement)
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

        var targets = new List<MethodBase> { localPlayerShips };
        foreach (var t in commandTargets)
        {
            MethodInfo replacement = rules.GetMethod(t.Replacement, BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(rules.FullName, t.Replacement);
            Redirects[t.Original.MethodHandle] = replacement;
            targets.Add(t.Original);
        }

        // 1. The vanilla members themselves (covers reflection, other mods, and anything JIT-compiled later).
        foreach (var t in commandTargets)
        {
            harmony.Patch(t.Original, prefix: new HarmonyMethod(Method(typeof(OriginalPrefixes), t.Prefix)));
        }
        harmony.Patch(localPlayerShips, postfix: new HarmonyMethod(Method(typeof(FleetView), nameof(FleetView.LocalPlayerShipsPostfix))));

        // 2. Every (transitive, through small wrappers) caller inside Cosmoteer.dll.
        List<MethodBase> callers = FindCallers(cm.Module, targets, out int skipped);
        var transpiler = new HarmonyMethod(Method(typeof(Patcher), nameof(RedirectTranspiler)));
        int patched = 0, failed = 0;
        foreach (MethodBase caller in callers)
        {
            try
            {
                harmony.Patch(caller, transpiler: transpiler);
                patched++;
            }
            catch (Exception ex)
            {
                failed++;
                Log.Info($"WARN could not patch caller {caller.DeclaringType?.FullName}.{caller.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Log.Info($"Patched {targets.Count} targets + {patched} callers ({failed} failed, {skipped} unpatchable generic/unresolved skipped) in {sw.ElapsedMilliseconds} ms");
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

    /// <summary>
    /// Scans Cosmoteer.dll's IL (System.Reflection.Metadata, no JIT, no allocation per instruction) for methods calling
    /// any target; small callers become targets for the next round. Returns all callers that are not targets.
    /// </summary>
    internal static List<MethodBase> FindCallers(Module module, List<MethodBase> roots, out int skipped)
    {
        var targetTokens = new HashSet<int>();
        foreach (MethodBase m in roots)
        {
            targetTokens.Add(m.MetadataToken);
        }
        var callerTokens = new HashSet<int>();
        var result = new List<MethodBase>();
        skipped = 0;

        using var stream = File.OpenRead(module.Assembly.Location);
        using var pe = new PEReader(stream);
        MetadataReader md = pe.GetMetadataReader();

        var frontier = new HashSet<int>(targetTokens);
        for (int depth = 0; depth < MaxClosureDepth && frontier.Count > 0; depth++)
        {
            var next = new HashSet<int>();
            foreach (MethodDefinitionHandle h in md.MethodDefinitions)
            {
                MethodDefinition def = md.GetMethodDefinition(h);
                if (def.RelativeVirtualAddress == 0)
                {
                    continue;
                }
                int token = MetadataTokens.GetToken(h);
                if (targetTokens.Contains(token) || callerTokens.Contains(token))
                {
                    continue;
                }
                MethodBodyBlock body = pe.GetMethodBody(def.RelativeVirtualAddress);
                if (!CallsAny(body.GetILReader(), frontier))
                {
                    continue;
                }
                callerTokens.Add(token);
                MethodBase? caller;
                try
                {
                    caller = module.ResolveMethod(token);
                }
                catch (Exception)
                {
                    caller = null;
                }
                if (caller != null && caller.ContainsGenericParameters)
                {
                    // Open generic method of a generic class: patch one reference-type instantiation instead. All
                    // reference-type instantiations share one canonical native body, so that covers e.g.
                    // BaseCommandState<Command/MoveCommand/AttackCommand>.Input (BaseCommandState.cs:90).
                    caller = CloseOverReferenceTypes(module, caller);
                }
                if (caller == null || caller.ContainsGenericParameters || caller.IsAbstract)
                {
                    skipped++;
                    Log.Info($"skip (generic/unresolvable) caller token 0x{token:X8}");
                    continue;
                }
                result.Add(caller);
                if (body.Size <= InlineCandidateMaxIl)
                {
                    next.Add(token);
                }
            }
            frontier = next;
        }
        return result;
    }

    /// <summary>
    /// For a non-generic method declared on a generic class, returns the same method on the first instantiation of that
    /// class found as a base type in the module whose type arguments are all reference types; null if none.
    /// </summary>
    private static MethodBase? CloseOverReferenceTypes(Module module, MethodBase open)
    {
        Type? definition = open.DeclaringType;
        if (definition == null || !definition.IsGenericTypeDefinition || open.IsGenericMethodDefinition)
        {
            return null;
        }
        Type[] types;
        try
        {
            types = module.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = Array.FindAll(ex.Types, t => t != null)!;
        }
        foreach (Type type in types)
        {
            for (Type? b = type.BaseType; b != null; b = b.BaseType)
            {
                if (b.IsConstructedGenericType && !b.ContainsGenericParameters && b.GetGenericTypeDefinition() == definition
                    && Array.TrueForAll(b.GetGenericArguments(), a => !a.IsValueType))
                {
                    return MethodBase.GetMethodFromHandle(open.MethodHandle, b.TypeHandle);
                }
            }
        }
        return null;
    }

    private static bool CallsAny(BlobReader il, HashSet<int> tokens)
    {
        while (il.RemainingBytes > 0)
        {
            OpCode op = IlOpCodes.Read(ref il);
            if (op.OperandType == OperandType.InlineMethod)
            {
                int token = il.ReadInt32();
                if ((op == OpCodes.Call || op == OpCodes.Callvirt) && tokens.Contains(token))
                {
                    return true;
                }
                continue;
            }
            IlOpCodes.SkipOperand(ref il, op);
        }
        return false;
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

/// <summary>Minimal IL opcode decoder for <see cref="Patcher.FindCallers"/>.</summary>
internal static class IlOpCodes
{
    private static readonly OpCode[] OneByte = new OpCode[0x100];
    private static readonly OpCode[] TwoByte = new OpCode[0x100];

    static IlOpCodes()
    {
        foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is not OpCode op)
            {
                continue;
            }
            ushort value = unchecked((ushort)op.Value);
            if (op.Size == 1)
            {
                OneByte[value] = op;
            }
            else
            {
                TwoByte[value & 0xFF] = op;
            }
        }
    }

    public static OpCode Read(ref BlobReader il)
    {
        byte b = il.ReadByte();
        return b == 0xFE ? TwoByte[il.ReadByte()] : OneByte[b];
    }

    public static void SkipOperand(ref BlobReader il, OpCode op)
    {
        switch (op.OperandType)
        {
            case OperandType.InlineNone:
                break;
            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                il.Offset += 1;
                break;
            case OperandType.InlineVar:
                il.Offset += 2;
                break;
            case OperandType.InlineI8:
            case OperandType.InlineR:
                il.Offset += 8;
                break;
            case OperandType.InlineSwitch:
                int count = il.ReadInt32();
                il.Offset += count * 4;
                break;
            default: // InlineBrTarget, InlineField, InlineI, InlineMethod, InlineSig, InlineString, InlineTok, InlineType, ShortInlineR
                il.Offset += 4;
                break;
        }
    }
}
