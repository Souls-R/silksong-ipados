using System;
using System.Collections.Generic;
using System.IO;

// Files only: no Unity state, downloads, extraction, or content hashing.
public static class NativeResourceStore
{
    // Exact small contract/manifest text and filesystem metadata, not hashes.
    // Corrupt/evicted cache files cause a fresh names-and-sizes check.
    public static bool IsAccepted(string cache, string stamp, bool force)
    {
        if (force) return false;
        try { return File.ReadAllText(cache) == stamp; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    public static void Remember(string cache, string stamp)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(cache)); File.WriteAllText(cache, stamp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    public static void Invalidate(string cache)
    {
        try { if (cache != null) File.Delete(cache); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    [Serializable] public sealed class Record { public string bundle; public long bytes; }
    [Serializable] public sealed class Manifest
    {
        public int schema;
        public string pack_id;
        public string compatibility;
        public int bundle_count;
        public long bundle_bytes;
    }
    [Serializable] public sealed class Index
    {
        public string storage;
        public string folder;
        public Manifest pack;
        public Record[] bundles;
    }
    [Serializable] public sealed class Contract
    {
        public int schema;
        public string storage;
        public string folder;
        public string pack_id;
        public string compatibility;
        public string[] bundles;
    }

    public static void ValidateContract(Contract contract)
    {
        if (contract == null || contract.schema != 2 || contract.storage != "documents"
            || string.IsNullOrEmpty(contract.pack_id) || string.IsNullOrEmpty(contract.compatibility)
            || contract.bundles == null || contract.bundles.Length == 0)
            throw new InvalidDataException("Invalid application resource contract");
        ValidateFolder(contract.folder);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in contract.bundles)
        {
            ValidateRelative(name);
            if (!name.EndsWith(".bundle", StringComparison.Ordinal) || !names.Add(name))
                throw new InvalidDataException("Invalid or duplicate required resource: " + name);
        }
    }

    public static void ValidateIndex(Index index)
    {
        if (index == null || index.storage != "documents" || index.pack == null
            || index.pack.schema != 1 || string.IsNullOrEmpty(index.pack.pack_id)
            || string.IsNullOrEmpty(index.pack.compatibility)
            || index.bundles == null || index.bundles.Length == 0)
            throw new InvalidDataException("Invalid resource pack index");
        ValidateFolder(index.folder);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var record in index.bundles)
        {
            if (record == null) throw new InvalidDataException("Empty resource entry");
            ValidateRelative(record.bundle);
            if (!record.bundle.EndsWith(".bundle", StringComparison.Ordinal) || record.bytes <= 0 || !names.Add(record.bundle))
                throw new InvalidDataException("Invalid or duplicate resource: " + record.bundle);
            total = checked(total + record.bytes);
        }
        if (index.pack.bundle_count != names.Count || index.pack.bundle_bytes != total)
            throw new InvalidDataException("Resource index counts differ");
    }

    public static string CheckManifest(Contract expected, Manifest actual)
    {
        // Compatibility never depends on the compressed sizes of another pack.
        if (actual == null || actual.schema != 1 || actual.pack_id != expected.pack_id
            || actual.compatibility != expected.compatibility)
            return "Resource compatibility version does not match. Required: " + expected.compatibility;
        return null;
    }

    public static void ValidatePack(Contract expected, Manifest header, Index index)
    {
        ValidateContract(expected);
        ValidateIndex(index);
        string error = CheckManifest(expected, header) ?? CheckManifest(expected, index.pack);
        if (error != null) throw new InvalidDataException(error);
        if (index.storage != expected.storage || index.folder != expected.folder
            || header.bundle_count != index.pack.bundle_count || header.bundle_bytes != index.pack.bundle_bytes)
            throw new InvalidDataException("Resource pack metadata is inconsistent");
        var required = new HashSet<string>(expected.bundles, StringComparer.Ordinal);
        foreach (var record in index.bundles)
            if (!required.Remove(record.bundle))
                throw new InvalidDataException("Unexpected resource: " + record.bundle);
        if (required.Count != 0)
            throw new InvalidDataException("Resource pack is missing required files");
    }

    public static string AcceptanceStamp(string contract, string manifest, string index,
        long manifestTime, long indexTime, long directoryTime)
    {
        // Include actual index text: even an edit preserving totals/timestamps
        // must not reuse acceptance of a different pack. Only metadata is read.
        return "resource-cache-v2\n" + contract + "\n" + manifest + "\n" + index
            + "\n" + manifestTime + "\n" + indexTime + "\n" + directoryTime;
    }

    static void ValidateFolder(string folder)
    {
        ValidateRelative(folder);
        if (folder.IndexOf('/') >= 0)
            throw new InvalidDataException("Resource folder must be directly inside Documents");
    }

    static void ValidateRelative(string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.IndexOfAny(new[] { '\\', ':', '\0' }) >= 0
            || Path.IsPathRooted(relative)) throw new InvalidDataException("Invalid resource path");
        foreach (string part in relative.Split('/'))
            if (part.Length == 0 || part == "." || part == "..")
                throw new InvalidDataException("Invalid resource path: " + relative);
    }

    public static string LocalPath(string root, string relative)
    {
        ValidateRelative(relative);
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(prefix, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Resource outside pack");
        return path;
    }

    public static string CheckFile(string bundleRoot, Record record)
    {
        try
        {
            var file = new FileInfo(LocalPath(bundleRoot, record.bundle));
            if (!file.Exists || file.Length != record.bytes)
                return "Copy or extraction is incomplete: " + record.bundle;
            return null;
        }
        catch (IOException ex) { return "Cannot read resource: " + ex.Message; }
        catch (UnauthorizedAccessException ex) { return "Cannot read resource: " + ex.Message; }
    }
}
