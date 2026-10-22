using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ModWeaver;

// These are platform substitutions, not changes to the mod's gameplay logic.
internal static class IOSCompat
{
    public static int Run(string[] args)
    {
        if (args.Length != 4)
            throw new ArgumentException("ios-compat <managed-dir> <input-plugin> <output-plugin>");
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(args[1]);
        using var shim = AssemblyDefinition.ReadAssembly(Path.Combine(args[1], "BepInEx.dll"));
        using var plugin = AssemblyDefinition.ReadAssembly(args[2], new ReaderParameters {
            AssemblyResolver = resolver, InMemory = true });
        var location = shim.MainModule.GetType("BepInEx.Paths").Methods.Single(m => m.Name == "GetAssemblyLocation");
        int paths = 0, fonts = 0;
        foreach (var type in Types(plugin.MainModule.Types))
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is MethodReference call && call.Name == "get_Location" &&
                call.DeclaringType.FullName == "System.Reflection.Assembly")
            {
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = plugin.MainModule.ImportReference(location);
                paths++;
            }
            if (instruction.OpCode == OpCodes.Ldstr && (string)instruction.Operand == "Arial.ttf")
            {
                instruction.Operand = "LegacyRuntime.ttf";
                fonts++;
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
        plugin.Write(args[3]);
        Console.WriteLine($"iOS compatibility: {paths} assembly path(s), {fonts} legacy font(s)");
        return 0;
    }

    static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var child in Types(type.NestedTypes)) yield return child;
        }
    }
}
