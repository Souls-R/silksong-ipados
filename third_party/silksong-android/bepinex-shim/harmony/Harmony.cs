// HarmonyLib.Harmony, minus the patching.
//
// Every method here that would rewrite a method at runtime does nothing and
// says so once. That is not a stub left to be filled in: after IL2CPP there is
// no JIT to write a detour into and no method table to swap, so runtime
// patching is not a thing that can be made to work. The patches a plugin
// declares as attributes were already applied, statically, before il2cpp ran.
//
// PatchAll is therefore *correct* as a no-op -- the work it names has been
// done -- while Patch and Unpatch are genuinely lost, and are logged as such
// so that a mod behaving oddly can be traced to the line that explains it.

using System;
using System.Collections.Generic;
using System.Reflection;

namespace HarmonyLib
{
    public class Harmony
    {
        public static bool DEBUG = false;

        public string Id { get; private set; }

        public Harmony(string id)
        {
            Id = id;
            UnityEngine.Debug.Log("[Harmony] " + id + " ready (patches were applied when the game was built)");
        }

        public static Harmony CreateAndPatchAll(Type type, string harmonyInstanceId = null)
        {
            var harmony = new Harmony(harmonyInstanceId ?? "harmony-" + type.FullName);
            harmony.PatchAll(type);
            return harmony;
        }

        /// Already done, at build time. Deliberately silent: this is the call
        /// nearly every plugin makes, and a warning on it would be noise that
        /// says nothing is wrong.
        public void PatchAll() { }
        public void PatchAll(Type type) { }
        public void PatchAll(Assembly assembly) { }
        public void PatchAllUncategorized() { }
        public void PatchAllUncategorized(Type type) { }
        public void PatchAllUncategorized(Assembly assembly) { }
        public void PatchCategory(string category) { }
        public void PatchCategory(Assembly assembly, string category) { }

        public MethodInfo Patch(
            MethodBase original,
            HarmonyMethod prefix = null,
            HarmonyMethod postfix = null,
            HarmonyMethod transpiler = null,
            HarmonyMethod finalizer = null,
            HarmonyMethod ilmanipulator = null)
        {
            Lost("Harmony.Patch", original == null ? "(null)" : original.Name);
            return null;
        }

        public void Unpatch(MethodBase original, HarmonyPatchType type, string harmonyID = "*")
        {
            Lost("Harmony.Unpatch", original == null ? "(null)" : original.Name);
        }

        public void Unpatch(MethodBase original, MethodInfo patch)
        {
            Lost("Harmony.Unpatch", original == null ? "(null)" : original.Name);
        }

        public void UnpatchSelf() { Lost("Harmony.UnpatchSelf", Id); }
        public void UnpatchAll(string harmonyID = null) { Lost("Harmony.UnpatchAll", harmonyID ?? Id); }
        public static void UnpatchID(string harmonyID) { Lost("Harmony.UnpatchID", harmonyID); }

        public static IEnumerable<MethodBase> GetAllPatchedMethods() { return new MethodBase[0]; }
        public IEnumerable<MethodBase> GetPatchedMethods() { return new MethodBase[0]; }
        public static bool HasAnyPatches(string harmonyID) { return false; }

        public static string VersionInfo(out Version currentVersion)
        {
            currentVersion = new Version(2, 10, 0);
            return "silksong-android build-time weaver";
        }

        static readonly HashSet<string> Told = new HashSet<string>();

        /// Said once per call site, not once per call: a mod that patches in
        /// Update would otherwise fill the log at sixty lines a second.
        static void Lost(string what, string detail)
        {
            var key = what + ":" + detail;
            if (Told.Contains(key)) return;
            Told.Add(key);
            UnityEngine.Debug.LogWarning(
                "[Harmony] " + what + "(" + detail + ") does nothing on this port: " +
                "patches are applied when the game is built, not while it runs.");
        }
    }
}
