using System.IO;
using System.Reflection;

namespace BepInEx
{
    public static class Paths
    {
        public static string BepInExRootPath
        {
            get
            {
                // This shim is compiled before NativeCompatibility; use the
                // same container rule without a circular assembly reference.
                string documents = UnityEngine.Application.persistentDataPath;
                string combined = Path.Combine(documents, "SilksongMods");
                return Path.Combine(Directory.Exists(combined) ? combined : documents, "Mods");
            }
        }
        public static string PluginPath { get { return Path.Combine(BepInExRootPath, "plugins"); } }
        public static string ConfigPath { get { return Path.Combine(BepInExRootPath, "config"); } }
        public static string CachePath { get { return Path.Combine(BepInExRootPath, "cache"); } }
        public static string PatcherPluginPath { get { return Path.Combine(BepInExRootPath, "patchers"); } }
        public static string BepInExAssemblyDirectory { get { return BepInExRootPath; } }
        public static string GameRootPath { get { return UnityEngine.Application.dataPath; } }
        public static string ProcessName { get { return UnityEngine.Application.productName; } }
        public static string ExecutablePath { get { return UnityEngine.Application.dataPath; } }
        public static string ManagedPath { get { return Path.Combine(GameRootPath, "Managed"); } }

        // AOT assemblies have no DLL location on disk. Keep a stable sidecar
        // directory for plugins which locate bundles beside their own DLL.
        public static string GetAssemblyLocation(Assembly assembly)
        {
            string name = assembly.GetName().Name;
            return Path.Combine(PluginPath, name, name + ".dll");
        }
    }
}
