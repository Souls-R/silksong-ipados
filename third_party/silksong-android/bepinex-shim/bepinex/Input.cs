// Input, as BepInEx presents it to a plugin.
//
// A plugin does not call UnityEngine.Input directly. BepInEx 5.4.20 put an
// interface in front of it, because a player built against the new Input
// System package throws from every legacy call, and a mod that binds a hotkey
// should not be the thing that discovers this. Plugins compiled since then --
// ConfigurationManager among them -- reference `UnityInput.Current` by name,
// so it has to exist here with the same shape or they cannot be converted.
//
// This port's player has the legacy manager: the touch handling in
// SilksongPatches reads Input.touchCount and Input.mousePosition every frame.
// The fallback below is therefore not expected to run; it exists so that a
// player built the other way degrades to "no hotkeys" instead of an exception
// on the first Update of every plugin that has one.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BepInEx
{
    /// <summary>The subset of UnityEngine.Input a plugin is given.</summary>
    public interface IInputSystem
    {
        UnityEngine.Vector3 mousePosition { get; }
        UnityEngine.Vector2 mouseScrollDelta { get; }
        bool mousePresent { get; }
        bool anyKey { get; }
        bool anyKeyDown { get; }

        /// <summary>Every key this backend can report, for a keybind picker.</summary>
        IEnumerable<UnityEngine.KeyCode> SupportedKeyCodes { get; }

        bool GetKey(string name);
        bool GetKey(UnityEngine.KeyCode key);
        bool GetKeyDown(string name);
        bool GetKeyDown(UnityEngine.KeyCode key);
        bool GetKeyUp(string name);
        bool GetKeyUp(UnityEngine.KeyCode key);
        bool GetMouseButton(int button);
        bool GetMouseButtonDown(int button);
        bool GetMouseButtonUp(int button);
        void ResetInputAxes();
    }

    public static class UnityInput
    {
        static IInputSystem _current;

        /// <summary>
        /// The backend to poll. Legacy where it works, and a backend that
        /// reports nothing where it does not.
        /// </summary>
        public static IInputSystem Current
        {
            get
            {
                if (_current == null)
                {
                    if (LegacyInputSystemAvailable)
                    {
                        _current = new LegacyInputSystem();
                    }
                    else
                    {
                        UnityEngine.Debug.LogWarning(
                            "[BepInEx] This player has no legacy input backend; hotkeys are unavailable.");
                        _current = new NoInputSystem();
                    }
                }
                return _current;
            }
        }

        /// <summary>
        /// Whether UnityEngine.Input answers at all.
        ///
        /// Reading a property is the test, because the exception a player with
        /// the legacy manager switched off throws is thrown by every member,
        /// and mousePosition has no side effect.
        /// </summary>
        public static bool LegacyInputSystemAvailable
        {
            get
            {
                try
                {
                    var unused = UnityEngine.Input.mousePosition;
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }
    }

    internal class LegacyInputSystem : IInputSystem
    {
        public UnityEngine.Vector3 mousePosition { get { return UnityEngine.Input.mousePosition; } }
        public UnityEngine.Vector2 mouseScrollDelta { get { return UnityEngine.Input.mouseScrollDelta; } }
        public bool mousePresent { get { return UnityEngine.Input.mousePresent; } }
        public bool anyKey { get { return UnityEngine.Input.anyKey; } }
        public bool anyKeyDown { get { return UnityEngine.Input.anyKeyDown; } }

        static UnityEngine.KeyCode[] _keys;

        public IEnumerable<UnityEngine.KeyCode> SupportedKeyCodes
        {
            get
            {
                // Every value once: KeyCode has aliases that share a number,
                // and a picker that offered both would show the same key twice.
                if (_keys == null)
                    _keys = Enum.GetValues(typeof(UnityEngine.KeyCode))
                        .Cast<UnityEngine.KeyCode>()
                        .Distinct()
                        .ToArray();
                return _keys;
            }
        }

        public bool GetKey(string name) { return UnityEngine.Input.GetKey(name); }
        public bool GetKey(UnityEngine.KeyCode key) { return UnityEngine.Input.GetKey(key); }
        public bool GetKeyDown(string name) { return UnityEngine.Input.GetKeyDown(name); }
        public bool GetKeyDown(UnityEngine.KeyCode key) { return UnityEngine.Input.GetKeyDown(key); }
        public bool GetKeyUp(string name) { return UnityEngine.Input.GetKeyUp(name); }
        public bool GetKeyUp(UnityEngine.KeyCode key) { return UnityEngine.Input.GetKeyUp(key); }
        public bool GetMouseButton(int button) { return UnityEngine.Input.GetMouseButton(button); }
        public bool GetMouseButtonDown(int button) { return UnityEngine.Input.GetMouseButtonDown(button); }
        public bool GetMouseButtonUp(int button) { return UnityEngine.Input.GetMouseButtonUp(button); }
        public void ResetInputAxes() { UnityEngine.Input.ResetInputAxes(); }
    }

    /// <summary>Answers everything with "no". Used when there is no backend.</summary>
    internal class NoInputSystem : IInputSystem
    {
        public UnityEngine.Vector3 mousePosition { get { return UnityEngine.Vector3.zero; } }
        public UnityEngine.Vector2 mouseScrollDelta { get { return UnityEngine.Vector2.zero; } }
        public bool mousePresent { get { return false; } }
        public bool anyKey { get { return false; } }
        public bool anyKeyDown { get { return false; } }
        public IEnumerable<UnityEngine.KeyCode> SupportedKeyCodes { get { return new UnityEngine.KeyCode[0]; } }

        public bool GetKey(string name) { return false; }
        public bool GetKey(UnityEngine.KeyCode key) { return false; }
        public bool GetKeyDown(string name) { return false; }
        public bool GetKeyDown(UnityEngine.KeyCode key) { return false; }
        public bool GetKeyUp(string name) { return false; }
        public bool GetKeyUp(UnityEngine.KeyCode key) { return false; }
        public bool GetMouseButton(int button) { return false; }
        public bool GetMouseButtonDown(int button) { return false; }
        public bool GetMouseButtonUp(int button) { return false; }
        public void ResetInputAxes() { }
    }
}
