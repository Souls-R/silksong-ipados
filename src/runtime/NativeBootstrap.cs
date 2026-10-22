using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

// The original Loader, managers, callbacks and scene transitions retain control.
public sealed class NativeBootstrap : MonoBehaviour
{
    public sealed class JsonCallbackCheck
    {
        public int value;
        [JsonIgnore] public bool callback;
        [OnDeserialized] void Restored(StreamingContext context) { callback = true; }
    }
    static readonly Dictionary<string, NativeResourceStore.Record> bundles = new Dictionary<string, NativeResourceStore.Record>(StringComparer.Ordinal);
    static string logPath, fatal, bundleRoot, packRoot, acceptedPath, contract;
    static NativeResourceStore.Contract resourceContract;
    static NativeBootstrap instance;
    static bool installed, booted, gameOwnsScenes;
    static long logBytes;
    bool resourcesWaiting, retryResources, recovering;
    static Action<AsyncOperationHandle, Exception> previousExceptionHandler;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
    public static void Install()
    {
        if (installed) return;
        installed = true;
        NativePreferences.Initialize();
        logPath = Path.Combine(Application.persistentDataPath, "native.log");
        acceptedPath = Path.Combine(Application.temporaryCachePath, "native-resource-accepted.txt");
        try { File.WriteAllText(logPath, "Native compatibility startup\n"); } catch { }
        Application.logMessageReceived += Log;
        NativeAnimatorCompatibility.Install();
        if (NativePreferences.ResourceLogs || NativePreferences.StartupDetails)
        {
            SceneManager.sceneLoaded += SceneLoaded;
            SceneManager.sceneUnloaded += SceneUnloaded;
        }
        try
        {
            contract = File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "native-resource-index.json"));
            resourceContract = JsonUtility.FromJson<NativeResourceStore.Contract>(contract);
            NativeResourceStore.ValidateContract(resourceContract);
            NativeSkin.Install(resourceContract);
            NativeMods.Install(resourceContract.compatibility);
            packRoot = NativeResourceStore.LocalPath(Application.persistentDataPath, resourceContract.folder);
            bundleRoot = Path.Combine(packRoot, "aa/StandaloneOSX");
            // Original PreWarm callbacks can request assets before Start(),
            // including while the declaration is open. Publish the selected
            // pack's routing table before registering the resolver. File checks
            // and missing-pack UI still run in WaitForResources.
            string stamp;
            NativeResourceStore.Index index;
            if (ReadResourceMetadata(out stamp, out index) == null) SetBundles(index);
            Addressables.ResourceManager.InternalIdTransformFunc = ResolveId;
            previousExceptionHandler = ResourceManager.ExceptionHandler;
            ResourceManager.ExceptionHandler = ResourceException;
            if (NativePreferences.ResourceLogs) Step("Documents resolver installed: " + resourceContract.pack_id + "; required=" + resourceContract.bundles.Length + "; available=" + bundles.Count);
            NativeInputCompatibility.Install();
        }
        catch (Exception ex) { fatal = ex.ToString(); Debug.LogException(ex); }
    }

    static string ResolveId(IResourceLocation location)
    {
        string id = location.InternalId.Replace('\\', '/');
        const string marker = "/StandaloneOSX/";
        int index = id.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return location.InternalId;
        string name = id.Substring(index + marker.Length);
        NativeResourceStore.Record record;
        if (!bundles.TryGetValue(name, out record))
            throw new FileNotFoundException("Resource not in active pack: " + name + "; available=" + bundles.Count, name);
        string path = NativeResourceStore.LocalPath(bundleRoot, name);
        long bytes = record.bytes;
        path = NativeSkin.Resolve(name, path, ref bytes);
        // Let the provider read the requested asset. Its actual load failure
        // invalidates acceptance; no extra stat/read pass on the hot path.
        var options = location.Data as AssetBundleRequestOptions;
        if (options != null)
        {
            options.Crc = 0;
            options.Hash = "";
            options.BundleSize = bytes;
            options.UseUnityWebRequestForLocalBundles = false;
        }
        return path;
    }

    static void ResourceException(AsyncOperationHandle handle, Exception error)
    {
        // Preserve Addressables' logging. Only bundle failures trigger the pack
        // recovery UI, not ordinary game/manager logs during scene transitions.
        string text = error == null ? "" : error.ToString();
        if (text.IndexOf(".bundle", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("AssetBundleProvider", StringComparison.Ordinal) >= 0) ResourceFailed(text);
        if (previousExceptionHandler != null) previousExceptionHandler(handle, error);
        else if (error != null) Debug.LogException(error);
    }
    static void ResourceFailed(string error)
    {
        NativeResourceStore.Invalidate(acceptedPath);
        if (gameOwnsScenes && instance && !instance.recovering)
            instance.StartCoroutine(instance.Recover(error));
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    public static void Boot()
    {
        if (booted) return;
        booted = true;
        var host = new GameObject("NativeCompatibilityBoundary");
        DontDestroyOnLoad(host);
        instance = host.AddComponent<NativeBootstrap>();
        host.AddComponent<NativeAutoPause>();
        host.AddComponent<NativeDisplaySettings>();
        host.AddComponent<NativeAnimatorCompatibility>();
        if (NativePreferences.StartupDetails) host.AddComponent<NativeDiagnosticOverlay>();
        if (NativePreferences.ResourceLogs) host.AddComponent<NativePerformanceSampler>();
    }

    IEnumerator Start()
    {
        yield return null;
        yield return null;
        if (fatal != null) { NativePreferences.Show(6, fatal); yield break; }
        if (NativePreferences.ShowNotice)
        {
            NativePreferences.Show(1);
            while (NativePreferences.Poll() != 1) yield return null;
            NativePreferences.Hide();
        }
        yield return WaitForResources(NativePreferences.RecheckResources);
        if (NativePreferences.ResourceLogs) Step("Original runtime callbacks restored; platform=" + (Platform.Current == null ? "missing" : Platform.Current.GetType().Name));
        var init = Addressables.InitializeAsync(false);
        float deadline = Time.realtimeSinceStartup + 120;
        while (!init.IsDone && Time.realtimeSinceStartup < deadline) yield return null;
        if (!init.IsDone || init.Status != AsyncOperationStatus.Succeeded)
        {
            fatal = "Addressables initialization failed or timed out";
            if (init.OperationException != null) Debug.LogException(init.OperationException);
            if (init.IsDone) Addressables.Release(init);
            yield return Recover(fatal);
            yield break;
        }
        Addressables.Release(init);
        try
        {
            var check = JsonConvert.DeserializeObject<JsonCallbackCheck>("{\"value\":17}");
            if (check == null || check.value != 17 || !check.callback)
                throw new Exception("JSON deserialization callback self-check failed");
            int count = 0;
            NativeMods.StartPlugins();
            gameOwnsScenes = true;
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                foreach (var loader in root.GetComponentsInChildren<AddressablesLoadScene>(true))
                {
                    if (loader.gameObject.activeInHierarchy) throw new Exception("Original Loader started before compatibility boundary");
                    loader.gameObject.SetActive(true);
                    count++;
                }
            if (count != 1) throw new Exception("Expected one original scene Loader, found " + count);
            if (NativePreferences.ResourceLogs) Step("Original Loader activated; original game owns subsequent scenes");
        }
        catch (Exception ex) { fatal = ex.ToString(); Debug.LogException(ex); NativePreferences.Show(6, fatal); yield break; }
        // No ongoing bootstrap coroutine when logging is off.
        if (NativePreferences.ResourceLogs)
            while (true)
            {
                yield return new WaitForSecondsRealtime(10);
                Step("scene=" + SceneManager.GetActiveScene().name + "; fps=" + NativePerformanceSampler.FramesPerSecond.ToString("F1")
                    + "; targetFPS=" + Application.targetFrameRate + "; render=" + Display.main.renderingWidth + "x" + Display.main.renderingHeight
                    + "; managedMB=" + GC.GetTotalMemory(false) / 1048576);
            }
    }

    static string ReadResourceMetadata(out string stamp, out NativeResourceStore.Index index)
    {
        stamp = null;
        index = null;
        try
        {
            string manifest = Path.Combine(packRoot, "pack.json");
            if (!File.Exists(manifest)) return "Resource pack not found: " + resourceContract.folder;
            string text = File.ReadAllText(manifest);
            var header = JsonUtility.FromJson<NativeResourceStore.Manifest>(text);
            string error = NativeResourceStore.CheckManifest(resourceContract, header);
            if (error != null) return error;
            string indexPath = Path.Combine(packRoot, "resource-index.json");
            string indexText = File.ReadAllText(indexPath);
            index = JsonUtility.FromJson<NativeResourceStore.Index>(indexText);
            NativeResourceStore.ValidatePack(resourceContract, header, index);
            stamp = NativeResourceStore.AcceptanceStamp(contract, text, indexText,
                File.GetLastWriteTimeUtc(manifest).Ticks, File.GetLastWriteTimeUtc(indexPath).Ticks,
                Directory.GetCreationTimeUtc(packRoot).Ticks);
            return null;
        }
        catch (Exception ex) { return "Cannot read resource metadata: " + ex.Message; }
    }

    static void SetBundles(NativeResourceStore.Index index)
    {
        bundles.Clear();
        foreach (var record in index.bundles) bundles.Add(record.bundle, record);
    }

    IEnumerator WaitForResources(bool force)
    {
        resourcesWaiting = true;
        retryResources = true;
        while (resourcesWaiting)
        {
            if (!retryResources)
            {
                if (NativePreferences.Poll() == 2) retryResources = true;
                yield return null;
                continue;
            }
            retryResources = false;
            string stamp;
            NativeResourceStore.Index index;
            string error = ReadResourceMetadata(out stamp, out index);
            bool cached = error == null && NativeResourceStore.IsAccepted(acceptedPath, stamp, force);
            if (error == null && !cached)
            {
                float started = Time.realtimeSinceStartup;
                bool preparing = false;
                for (int i = 0; i < index.bundles.Length; i++)
                {
                    error = NativeResourceStore.CheckFile(bundleRoot, index.bundles[i]);
                    if (error != null) break;
                    if ((i & 63) == 63)
                    {
                        if (!preparing && Time.realtimeSinceStartup - started > .5f)
                        { NativePreferences.Show(3); preparing = true; }
                        yield return null;
                    }
                }
            }
            if (error != null)
            {
                NativeResourceStore.Invalidate(acceptedPath);
                NativePreferences.Show(2, error);
                if (NativePreferences.ResourceLogs) Step("Waiting for resource pack: " + error);
                force = true;
                yield return null;
                continue;
            }
            // The provider also receives the selected pack's actual sizes.
            SetBundles(index);
            if (!cached) NativeResourceStore.Remember(acceptedPath, stamp);
            NativePreferences.ConsumeRecheck();
            resourcesWaiting = false;
            NativePreferences.Hide();
#if UNITY_IOS && !UNITY_EDITOR
            UnityEngine.iOS.Device.SetNoBackupFlag(packRoot);
#endif
            if (NativePreferences.ResourceLogs) Step("Documents resources ready; acceptedCache=" + cached + "; files=" + bundles.Count);
        }
    }

    IEnumerator Recover(string error)
    {
        recovering = true;
        NativeResourceStore.Invalidate(acceptedPath);
        NativePreferences.Show(4, error.Length > 900 ? error.Substring(0, 900) : error);
        while (true)
        {
            while (NativePreferences.Poll() != 2) yield return null;
            yield return WaitForResources(true);
            // A failed original game async operation cannot safely be replayed
            // from this boundary. Recheck in place, then ask for a fresh launch.
            NativePreferences.Show(5);
        }
    }
    void OnApplicationFocus(bool focused)
    {
        if (focused && resourcesWaiting) retryResources = true;
    }
    static void SceneLoaded(Scene scene, LoadSceneMode mode)
    { if (NativePreferences.ResourceLogs || NativePreferences.StartupDetails) Step("Scene loaded: " + scene.name); }
    static void SceneUnloaded(Scene scene)
    { if (NativePreferences.ResourceLogs || NativePreferences.StartupDetails) Step("Scene unloaded: " + scene.name); }
    static void Step(string message) { Debug.Log("NATIVE_BOOT: " + message); }
    static void Log(string message, string trace, LogType type)
    {
        bool error = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
        bool repairFailure = type == LogType.Warning && message.StartsWith("NATIVE_ANIMATOR:", StringComparison.Ordinal);
        bool modStatus = message.StartsWith("[NativeSkin]", StringComparison.Ordinal)
            || message.StartsWith("[NativeMods]", StringComparison.Ordinal);
        if (!error && !repairFailure && !modStatus && !NativePreferences.Diagnostics) return;
        if (NativePreferences.StartupDetails) NativeDiagnosticOverlay.Add(message);
        if (logBytes >= 4 * 1024 * 1024) return;
        string line = type + ": " + message + "\n" + (error ? trace + "\n" : "");
        try { File.AppendAllText(logPath, line); logBytes += line.Length * 2; } catch { }
    }
}

// Created only when requested. Disabled diagnostics have no IMGUI callback,
// sample clock, text layout, string formatting or preference polling per frame.
public sealed class NativeDiagnosticOverlay : MonoBehaviour
{
    static readonly Queue<string> lines = new Queue<string>();
    static string text = "";
    GUIStyle style;
    public static void Add(string message)
    {
        lines.Enqueue(message);
        while (lines.Count > 8) lines.Dequeue();
        text = string.Join("\n", lines.ToArray());
    }
    void OnGUI()
    {
        if (style == null) style = new GUIStyle(GUI.skin.label) { fontSize = Math.Max(14, Screen.height / 65), wordWrap = true };
        GUI.Label(new Rect(12, 12, Screen.width - 24, Screen.height * .4f), text, style);
    }
}
public sealed class NativePerformanceSampler : MonoBehaviour
{
    float elapsed;
    int frames;
    public static float FramesPerSecond { get; private set; }
    void LateUpdate()
    {
        elapsed += Time.unscaledDeltaTime;
        frames++;
        if (elapsed >= 1f) { FramesPerSecond = frames / elapsed; frames = 0; elapsed = 0f; }
    }
}
