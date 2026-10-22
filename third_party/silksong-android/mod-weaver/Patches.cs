using Mono.Cecil;

namespace ModWeaver;

/// Harmony's MethodType, by value. Kept here rather than read from the
/// plugin's own copy of 0Harmony because the values are part of the attribute
/// encoding -- an enum argument arrives as a boxed int and nothing else.
internal enum HarmonyMethodType
{
    Normal = 0,
    Getter = 1,
    Setter = 2,
    Constructor = 3,
    StaticConstructor = 4,
    Enumerator = 5,
    Async = 6,
}

/// <summary>
/// What a [HarmonyPatch] attribute said, before it is turned into a method.
///
/// Harmony spreads one target across any number of attributes -- on the class,
/// on the method, several of each -- and merges them, later winning over
/// earlier. This is that merge, and nothing more; resolution happens in
/// <see cref="Resolve"/>.
/// </summary>
internal sealed class PatchSpec
{
    public TypeReference? DeclaringType;

    /// The string form, from HarmonyPatch(string typeName, ...). Rare, but it
    /// is what a plugin uses to reach a type it cannot reference.
    public string? DeclaringTypeName;

    public string? MethodName;
    public TypeReference[]? ArgumentTypes;
    public HarmonyMethodType MethodType = HarmonyMethodType.Normal;

    public bool IsEmpty =>
        DeclaringType is null && DeclaringTypeName is null && MethodName is null &&
        ArgumentTypes is null && MethodType == HarmonyMethodType.Normal;

    public PatchSpec MergedWith(PatchSpec later)
    {
        var merged = new PatchSpec
        {
            DeclaringType = later.DeclaringType ?? DeclaringType,
            DeclaringTypeName = later.DeclaringTypeName ?? DeclaringTypeName,
            MethodName = later.MethodName ?? MethodName,
            ArgumentTypes = later.ArgumentTypes ?? ArgumentTypes,
            MethodType = later.MethodType != HarmonyMethodType.Normal ? later.MethodType : MethodType,
        };
        return merged;
    }

    /// <summary>
    /// Reads one [HarmonyPatch(...)] into a spec.
    ///
    /// Harmony has around a dozen constructor overloads and they are told
    /// apart by argument type rather than by count, so this reads the argument
    /// list positionally-by-type instead of matching a signature: a Type is
    /// the declaring type, a Type[] is the argument types, an enum is the
    /// MethodType, a string is the method name -- unless a second string
    /// follows, in which case the first was a type name.
    /// </summary>
    public static PatchSpec FromAttribute(CustomAttribute attr)
    {
        var spec = new PatchSpec();
        var strings = new List<string>();

        foreach (var arg in attr.ConstructorArguments) Read(arg, spec, strings);

        if (strings.Count == 1)
        {
            spec.MethodName = strings[0];
        }
        else if (strings.Count >= 2)
        {
            spec.DeclaringTypeName = strings[0];
            spec.MethodName = strings[1];
        }

        // Harmony also allows the target to be given as properties rather than
        // constructor arguments on some versions. Cheap to honour.
        foreach (var prop in attr.Properties)
        {
            switch (prop.Name)
            {
                case "declaringType": spec.DeclaringType = prop.Argument.Value as TypeReference; break;
                case "methodName": spec.MethodName = prop.Argument.Value as string; break;
            }
        }

        return spec;
    }

    static void Read(CustomAttributeArgument arg, PatchSpec spec, List<string> strings)
    {
        switch (arg.Value)
        {
            case TypeReference type:
                spec.DeclaringType = type;
                break;
            case string s:
                strings.Add(s);
                break;
            case CustomAttributeArgument[] array:
                // Either Type[] (argument types) or a params array that Cecil
                // has already collapsed. ArgumentType[] -- Harmony's ref/out
                // variations -- is an enum array, and is not a target.
                if (array.Length == 0 || array[0].Value is TypeReference)
                {
                    spec.ArgumentTypes = array.Select(a => (TypeReference)a.Value).ToArray();
                }
                break;
            case CustomAttributeArgument nested:
                Read(nested, spec, strings);
                break;
            default:
                if (arg.Type.Name == "MethodType" && arg.Value is int m)
                {
                    spec.MethodType = (HarmonyMethodType)m;
                }
                break;
        }
    }

    /// <summary>
    /// Finds the method this spec names, in the game's own assemblies.
    ///
    /// Returns null and explains itself through <paramref name="why"/>: a
    /// target that has moved is the single most likely reason for a mod
    /// written against a different version of the game to fail, and "not found"
    /// with the name in it is the difference between a fixable report and a
    /// mystery.
    /// </summary>
    public MethodDefinition? Resolve(Func<string, TypeDefinition?> byName, out string why)
    {
        why = "";

        TypeDefinition? type = null;
        if (DeclaringType is not null)
        {
            type = Safely(() => DeclaringType.Resolve());
            if (type is null)
            {
                why = $"the type {DeclaringType.FullName} is not in this build of the game";
                return null;
            }
        }
        else if (DeclaringTypeName is not null)
        {
            type = byName(DeclaringTypeName);
            if (type is null)
            {
                why = $"the type {DeclaringTypeName} is not in this build of the game";
                return null;
            }
        }
        else
        {
            why = "the patch does not say which type it patches";
            return null;
        }

        var name = MethodName;
        switch (MethodType)
        {
            case HarmonyMethodType.Constructor: name = ".ctor"; break;
            case HarmonyMethodType.StaticConstructor: name = ".cctor"; break;
            case HarmonyMethodType.Getter: name = "get_" + name; break;
            case HarmonyMethodType.Setter: name = "set_" + name; break;
            case HarmonyMethodType.Enumerator:
            case HarmonyMethodType.Async:
                why = $"{MethodType} patches are not supported by a build-time weaver";
                return null;
        }
        if (string.IsNullOrEmpty(name))
        {
            why = $"the patch on {type.FullName} does not say which method it patches";
            return null;
        }

        // Walk the base chain: patching an inherited method is written against
        // the derived type as often as not.
        for (var t = type; t is not null; t = Safely(() => t!.BaseType?.Resolve()))
        {
            var candidates = t.Methods.Where(m => m.Name == name).ToList();
            if (candidates.Count == 0) continue;

            if (ArgumentTypes is not null)
            {
                var wanted = ArgumentTypes.Select(a => a.FullName).ToArray();
                var match = candidates.FirstOrDefault(m =>
                    m.Parameters.Count == wanted.Length &&
                    m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(wanted));
                if (match is null)
                {
                    why = $"{type.FullName}.{name} has no overload taking ({string.Join(", ", wanted)})";
                    return null;
                }
                candidates = new List<MethodDefinition> { match };
            }

            if (candidates.Count > 1)
            {
                why = $"{type.FullName}.{name} is overloaded and the patch does not say which one";
                return null;
            }
            if (!candidates[0].HasBody)
            {
                why = $"{type.FullName}.{name} has no IL to patch (abstract, or implemented natively)";
                return null;
            }
            return candidates[0];
        }

        why = $"{type.FullName} has no method called {name}";
        return null;
    }

    /// Cecil throws when a reference cannot be resolved, and a plugin that
    /// names something we do not have is an ordinary outcome here, not an
    /// exceptional one.
    static T? Safely<T>(Func<T?> f) where T : class
    {
        try { return f(); }
        catch (AssemblyResolutionException) { return null; }
        catch (Exception) { return null; }
    }
}
