using System;
using System.IO;
using System.Text.Json;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

static class BootstrapTests
{
    sealed class Location : IResourceLocation
    {
        public string InternalId { get; set; }
        public object Data { get; set; }
    }
    static readonly JsonSerializerOptions Json = new JsonSerializerOptions { IncludeFields = true };
    static void Write(string file, object data) => File.WriteAllText(file, JsonSerializer.Serialize(data, Json));
    static void Assert(bool valid, string message) { if (!valid) throw new Exception(message); }

    static void Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "bootstrap-order-" + Guid.NewGuid().ToString("N"));
        Application.persistentDataPath = Path.Combine(root, "Documents");
        Application.streamingAssetsPath = Path.Combine(root, "Raw");
        Application.temporaryCachePath = Path.Combine(root, "Caches");
        string pack = Path.Combine(Application.persistentDataPath, "SilksongResources-v1");
        string bundles = Path.Combine(pack, "aa/StandaloneOSX");
        Directory.CreateDirectory(bundles);
        Directory.CreateDirectory(Application.streamingAssetsPath);
        try
        {
            var contract = new NativeResourceStore.Contract {
                schema = 2, storage = "documents", folder = "SilksongResources-v1",
                pack_id = "silksong-ios-resources-v1", compatibility = "test-catalog",
                bundles = new[] { "startup.bundle" }
            };
            var index = new NativeResourceStore.Index {
                storage = contract.storage, folder = contract.folder,
                pack = new NativeResourceStore.Manifest {
                    schema = 1, pack_id = contract.pack_id, compatibility = contract.compatibility,
                    bundle_count = 1, bundle_bytes = 7
                },
                bundles = new[] { new NativeResourceStore.Record { bundle = "startup.bundle", bytes = 7 } }
            };
            Write(Path.Combine(Application.streamingAssetsPath, "native-resource-index.json"), contract);
            Write(Path.Combine(pack, "pack.json"), index.pack);
            Write(Path.Combine(pack, "resource-index.json"), index);
            File.WriteAllBytes(Path.Combine(bundles, "startup.bundle"), new byte[7]);

            // Original callbacks can request assets before Boot/Start, while
            // the user still has the declaration open. No coroutine has run.
            NativeBootstrap.Install();
            var options = new AssetBundleRequestOptions { Crc = 42, Hash = "desktop", BundleSize = 3,
                UseUnityWebRequestForLocalBundles = true };
            var location = new Location { InternalId = "original/aa/StandaloneOSX/startup.bundle", Data = options };
            string resolved = Addressables.ResourceManager.InternalIdTransformFunc(location);
            Assert(resolved == Path.GetFullPath(Path.Combine(bundles, "startup.bundle")), "early request must use Documents");
            Assert(options.BundleSize == 7, "early request must use current pack size, not catalog size");
            Assert(options.Crc == 0 && options.Hash == "" && !options.UseUnityWebRequestForLocalBundles,
                "desktop validation options must be cleared");
            location.InternalId = "catalog.bin";
            Assert(Addressables.ResourceManager.InternalIdTransformFunc(location) == "catalog.bin", "catalog pass-through");
            location.InternalId = "original/aa/StandaloneOSX/unknown.bundle";
            bool rejected = false;
            try { Addressables.ResourceManager.InternalIdTransformFunc(location); }
            catch (FileNotFoundException) { rejected = true; }
            Assert(rejected, "unknown resource must still be rejected");
            Console.WriteLine("Bootstrap early resource requests: passed");
        }
        finally { Directory.Delete(root, true); }
    }
}
