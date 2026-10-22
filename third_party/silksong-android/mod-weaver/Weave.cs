using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace ModWeaver;

/// <summary>
/// The IL surgery: prefixes and postfixes folded into a method's own body.
///
/// Harmony does this at runtime by writing a new method and detouring the old
/// one at its machine-code entry point. There is nothing to detour after
/// IL2CPP, so the same shape is built statically instead. A patched method
/// ends up as:
///
///     [gated prefix calls]
///     brfalse  --> postfixes       (when a prefix returns bool)
///     ... the original body, with every `ret` turned into
///         `stloc __result; br postfixes` ...
///  postfixes:
///     [gated postfix calls]
///     ldloc __result
///     ret
///
/// which is Harmony's semantics -- prefix first, a `false` prefix skips the
/// original, postfixes run either way -- expressed in the one place a build-time
/// tool can reach.
///
/// A target has one result local and one postfix path, even across plugins.
/// Prefixes run in reverse weave order and postfixes in weave order, as with
/// the original nested weaves, but skipping the original never bypasses an
/// earlier plugin's postfix. A false prefix still skips remaining prefixes.
/// Harmony's priority attributes are not honoured.
///
/// Each plugin's calls are wrapped in a test of its gate field, so a mod that
/// is switched off costs one predictable branch and does nothing else. See
/// Weaver.GateFor.
/// </summary>
internal sealed class Weave
{
    sealed record Composition(
        VariableDefinition? Result, Instruction PostfixStart, Instruction ReturnStart);

    readonly Dictionary<MethodDefinition, Composition> _targets = new();

    /// <summary>
    /// Applies one patch class to one target.
    ///
    /// Returns false without touching the body when something cannot be
    /// honoured -- the arguments are all computed before a single instruction
    /// is added, so a patch that is rejected leaves no half-woven method
    /// behind.
    /// </summary>
    public bool Apply(
        MethodDefinition target,
        MethodDefinition? prefix,
        MethodDefinition? postfix,
        FieldDefinition? gate,
        PluginReport report,
        Action<AssemblyDefinition> touched)
    {
        if (prefix is null && postfix is null) return false;
        if (!target.HasBody || target.Body.Instructions.Count == 0)
        {
            report.Note($"{target.FullName} has no body to weave into");
            return false;
        }

        foreach (var patch in new[] { prefix, postfix })
        {
            if (patch is null) continue;
            if (!patch.IsStatic)
            {
                report.Note($"{Where(patch)} is not static; Harmony patch methods have to be");
                return false;
            }
            if (patch.HasGenericParameters)
            {
                report.Note($"{Where(patch)} is generic, which a build-time weaver cannot resolve");
                return false;
            }
        }
        if (target.HasGenericParameters)
        {
            report.Note($"{target.FullName} is a generic method and cannot be woven");
            return false;
        }

        var module = target.Module;
        var body = target.Body;
        var simplified = false;
        try
        {
            var firstWeave = !_targets.TryGetValue(target, out var composition);
            var resultVar = composition?.Result;
            if (firstWeave && target.ReturnType.MetadataType != MetadataType.Void)
                resultVar = new VariableDefinition(module.ImportReference(target.ReturnType));
            VariableDefinition? stateVar = null;

            // Everything is built first and committed second. Building can fail --
            // an argument the weaver does not understand -- and a body that has
            // already been half-rewritten cannot be put back.
            var prefixCode = new List<Instruction>();
            var postfixCode = new List<Instruction>();
            var scratch = new List<VariableDefinition>();

            var postfixStart = composition?.PostfixStart ?? Instruction.Create(OpCodes.Nop);
            var returnStart = composition?.ReturnStart ?? (resultVar is null
                ? Instruction.Create(OpCodes.Ret)
                : Instruction.Create(OpCodes.Ldloc, resultVar));

            if (prefix is not null)
            {
                if (!LoadArguments(target, prefix, resultVar, ref stateVar, scratch, prefixCode, report, touched))
                {
                    return false;
                }
                prefixCode.Add(Instruction.Create(OpCodes.Call, module.ImportReference(prefix)));
                var ret = prefix.ReturnType;
                if (ret.MetadataType == MetadataType.Boolean)
                {
                    prefixCode.Add(Instruction.Create(OpCodes.Brfalse, postfixStart));
                }
                else if (ret.MetadataType != MetadataType.Void)
                {
                    report.Note(
                        $"{Where(prefix)} returns {ret.Name}; a prefix has to return void or bool");
                    return false;
                }
            }

            if (postfix is not null)
            {
                if (!LoadArguments(target, postfix, resultVar, ref stateVar, scratch, postfixCode, report, touched))
                {
                    return false;
                }
                postfixCode.Add(Instruction.Create(OpCodes.Call, module.ImportReference(postfix)));
                var ret = postfix.ReturnType;
                if (ret.MetadataType != MetadataType.Void)
                {
                    // A postfix may return the value it was given, which is how a
                    // patch rewrites a result without a by-ref parameter.
                    if (resultVar is not null && Assignable(target.ReturnType, ret))
                    {
                        postfixCode.Add(Instruction.Create(OpCodes.Stloc, resultVar));
                    }
                    else
                    {
                        postfixCode.Add(Instruction.Create(OpCodes.Pop));
                        report.Note($"{Where(postfix)} returns a value that does not fit the patched method; ignoring it");
                    }
                }
            }

            // The gate, wrapped around each half independently.
            //
            // Around the calls rather than around the whole method: the
            // original body has to run either way, and a bool prefix that is
            // switched off must not take its "skip the original" branch with
            // it. Two tests rather than one because the prefix and the postfix
            // are separated by the body, and a branch cannot span it.
            if (gate is not null)
            {
                var gateRef = module.ImportReference(gate);
                if (prefixCode.Count > 0)
                {
                    var after = Instruction.Create(OpCodes.Nop);
                    prefixCode.Insert(0, Instruction.Create(OpCodes.Brfalse, after));
                    prefixCode.Insert(0, Instruction.Create(OpCodes.Ldsfld, gateRef));
                    prefixCode.Add(after);
                }
                if (postfixCode.Count > 0)
                {
                    var after = Instruction.Create(OpCodes.Nop);
                    postfixCode.Insert(0, Instruction.Create(OpCodes.Brfalse, after));
                    postfixCode.Insert(0, Instruction.Create(OpCodes.Ldsfld, gateRef));
                    postfixCode.Add(after);
                }
            }

            // ── committed from here ────────────────────────────────────────────

            body.SimplifyMacros();
            simplified = true;

            if (firstWeave && resultVar is not null) body.Variables.Add(resultVar);
            foreach (var v in scratch) body.Variables.Add(v);
            if (body.Variables.Count > 0) body.InitLocals = true;

            var il = body.GetILProcessor();
            var first = body.Instructions[0];
            if (firstWeave)
            {
                var rets = body.Instructions.Where(i => i.OpCode.Code == Code.Ret).ToList();
                il.Append(postfixStart);

                // A null end means the end of the old body, not the postfixes
                // and return path that are being appended outside its handlers.
                foreach (var handler in body.ExceptionHandlers)
                {
                    handler.TryEnd ??= postfixStart;
                    handler.HandlerEnd ??= postfixStart;
                }

                foreach (var ret in rets)
                {
                    // Leaving a protected region must also run its finally.
                    var jump = Protected(body, ret) ? OpCodes.Leave : OpCodes.Br;
                    if (resultVar is not null)
                    {
                        // Keep branch targets and exception boundaries attached
                        // to the original instruction.
                        il.InsertAfter(ret, Instruction.Create(jump, postfixStart));
                        ret.OpCode = OpCodes.Stloc;
                        ret.Operand = resultVar;
                    }
                    else
                    {
                        ret.OpCode = jump;
                        ret.Operand = postfixStart;
                    }
                }

                il.Append(returnStart);
                if (resultVar is not null) il.Append(Instruction.Create(OpCodes.Ret));
                _targets.Add(target, new Composition(resultVar, postfixStart, returnStart));
            }

            foreach (var ins in postfixCode) il.InsertBefore(returnStart, ins);

            // Last, so that the branches above are already pointing at real
            // instructions. Inserting before the original first instruction keeps
            // every existing branch into it correct.
            foreach (var ins in prefixCode) il.InsertBefore(first, ins);

            // A Harmony patch method is private by convention, and it is now
            // called from another assembly. Nothing else can grant that access:
            // the alternative is a MethodAccessException at the first patched call,
            // which under IL2CPP surfaces as a crash rather than an exception.
            Publicise(prefix);
            Publicise(postfix);
            if (prefix is not null) touched(prefix.Module.Assembly);
            if (postfix is not null) touched(postfix.Module.Assembly);
            touched(target.Module.Assembly);

            return true;
        }
        finally
        {
            if (simplified) body.OptimizeMacros();
        }
    }

    /// <summary>
    /// Makes a patch method reachable from the assembly it is woven into.
    ///
    /// The type has to come with it, and so does every type it is nested in:
    /// a public method on a private nested class is still unreachable.
    /// </summary>
    static void Publicise(MethodDefinition? method)
    {
        if (method is null) return;
        method.IsPublic = true;
        for (var type = method.DeclaringType; type is not null; type = type.DeclaringType)
        {
            if (type.IsNested) type.IsNestedPublic = true;
            else type.IsPublic = true;
        }
    }

    /// <summary>
    /// Emits the loads for one patch method's parameters.
    ///
    /// Harmony names its injections rather than positioning them: `__instance`
    /// is the receiver, `__result` the return value, `___field` a private
    /// field, `__state` a value handed from the prefix to the postfix, and
    /// anything else is matched against the patched method's own parameter
    /// names. All of them may be by-ref, which is how a patch changes what it
    /// was given.
    /// </summary>
    static bool LoadArguments(
        MethodDefinition target,
        MethodDefinition patch,
        VariableDefinition? resultVar,
        ref VariableDefinition? stateVar,
        List<VariableDefinition> scratch,
        List<Instruction> into,
        PluginReport report,
        Action<AssemblyDefinition> touched)
    {
        var module = target.Module;

        foreach (var p in patch.Parameters)
        {
            var byRef = p.ParameterType.IsByReference;
            var want = byRef ? ((ByReferenceType)p.ParameterType).ElementType : p.ParameterType;
            var name = p.Name ?? "";

            switch (name)
            {
                case "__instance":
                    {
                        if (target.IsStatic)
                        {
                            report.Note($"{Where(patch)} wants __instance, but {target.Name} is static");
                            return false;
                        }
                        if (target.DeclaringType.IsValueType)
                        {
                            report.Note($"{Where(patch)} patches a struct method, which is not supported");
                            return false;
                        }
                        if (byRef)
                        {
                            report.Note($"{Where(patch)} takes __instance by ref, which is not supported");
                            return false;
                        }
                        if (!Assignable(want, target.DeclaringType))
                        {
                            report.Note($"{Where(patch)} types __instance as {want.Name}, which {target.DeclaringType.Name} is not");
                            return false;
                        }
                        into.Add(Instruction.Create(OpCodes.Ldarg_0));
                        continue;
                    }

                case "__result":
                    {
                        if (resultVar is null)
                        {
                            report.Note($"{Where(patch)} wants __result, but {target.Name} returns nothing");
                            return false;
                        }
                        if (!Assignable(want, target.ReturnType) && !(want.FullName == "System.Object" && !byRef))
                        {
                            report.Note($"{Where(patch)} types __result as {want.Name} rather than {target.ReturnType.Name}");
                            return false;
                        }
                        into.Add(byRef
                            ? Instruction.Create(OpCodes.Ldloca, resultVar)
                            : Instruction.Create(OpCodes.Ldloc, resultVar));
                        if (!byRef && want.FullName == "System.Object" && target.ReturnType.IsValueType)
                        {
                            into.Add(Instruction.Create(OpCodes.Box, module.ImportReference(target.ReturnType)));
                        }
                        continue;
                    }

                case "__state":
                    {
                        if (stateVar is null)
                        {
                            stateVar = new VariableDefinition(module.ImportReference(want));
                            scratch.Add(stateVar);
                        }
                        else if (stateVar.VariableType.FullName != want.FullName)
                        {
                            report.Note($"{Where(patch)} types __state differently from its prefix");
                            return false;
                        }
                        into.Add(byRef
                            ? Instruction.Create(OpCodes.Ldloca, stateVar)
                            : Instruction.Create(OpCodes.Ldloc, stateVar));
                        continue;
                    }

                case "__originalMethod":
                case "__args":
                case "__runOriginal":
                case "__exception":
                    report.Note($"{Where(patch)} uses {name}, which a build-time weaver cannot provide");
                    return false;
            }

            if (name.StartsWith("___", StringComparison.Ordinal))
            {
                var fieldName = name.Substring(3);
                var field = FindField(target.DeclaringType, fieldName);
                if (field is null)
                {
                    report.Note($"{target.DeclaringType.Name} has no field called {fieldName}");
                    return false;
                }
                // Same reasoning as Publicise: the load is emitted into the
                // patched method, which for an inherited field can be in a
                // different assembly from the one that declares it.
                if (!field.IsPublic)
                {
                    field.IsPublic = true;
                    touched(field.Module.Assembly);
                }
                var fieldRef = module.ImportReference(field);
                if (field.IsStatic)
                {
                    into.Add(Instruction.Create(byRef ? OpCodes.Ldsflda : OpCodes.Ldsfld, fieldRef));
                }
                else
                {
                    if (target.IsStatic)
                    {
                        report.Note($"{Where(patch)} reads the instance field {fieldName} from a static method");
                        return false;
                    }
                    into.Add(Instruction.Create(OpCodes.Ldarg_0));
                    into.Add(Instruction.Create(byRef ? OpCodes.Ldflda : OpCodes.Ldfld, fieldRef));
                }
                continue;
            }

            var arg = target.Parameters.FirstOrDefault(t => t.Name == name);
            if (arg is null)
            {
                report.Note($"{Where(patch)} takes a parameter called {name}, which {target.Name} does not have");
                return false;
            }

            var argByRef = arg.ParameterType.IsByReference;
            var argType = argByRef ? ((ByReferenceType)arg.ParameterType).ElementType : arg.ParameterType;
            if (!Assignable(want, argType) && want.FullName != "System.Object")
            {
                report.Note($"{Where(patch)} types {name} as {want.Name} rather than {argType.Name}");
                return false;
            }

            if (argByRef)
            {
                // The argument is already a pointer: hand it on as one, or read
                // through it.
                into.Add(Instruction.Create(OpCodes.Ldarg, arg));
                if (!byRef)
                {
                    into.Add(argType.IsValueType
                        ? Instruction.Create(OpCodes.Ldobj, module.ImportReference(argType))
                        : Instruction.Create(OpCodes.Ldind_Ref));
                }
            }
            else
            {
                into.Add(Instruction.Create(byRef ? OpCodes.Ldarga : OpCodes.Ldarg, arg));
            }

            if (!byRef && want.FullName == "System.Object" && argType.IsValueType)
            {
                into.Add(Instruction.Create(OpCodes.Box, module.ImportReference(argType)));
            }
        }

        return true;
    }

    static FieldDefinition? FindField(TypeDefinition? type, string name)
    {
        for (var t = type; t is not null; t = SafeResolve(t.BaseType))
        {
            var f = t.Fields.FirstOrDefault(x => x.Name == name);
            if (f is not null) return f;
        }
        return null;
    }

    /// Whether a value of <paramref name="have"/> can be handed to a parameter
    /// of <paramref name="want"/> without a conversion.
    static bool Assignable(TypeReference want, TypeReference have)
    {
        if (want.FullName == have.FullName) return true;
        if (want.FullName == "System.Object" && !have.IsValueType) return true;
        if (have.IsValueType || want.IsValueType) return false;

        for (var t = SafeResolve(have); t is not null; t = SafeResolve(t.BaseType))
        {
            if (t.FullName == want.FullName) return true;
            if (t.Interfaces.Any(i => i.InterfaceType.FullName == want.FullName)) return true;
        }
        return false;
    }

    static TypeDefinition? SafeResolve(TypeReference? reference)
    {
        if (reference is null) return null;
        try { return reference.Resolve(); }
        catch (Exception) { return null; }
    }

    /// Whether an instruction sits inside a try, a handler or a filter.
    static bool Protected(MethodBody body, Instruction instruction)
    {
        if (!body.HasExceptionHandlers) return false;
        var offset = body.Instructions.IndexOf(instruction);
        foreach (var h in body.ExceptionHandlers)
        {
            if (Within(body, h.TryStart, h.TryEnd, offset)) return true;
            if (Within(body, h.HandlerStart, h.HandlerEnd, offset)) return true;
            if (h.FilterStart is not null && Within(body, h.FilterStart, h.HandlerStart, offset)) return true;
        }
        return false;
    }

    static bool Within(MethodBody body, Instruction? start, Instruction? end, int offset)
    {
        if (start is null) return false;
        var from = body.Instructions.IndexOf(start);
        var to = end is null ? body.Instructions.Count : body.Instructions.IndexOf(end);
        return offset >= from && offset < to;
    }

    static string Where(MethodDefinition m) => $"{m.DeclaringType.Name}.{m.Name}";
}
