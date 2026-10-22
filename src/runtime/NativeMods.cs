using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

// Optional mod data is separate from the game's resource pack and save files.
// The contract is baked into the IPA; no DLL is loaded from Documents.
public static class NativeMods
{
    [Serializable] public sealed class FileRecord { public string path; public long bytes; }
    [Serializable] public sealed class Overlay { public string bundle; public long bytes; }
    [Serializable] public sealed class Contract
    {
        public int schema;
        public string compatibility;
        public FileRecord[] files;
        public Overlay[] overrides;
    }
    static readonly Dictionary<string, string> paths = new Dictionary<string, string>(StringComparer.Ordinal);
    static readonly Dictionary<string, long> sizes = new Dictionary<string, long>(StringComparer.Ordinal);
    static bool ready, started;

    public static void Install(string compatibility)
    {
        string embedded = Path.Combine(Application.streamingAssetsPath, "native-mod-index.json");
        if (!File.Exists(embedded)) return;
        string root = Path.Combine(Application.persistentDataPath, "Mods");
        try
        {
            if (!Directory.Exists(root) || File.Exists(Path.Combine(root, "disabled.txt")))
            {
                Debug.Log("[NativeMods] Disabled or data absent; using original game");
                return;
            }
            var contract = JsonUtility.FromJson<Contract>(File.ReadAllText(embedded));
            if (contract == null || contract.schema != 1 || contract.compatibility != compatibility ||
                contract.files == null || contract.overrides == null)
                throw new InvalidDataException("Invalid mod contract");
            foreach (var file in contract.files)
            {
                string path = NativeResourceStore.LocalPath(root, file.path);
                if (file.bytes < 1 || !File.Exists(path) || new FileInfo(path).Length != file.bytes)
                    throw new InvalidDataException("Missing or incompatible mod file: " + file.path);
            }
            foreach (var overlay in contract.overrides)
            {
                string path = NativeResourceStore.LocalPath(root, "overrides/" + overlay.bundle);
                if (overlay.bytes < 1 || !File.Exists(path) || new FileInfo(path).Length != overlay.bytes)
                    throw new InvalidDataException("Invalid skin overlay: " + overlay.bundle);
                paths.Add(overlay.bundle, path);
                sizes.Add(overlay.bundle, overlay.bytes);
            }
            ready = true;
            Debug.Log("[NativeMods] Data accepted; skin bundles=" + paths.Count);
        }
        catch (Exception ex)
        {
            paths.Clear();
            sizes.Clear();
            ready = false;
            Debug.LogWarning("[NativeMods] Mods disabled: " + ex.Message);
        }
    }

    public static string Resolve(string bundle, string original, ref long bytes)
    {
        string path;
        if (ready && paths.TryGetValue(bundle, out path))
        {
            bytes = sizes[bundle];
            return path;
        }
        return original;
    }

    public static void StartPlugins()
    {
        if (!ready || started) return;
        started = true;
        // Explicit type lookup in an AOT-compiled assembly, never Assembly.Load.
        try
        {
            var type = Type.GetType("BepInEx.Bootstrap.Chainloader, BepInEx", true);
            type.GetMethod("Start", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
            Debug.Log("[NativeMods] Compiled plugins started");
        }
        catch (Exception ex) { Debug.LogWarning("[NativeMods] Plugin startup failed: " + ex); }
    }
}
