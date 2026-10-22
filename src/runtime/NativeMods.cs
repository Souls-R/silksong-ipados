using System;
using System.IO;
using System.Reflection;
using UnityEngine;

// Only compiled plugins use an embedded contract. Skin data is independent.
// No DLL is loaded from Documents.
public static class NativeMods
{
    [Serializable] public sealed class FileRecord { public string path; public long bytes; }
    [Serializable] public sealed class Contract
    {
        public int schema;
        public string compatibility;
        public FileRecord[] files;
    }
    public static bool Ready { get; private set; }
    static bool started;

    public static void Install(string compatibility)
    {
        Ready = false;
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
            if (contract == null || contract.schema != 2 || contract.compatibility != compatibility ||
                contract.files == null || contract.files.Length == 0)
                throw new InvalidDataException("Invalid mod contract");
            foreach (var file in contract.files)
            {
                string path = NativeResourceStore.LocalPath(root, file.path);
                if (file.bytes < 1 || !File.Exists(path) || new FileInfo(path).Length != file.bytes)
                    throw new InvalidDataException("Missing or incompatible mod file: " + file.path);
            }
            Ready = true;
            Debug.Log("[NativeMods] Compiled plugin data accepted");
        }
        catch (Exception ex)
        {
            Ready = false;
            Debug.LogWarning("[NativeMods] Mods disabled: " + ex.Message);
        }
    }

    public static void StartPlugins()
    {
        if (!Ready || started) return;
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
