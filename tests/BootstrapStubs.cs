// Minimal host for exercising production bootstrap ordering without Unity.
// Rendering, engine JSON serialization and asset decoding require device tests.
using System;
using System.Collections;
using System.Text.Json;

namespace UnityEngine
{
    public enum RuntimeInitializeLoadType { BeforeSplashScreen, AfterSceneLoad }
    public class RuntimeInitializeOnLoadMethodAttribute : Attribute
    { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type) {} }
    public enum LogType { Error, Exception, Assert, Warning, Log }
    public class Object
    {
        public static implicit operator bool(Object value) => value != null;
        public static void DontDestroyOnLoad(Object value) {}
    }
    public class MonoBehaviour : Object { public void StartCoroutine(IEnumerator routine) {} }
    public class GameObject : Object
    {
        public GameObject(string name) {}
        public bool activeInHierarchy;
        public T AddComponent<T>() where T : new() => new T();
        public T[] GetComponentsInChildren<T>(bool inactive) => new T[0];
        public void SetActive(bool active) { activeInHierarchy = active; }
    }
    public static class Application
    {
        public static string persistentDataPath, temporaryCachePath, streamingAssetsPath;
        public static int targetFrameRate;
        public static event Action<string, string, LogType> logMessageReceived;
        public static void Emit(string message, LogType type) => logMessageReceived?.Invoke(message, "", type);
    }
    public static class Debug
    {
        public static void Log(string message) => Application.Emit(message, LogType.Log);
        public static void LogException(Exception error) => Application.Emit(error.ToString(), LogType.Exception);
    }
    public static class JsonUtility
    {
        public static T FromJson<T>(string text) => JsonSerializer.Deserialize<T>(text,
            new JsonSerializerOptions { IncludeFields = true });
    }
    public static class Time { public static float realtimeSinceStartup, unscaledDeltaTime; }
    public class WaitForSecondsRealtime { public WaitForSecondsRealtime(float seconds) {} }
    public class Display { public static Display main = new Display(); public int renderingWidth, renderingHeight; }
    public static class Screen { public static int width, height; }
    public class GUIStyle { public int fontSize; public bool wordWrap; public GUIStyle(GUIStyle other) {} }
    public class GUISkin { public GUIStyle label; }
    public struct Rect { public Rect(float x, float y, float width, float height) {} }
    public static class GUI { public static GUISkin skin; public static void Label(Rect rect, string text, GUIStyle style) {} }
}
namespace UnityEngine.SceneManagement
{
    public enum LoadSceneMode { Single }
    public struct Scene { public string name; public UnityEngine.GameObject[] GetRootGameObjects() => new UnityEngine.GameObject[0]; }
    public static class SceneManager
    {
        public static event Action<Scene, LoadSceneMode> sceneLoaded;
        public static event Action<Scene> sceneUnloaded;
        public static Scene GetActiveScene() => new Scene();
    }
}
namespace UnityEngine.ResourceManagement.AsyncOperations
{
    public enum AsyncOperationStatus { Succeeded }
    public struct AsyncOperationHandle { public bool IsDone; public AsyncOperationStatus Status; public Exception OperationException; }
}
namespace UnityEngine.ResourceManagement.ResourceLocations
{ public interface IResourceLocation { string InternalId { get; } object Data { get; } } }
namespace UnityEngine.ResourceManagement.ResourceProviders
{ public class AssetBundleRequestOptions { public uint Crc; public string Hash; public long BundleSize; public bool UseUnityWebRequestForLocalBundles; } }
namespace UnityEngine.ResourceManagement
{
    public class ResourceManager
    {
        public Func<ResourceLocations.IResourceLocation, string> InternalIdTransformFunc;
        public static Action<AsyncOperations.AsyncOperationHandle, Exception> ExceptionHandler;
    }
}
namespace UnityEngine.AddressableAssets
{
    public static class Addressables
    {
        public static ResourceManagement.ResourceManager ResourceManager = new ResourceManagement.ResourceManager();
        public static ResourceManagement.AsyncOperations.AsyncOperationHandle InitializeAsync(bool release) => default;
        public static void Release(ResourceManagement.AsyncOperations.AsyncOperationHandle handle) {}
    }
}
namespace Newtonsoft.Json
{
    public class JsonIgnoreAttribute : Attribute {}
    public static class JsonConvert { public static T DeserializeObject<T>(string text) => throw new NotSupportedException(); }
}
public static class NativePreferences
{
    public static bool ResourceLogs, StartupDetails, Diagnostics, RecheckResources;
    public static bool ShowNotice = true;
    public static void Initialize() {}
    public static void Show(int state, string error = null) {}
    public static int Poll() => 0;
    public static void Hide() {}
    public static void ConsumeRecheck() {}
}
public class NativeAnimatorCompatibility { public static void Install() {} }
public class NativeAutoPause {}
public class NativeDisplaySettings {}
public static class NativeInputCompatibility { public static void Install() {} }
public class Platform { public static Platform Current = new Platform(); }
public class AddressablesLoadScene { public UnityEngine.GameObject gameObject; }
