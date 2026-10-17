using System;
using System.IO;
using System.Text.Json;

// Compile the production file store directly; no substitute implementation.
static class ResourceStoreTests
{
    const string Version = "6000.0.50f1:ios-r2:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    static readonly JsonSerializerOptions Json = new JsonSerializerOptions { IncludeFields = true };
    static int passed;

    static NativeResourceStore.Contract Contract()
    {
        return new NativeResourceStore.Contract {
            schema = 2, storage = "documents", folder = "SilksongResources-v1",
            pack_id = "silksong-ios-resources-v1", compatibility = Version,
            bundles = new[] { "nested/a.bundle", "b.bundle" }
        };
    }

    static NativeResourceStore.Index Index(long first = 3, long second = 5)
    {
        return new NativeResourceStore.Index {
            storage = "documents", folder = "SilksongResources-v1",
            pack = new NativeResourceStore.Manifest {
                schema = 1, pack_id = "silksong-ios-resources-v1", compatibility = Version,
                bundle_count = 2, bundle_bytes = first + second
            },
            bundles = new[] {
                new NativeResourceStore.Record { bundle = "nested/a.bundle", bytes = first },
                new NativeResourceStore.Record { bundle = "b.bundle", bytes = second }
            }
        };
    }

    static void Assert(bool value, string name)
    {
        if (!value) throw new Exception(name);
        passed++;
    }

    static void Reject(Action action, string name)
    {
        try { action(); }
        catch (InvalidDataException) { passed++; return; }
        catch (OverflowException) { passed++; return; }
        throw new Exception("Accepted invalid input: " + name);
    }

    static T Read<T>(string path) { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json); }

    static void Main(string[] args)
    {
        if (args.Length == 2)
        {
            var contract = Read<NativeResourceStore.Contract>(args[0]);
            var header = Read<NativeResourceStore.Manifest>(Path.Combine(args[1], "pack.json"));
            var index = Read<NativeResourceStore.Index>(Path.Combine(args[1], "resource-index.json"));
            NativeResourceStore.ValidatePack(contract, header, index);
            Console.WriteLine("Accepted " + index.bundles.Length + " files, " + header.bundle_bytes + " bytes");
            return;
        }

        var expected = Contract();
        foreach (var candidate in new[] { Index(), Index(7, 9) })
        {
            NativeResourceStore.ValidatePack(expected, candidate.pack, candidate);
            passed++;
        }
        foreach (string version in new[] { null, "other-game-catalog", Version.Replace("ios-r2", "ios-r3") })
        {
            var candidate = Index(); candidate.pack.compatibility = version;
            Reject(() => NativeResourceStore.ValidatePack(expected, candidate.pack, candidate), "version");
        }
        var wrongHeader = Index().pack; wrongHeader.bundle_bytes++;
        Reject(() => NativeResourceStore.ValidatePack(expected, wrongHeader, Index()), "header totals");
        Reject(() => NativeResourceStore.ValidatePack(expected, null, Index()), "missing header");
        var totals = Index(); totals.pack.bundle_count++;
        Reject(() => NativeResourceStore.ValidatePack(expected, totals.pack, totals), "index totals");
        var missing = Index(); missing.bundles = new[] { missing.bundles[0] };
        missing.pack.bundle_count = 1; missing.pack.bundle_bytes = 3;
        Reject(() => NativeResourceStore.ValidatePack(expected, missing.pack, missing), "missing file");
        foreach (string name in new[] { "unexpected.bundle", "../a.bundle", "/a.bundle", "NESTED/A.bundle" })
        {
            var candidate = Index(); candidate.bundles[1].bundle = name;
            Reject(() => NativeResourceStore.ValidatePack(expected, candidate.pack, candidate), name);
        }
        var casing = Index(); casing.bundles[0].bundle = "Nested/a.bundle";
        Reject(() => NativeResourceStore.ValidatePack(expected, casing.pack, casing), "case mismatch");
        var overflow = Index(); overflow.bundles[0].bytes = long.MaxValue;
        Reject(() => NativeResourceStore.ValidatePack(expected, overflow.pack, overflow), "overflow");
        var unsafeContract = Contract(); unsafeContract.bundles[0] = "../a.bundle";
        Reject(() => NativeResourceStore.ValidateContract(unsafeContract), "unsafe contract");
        var duplicate = Contract(); duplicate.bundles[1] = "NESTED/A.bundle";
        Reject(() => NativeResourceStore.ValidateContract(duplicate), "duplicate contract");

        string root = Path.Combine(Path.GetTempPath(), "resource-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        try
        {
            var candidate = Index(7, 9);
            var file = candidate.bundles[0];
            Assert(NativeResourceStore.CheckFile(root, file) != null, "missing physical file");
            string path = Path.Combine(root, "nested/a.bundle");
            File.WriteAllBytes(path, new byte[3]);
            Assert(NativeResourceStore.CheckFile(root, file) != null, "old pack size must not pass");
            File.WriteAllBytes(path, new byte[7]);
            Assert(NativeResourceStore.CheckFile(root, file) == null, "own pack size must pass");

            string contract = JsonSerializer.Serialize(expected, Json);
            string header = JsonSerializer.Serialize(candidate.pack, Json);
            string index = JsonSerializer.Serialize(candidate, Json);
            string stamp = NativeResourceStore.AcceptanceStamp(contract, header, index, 1, 2, 3);
            string cache = Path.Combine(root, "cache/accepted.txt");
            Assert(!NativeResourceStore.IsAccepted(cache, stamp, false), "fresh cache");
            NativeResourceStore.Remember(cache, stamp);
            Assert(NativeResourceStore.IsAccepted(cache, stamp, false), "cached launch");
            Assert(!NativeResourceStore.IsAccepted(cache, stamp, true), "manual recheck");
            candidate.bundles[0].bytes++; candidate.bundles[1].bytes--;
            string changedIndex = JsonSerializer.Serialize(candidate, Json);
            string changed = NativeResourceStore.AcceptanceStamp(contract, header, changedIndex, 1, 2, 3);
            Assert(!NativeResourceStore.IsAccepted(cache, changed, false), "changed index with same totals and times");
            changed = NativeResourceStore.AcceptanceStamp(contract + " ", header, index, 1, 2, 3);
            Assert(!NativeResourceStore.IsAccepted(cache, changed, false), "updated application contract");
            NativeResourceStore.Invalidate(cache);
            Assert(!NativeResourceStore.IsAccepted(cache, stamp, false), "failed load invalidation");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("Resource store: " + passed + " checks passed");
    }
}
