using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace UnityEngine
{
    public static class Application { public static string persistentDataPath, streamingAssetsPath; }
    public static class Debug { public static void Log(object message) {} public static void LogWarning(object message) {} }
    public static class JsonUtility
    {
        public static T FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { IncludeFields = true });
    }
}
static class ModTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reset()
    {
        foreach (string name in new[] { "paths", "sizes" })
            ((System.Collections.IDictionary)typeof(NativeMods).GetField(name, BindingFlags.NonPublic | BindingFlags.Static).GetValue(null)).Clear();
        foreach (string name in new[] { "ready", "started" })
            typeof(NativeMods).GetField(name, BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, false);
    }
    static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        UnityEngine.Application.streamingAssetsPath = Path.Combine(root, "Raw");
        UnityEngine.Application.persistentDataPath = Path.Combine(root, "Documents");
        Directory.CreateDirectory(UnityEngine.Application.streamingAssetsPath);
        string mods = Path.Combine(UnityEngine.Application.persistentDataPath, "Mods");
        Directory.CreateDirectory(Path.Combine(mods, "overrides"));
        Directory.CreateDirectory(Path.Combine(mods, "plugins"));
        string embedded = Path.Combine(UnityEngine.Application.streamingAssetsPath, "native-mod-index.json");
        string overlay = Path.GetFullPath(Path.Combine(mods, "overrides/a.bundle"));
        long bytes = 17;
        NativeMods.Install("v1");
        Check(NativeMods.Resolve("a.bundle", "original", ref bytes) == "original" && bytes == 17, "unmodded build");
        string contract = "{\"schema\":1,\"compatibility\":\"v1\",\"files\":[{\"path\":\"plugins/data\",\"bytes\":2}],\"overrides\":[{\"bundle\":\"a.bundle\",\"bytes\":3}]}";
        File.WriteAllText(embedded, contract);
        File.WriteAllText(overlay, "abc");
        NativeMods.Install("v1");
        Check(NativeMods.Resolve("a.bundle", "original", ref bytes) == "original", "missing HP data disables hooks and skin");
        File.WriteAllText(Path.Combine(mods, "plugins/data"), "hp");
        Reset(); NativeMods.Install("v1");
        Check(NativeMods.Resolve("a.bundle", "original", ref bytes) == overlay && bytes == 3, "accepted skin routes with correct size");
        Check(NativeMods.Resolve("b.bundle", "original", ref bytes) == "original", "unrelated bundle unchanged");
        Reset(); NativeMods.Install("v2");
        Check(NativeMods.Resolve("a.bundle", "original", ref bytes) == "original", "stale compatibility rejected");
        File.WriteAllText(Path.Combine(mods, "disabled.txt"), "");
        Reset(); NativeMods.Install("v1");
        Check(NativeMods.Resolve("a.bundle", "original", ref bytes) == "original", "disabled switch");
        File.Delete(Path.Combine(mods, "disabled.txt"));
        File.WriteAllText(embedded, contract.Replace("a.bundle", "../escape.bundle"));
        Reset(); NativeMods.Install("v1");
        Check(NativeMods.Resolve("../escape.bundle", "original", ref bytes) == "original", "unsafe path rejected");
        File.WriteAllText(embedded, contract);
        File.WriteAllText(overlay, "a");
        Reset(); NativeMods.Install("v1");
        Check(NativeMods.Resolve("a.bundle", "original", ref bytes) == "original", "partial copy rejected");
        Directory.Delete(root, true);
        Console.WriteLine("Optional mod checks passed");
    }
}
