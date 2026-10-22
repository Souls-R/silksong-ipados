// The base class every plugin derives from.
//
// A MonoBehaviour, as on a PC, so Awake/Update/OnGUI work unchanged. What is
// different is who constructs it: there is no assembly scan at startup here,
// because the plugin was already compiled into the game. Chainloader finds the
// type in the loaded assemblies and adds it as a component.

using System;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace BepInEx
{
    public abstract class BaseUnityPlugin : UnityEngine.MonoBehaviour
    {
        /// <summary>Name, GUID and version, from this plugin's [BepInPlugin].</summary>
        public PluginInfo Info { get; private set; }

        /// <summary>Writes to logcat, tagged with the plugin name.</summary>
        protected ManualLogSource Logger { get; private set; }

        /// <summary>This plugin's .cfg, under mods/config.</summary>
        public ConfigFile Config { get; private set; }

        protected BaseUnityPlugin()
        {
            var type = GetType();
            var metadata = (BepInPlugin)Attribute.GetCustomAttribute(type, typeof(BepInPlugin));
            if (metadata == null) metadata = new BepInPlugin(type.FullName, type.Name, "0.0.0");

            Info = new PluginInfo(metadata, Paths.PluginPath);
            Info.Instance = this;
            Logger = BepInEx.Logging.Logger.CreateLogSource(metadata.Name);

            var path = Path.Combine(Paths.ConfigPath, metadata.GUID + ".cfg");
            Config = new ConfigFile(path, true, metadata);
        }
    }
}
