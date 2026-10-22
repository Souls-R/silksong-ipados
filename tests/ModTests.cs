using System;
using System.IO;
using System.Text.Json;

namespace UnityEngine
{
    public static class Application { public static string persistentDataPath, streamingAssetsPath, dataPath, productName; }
    public static class Debug { public static void Log(object message) {} public static void LogWarning(object message) {} }
    public static class JsonUtility
    {
        public static T FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { IncludeFields = true });
    }
}
static class ModTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static string Json(object value) => JsonSerializer.Serialize(value);
    static void Main()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try { Run(root); }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Independent skin replacement and plugin checks passed");
    }
    static void Run(string root)
    {
        UnityEngine.Application.streamingAssetsPath = Path.Combine(root, "Raw");
        UnityEngine.Application.persistentDataPath = Path.Combine(root, "Documents");
        Directory.CreateDirectory(UnityEngine.Application.streamingAssetsPath);
        string mods = Path.Combine(UnityEngine.Application.persistentDataPath, "Mods");
        string skin = Path.Combine(UnityEngine.Application.persistentDataPath, "SilksongSkin");
        Directory.CreateDirectory(Path.Combine(mods, "plugins"));
        Directory.CreateDirectory(Path.Combine(skin, "bundles"));
        string embedded = Path.Combine(UnityEngine.Application.streamingAssetsPath, "native-mod-index.json");
        string manifest = Path.Combine(skin, "skin.json");
        string data = Path.Combine(mods, "plugins/data");
        string a = Path.GetFullPath(Path.Combine(skin, "bundles/a.bundle"));
        string b = Path.GetFullPath(Path.Combine(skin, "bundles/b.bundle"));
        var resources = new NativeResourceStore.Contract { schema = 2, storage = "documents",
            folder = "SilksongResources-v1", pack_id = "base", compatibility = "v1",
            bundles = new[] { "a.bundle", "b.bundle" } };
        Action install = () => { NativeSkin.Install(resources); NativeMods.Install("v1"); };
        Func<string, string> resolve = bundle => { long size = 17; return NativeSkin.Resolve(bundle, "original", ref size); };
        string pluginContract = "{\"schema\":2,\"compatibility\":\"v1\",\"files\":[{\"path\":\"plugins/data\",\"bytes\":2}]}";
        string skinA = Json(new { schema = 1, name = "Skin A", compatibility = "v1",
            bundles = new[] { new { bundle = "a.bundle", bytes = 3 } } });
        string skinB = Json(new { schema = 1, name = "Skin B", compatibility = "v1",
            bundles = new[] { new { bundle = "b.bundle", bytes = 5 } } });
        install();
        Check(!NativeMods.Ready && resolve("a.bundle") == "original", "no optional data");
        File.WriteAllText(embedded, pluginContract);
        File.WriteAllText(data, "hp");
        install();
        Check(NativeMods.Ready && NativeSkin.ActiveName == null, "HP runs without a skin");
        File.WriteAllText(a, "abc");
        File.WriteAllText(manifest, skinA);
        install();
        long bytes = 17;
        Check(NativeMods.Ready && NativeSkin.ActiveName == "Skin A", "both enabled");
        Check(NativeSkin.Resolve("a.bundle", "original", ref bytes) == a && bytes == 3, "skin routing and size");
        File.WriteAllText(b, "12345");
        File.WriteAllText(manifest, skinB);
        install();
        Check(NativeMods.Ready && NativeSkin.ActiveName == "Skin B", "replace skin with same IPA");
        Check(resolve("a.bundle") == "original" && resolve("b.bundle") == b, "old files ignored after replacement");
        Check(File.ReadAllText(embedded) == pluginContract, "no skin metadata baked into IPA");
        File.Delete(data);
        install();
        Check(!NativeMods.Ready && resolve("b.bundle") == b, "skin works when HP data is missing");
        File.Delete(embedded);
        install();
        Check(!NativeMods.Ready && resolve("b.bundle") == b, "skin works in an IPA without compiled mods");
        File.WriteAllText(embedded, pluginContract);
        File.WriteAllText(data, "hp");
        File.WriteAllText(Path.Combine(mods, "disabled.txt"), "");
        install();
        Check(!NativeMods.Ready && resolve("b.bundle") == b, "plugin disable switch does not disable skin");
        File.Delete(Path.Combine(mods, "disabled.txt"));
        File.WriteAllText(Path.Combine(skin, "disabled.txt"), "");
        install();
        Check(NativeMods.Ready && resolve("b.bundle") == "original", "skin disable switch does not disable HP");
        File.Delete(Path.Combine(skin, "disabled.txt"));
        foreach (string bad in new[] {
            skinA.Replace("v1", "v2"), skinA.Replace("a.bundle", "../escape.bundle"),
            skinA.Replace("a.bundle", "unknown.bundle"), skinA.Replace("a.bundle", "A.bundle"),
            skinA.Replace("\"schema\":1", "\"schema\":99"), skinA.Replace("\"bytes\":3", "\"bytes\":-1"),
            "{", "null", new string(' ', 1024 * 1024 + 1),
            Json(new { schema = 1, name = "Duplicate", compatibility = "v1", bundles = new[] {
                new { bundle = "a.bundle", bytes = 3 }, new { bundle = "a.bundle", bytes = 3 } } }),
            Json(new { schema = 1, name = "Empty", compatibility = "v1", bundles = new object[0] })
        })
        {
            File.WriteAllText(manifest, bad);
            install();
            Check(NativeMods.Ready && NativeSkin.ActiveName == null && resolve("a.bundle") == "original",
                  "invalid skin must not affect HP or retain previous routing");
        }
        File.WriteAllText(manifest, Json(new { schema = 1, name = "Partial", compatibility = "v1",
            bundles = new[] { new { bundle = "a.bundle", bytes = 3 }, new { bundle = "b.bundle", bytes = 6 } } }));
        install();
        Check(NativeMods.Ready && resolve("a.bundle") == "original" && resolve("b.bundle") == "original",
              "one partial file disables the entire skin before any routing");
        File.Delete(manifest);
        install();
        Check(NativeMods.Ready && NativeSkin.ActiveName == null, "missing manifest does not disable HP");
        File.WriteAllText(manifest, skinA);
        File.WriteAllText(embedded, pluginContract.Replace("plugins/data", "../escape"));
        install();
        Check(!NativeMods.Ready && resolve("a.bundle") == a, "invalid plugin path does not disable skin");

        // Model Files uncompressing a two-folder archive into SilksongMods.
        File.WriteAllText(embedded, pluginContract);
        string combined = Path.Combine(UnityEngine.Application.persistentDataPath, "SilksongMods");
        string combinedSkin = Path.Combine(combined, "SilksongSkin");
        string combinedMods = Path.Combine(combined, "Mods");
        Directory.CreateDirectory(Path.Combine(combinedSkin, "bundles"));
        Directory.CreateDirectory(Path.Combine(combinedMods, "plugins"));
        string combinedB = Path.GetFullPath(Path.Combine(combinedSkin, "bundles/b.bundle"));
        File.WriteAllText(combinedB, "12345");
        File.WriteAllText(Path.Combine(combinedSkin, "skin.json"), skinB);
        File.WriteAllText(Path.Combine(combinedMods, "plugins/data"), "hp");
        install();
        Check(NativeMods.Ready && NativeSkin.ActiveName == "Skin B" && resolve("b.bundle") == combinedB,
              "Files extraction container is loaded directly");
        Check(BepInEx.Paths.BepInExRootPath == combinedMods && resolve("a.bundle") == "original",
              "shim sidecars/config and loader select the same container; loose data ignored");
        File.WriteAllText(Path.Combine(combinedSkin, "skin.json"), "{");
        install();
        Check(NativeMods.Ready && NativeSkin.ActiveName == null && resolve("a.bundle") == "original",
              "bad combined skin does not silently load a stale loose skin");
        File.WriteAllText(Path.Combine(combinedSkin, "skin.json"), skinB);
        File.WriteAllText(Path.Combine(combinedMods, "disabled.txt"), "");
        install();
        Check(!NativeMods.Ready && resolve("b.bundle") == combinedB, "combined skin and HP remain independent");
        Directory.Move(combined, Path.Combine(root, "removed-combined-pack"));
        install();
        Check(NativeMods.Ready && resolve("a.bundle") == a && BepInEx.Paths.BepInExRootPath == mods,
              "removing combined pack restores standalone directories");
    }
}
