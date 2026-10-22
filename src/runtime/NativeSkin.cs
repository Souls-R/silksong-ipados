using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// The skin manifest lives beside its data, so changing a skin never changes
// the IPA. Publish routing only after the complete skin has been checked.
public static class NativeSkin
{
    [Serializable] public sealed class Manifest
    {
        public int schema;
        public string name;
        public string compatibility;
        public NativeResourceStore.Record[] bundles;
    }
    sealed class Entry { public string path; public long bytes; }
    static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    public static string ActiveName { get; private set; }

    public static void Install(NativeResourceStore.Contract resources)
    {
        entries.Clear();
        ActiveName = null;
        string root = Path.Combine(NativeModPaths.Root, "SilksongSkin");
        if (!Directory.Exists(root) || File.Exists(Path.Combine(root, "disabled.txt"))) return;
        try
        {
            NativeResourceStore.ValidateContract(resources);
            string manifestPath = Path.Combine(root, "skin.json");
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 1024 * 1024)
                throw new InvalidDataException("Missing or oversized skin.json");
            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
            if (manifest == null || manifest.schema != 1 || string.IsNullOrEmpty(manifest.name) ||
                manifest.compatibility != resources.compatibility || manifest.bundles == null ||
                manifest.bundles.Length == 0 || manifest.bundles.Length > resources.bundles.Length)
                throw new InvalidDataException("Skin does not match this resource version");
            var allowed = new HashSet<string>(resources.bundles, StringComparer.Ordinal);
            var selected = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var record in manifest.bundles)
            {
                if (record == null || record.bytes <= 0 || record.bundle == null ||
                    !allowed.Contains(record.bundle) || selected.ContainsKey(record.bundle))
                    throw new InvalidDataException("Invalid, unknown or duplicate skin bundle");
                string path = NativeResourceStore.LocalPath(root, "bundles/" + record.bundle);
                if (!File.Exists(path) || new FileInfo(path).Length != record.bytes)
                    throw new InvalidDataException("Incomplete skin file: " + record.bundle);
                selected.Add(record.bundle, new Entry { path = path, bytes = record.bytes });
            }
            foreach (var pair in selected) entries.Add(pair.Key, pair.Value);
            ActiveName = manifest.name;
            Debug.Log("[NativeSkin] Accepted " + ActiveName + "; bundles=" + entries.Count);
        }
        catch (Exception ex)
        {
            entries.Clear();
            ActiveName = null;
            Debug.LogWarning("[NativeSkin] Original appearance selected: " + ex.Message);
        }
    }

    public static string Resolve(string bundle, string original, ref long bytes)
    {
        Entry entry;
        if (entries.TryGetValue(bundle, out entry))
        {
            bytes = entry.bytes;
            return entry.path;
        }
        return original;
    }
}
