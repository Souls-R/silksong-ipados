// A way into a mod's settings on a device with no F1 key.
//
// BepInEx's configuration UIs are all opened with a keyboard shortcut, because
// on a PC there is always a keyboard. Here there usually is not, and without a
// way in, a settings mod that is installed and running is indistinguishable
// from one that is broken.
//
// So the way in is the sticks: click both (L3+R3), on the controller the
// player already has in their hands. A chord rather than a single button
// because every single button is already the game's.
//
// The contract on the other side is the one those UIs already publish for
// other mods: a plugin that draws settings exposes
// `public bool DisplayingWindow { get; set; }`, and toggling it opens and
// closes the window. ConfigurationManager is the plugin that defines it.
// Nothing here names that plugin, and no plugin has to know about this: if
// none of them has the property, this component is never created.

using System;
using System.Reflection;
using BepInEx.Configuration;

namespace BepInEx.Bootstrap
{
    internal class ModMenu : UnityEngine.MonoBehaviour
    {
        const string PROPERTY = "DisplayingWindow";

        object _plugin;
        PropertyInfo _window;
        ConfigEntry<KeyboardShortcut> _shortcut;
        ConfigEntry<float> _scale;
        Sticks _sticks;
        Enlarge _enlarge;

        // One reading of "the chord is held", so there is one edge.
        bool _wasHeld;
        bool _justOpened;

        /// <summary>
        /// Adds the trigger if some plugin can be opened, and does nothing at
        /// all otherwise -- including not creating the component, so a build
        /// with no settings UI polls nothing every frame.
        /// </summary>
        internal static void Install(UnityEngine.GameObject host)
        {
            foreach (var info in Chainloader.PluginInfos.Values)
            {
                var instance = info == null ? null : info.Instance;
                if (instance == null) continue;

                var property = instance.GetType().GetProperty(
                    PROPERTY, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || property.PropertyType != typeof(bool)) continue;
                if (!property.CanRead || !property.CanWrite) continue;

                var menu = host.AddComponent<ModMenu>();
                menu._plugin = instance;
                menu._window = property;
                menu._shortcut = Shortcut();
                menu._scale = Scale();
                menu._sticks = Sticks.Find();
                menu._enlarge = new Enlarge(instance);

                Logging.Logger.CreateLogSource("BepInEx").LogInfo(
                    info.Metadata.Name + " opens with " + menu._shortcut.Value +
                    (menu._sticks == null ? "" : ", or by clicking both sticks"));
                return;
            }
        }

        /// <summary>
        /// The chord, in the core config, so it can be changed without a
        /// rebuild -- and so the settings UI lists it, which means the way in
        /// can be rebound from inside the thing it opens.
        ///
        /// L3+R3 written as numbers because that is all the legacy input
        /// manager has: it exposes a pad as JoystickButtonN, numbered in the
        /// order Android reported the buttons.
        /// </summary>
        static ConfigEntry<KeyboardShortcut> Shortcut()
        {
            return ConfigFile.CoreConfig.Bind(
                "Mods", "Open settings",
                new KeyboardShortcut(UnityEngine.KeyCode.JoystickButton9, UnityEngine.KeyCode.JoystickButton8),
                new ConfigDescription(
                    "Opens and closes a mod's settings window. Click both sticks (L3+R3) on a " +
                    "controller, or set this to a key of your own -- F1, or any button. " +
                    "Set it to None to turn it off."));
        }

        /// <summary>
        /// How much bigger than a desktop this screen needs it.
        ///
        /// A settings UI written for a PC sizes itself in raw pixels -- a
        /// window capped at 650x560, text at eleven -- which on a 1080p panel
        /// held at arm's length is legible only in the sense that a bus
        /// timetable across the street is.
        /// </summary>
        static ConfigEntry<float> Scale()
        {
            return ConfigFile.CoreConfig.Bind(
                "Mods", "Settings scale", 2f,
                new ConfigDescription(
                    "How much larger to draw a mod's settings window than it would be on a " +
                    "desktop. Applies while the window is open; 1 is the size it was written for.",
                    new AcceptableValueRange<float>(1f, 4f)));
        }

        void Update()
        {
            var shortcut = _shortcut.Value;
            if (shortcut.MainKey == UnityEngine.KeyCode.None) { _wasHeld = false; return; }

            // One edge, from two readings of the same chord.
            //
            // They are asked whether the chord is HELD, and the edge is taken
            // here, once. Asking each for its own down-edge and opening on
            // either is what the first version did, and it toggled twice: `||`
            // skips the second reading exactly when the first has already
            // fired, so the second saw the chord go down a frame later and
            // closed what had just opened.
            var held = shortcut.IsPressed() || (_sticks != null && _sticks.Down());
            if (held && !_wasHeld)
            {
                var opening = !Open;
                Open = opening;
                // The plugin recalculates its layout as it opens, so the
                // resizing has to come after that, and it cannot happen here:
                // the skin it draws with only exists during OnGUI.
                _justOpened = opening;
            }
            _wasHeld = held;
        }

        /// <summary>
        /// Draws nothing. It is here because a GUI skin can only be touched
        /// from inside OnGUI, and only on a layout event: changing a style
        /// between the layout pass and the repaint that follows it is how
        /// IMGUI gets told the two disagree.
        /// </summary>
        void OnGUI()
        {
            if (UnityEngine.Event.current.type != UnityEngine.EventType.Layout) return;

            if (!Open)
            {
                _enlarge.Restore();
                return;
            }

            _enlarge.Apply(_scale.Value, _justOpened);
            _justOpened = false;
        }

        bool Open
        {
            get
            {
                try { return (bool)_window.GetValue(_plugin, null); }
                catch (Exception) { return false; }
            }
            set
            {
                try { _window.SetValue(_plugin, value, null); }
                catch (Exception e) { UnityEngine.Debug.LogWarning("[BepInEx] Could not open the settings: " + e); }
            }
        }
    }

    /// <summary>
    /// Both stick buttons, read through the input system package by name.
    ///
    /// The legacy manager numbers a pad's buttons in whatever order Android
    /// reported them, so JoystickButton8 is L3 on the pads this was tried with
    /// and need not be on the next one. The input system package names them
    /// instead, and this game already ships it.
    ///
    /// By reflection, so this assembly does not acquire a reference to a
    /// package that a game built the other way would not have.
    /// </summary>
    internal sealed class Sticks
    {
        PropertyInfo _current, _left, _right, _pressed;

        internal static Sticks Find()
        {
            try
            {
                var gamepad = Type.GetType("UnityEngine.InputSystem.Gamepad, Unity.InputSystem");
                if (gamepad == null) return null;

                var sticks = new Sticks
                {
                    _current = gamepad.GetProperty("current", BindingFlags.Public | BindingFlags.Static),
                    _left = gamepad.GetProperty("leftStickButton", BindingFlags.Public | BindingFlags.Instance),
                    _right = gamepad.GetProperty("rightStickButton", BindingFlags.Public | BindingFlags.Instance),
                };
                return sticks._current == null || sticks._left == null || sticks._right == null ? null : sticks;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>True while both stick buttons are down.</summary>
        internal bool Down()
        {
            if (_current == null) return false;
            try
            {
                var pad = _current.GetValue(null, null);
                if (pad == null) return false;
                return IsPressed(_left.GetValue(pad, null)) && IsPressed(_right.GetValue(pad, null));
            }
            catch (Exception e)
            {
                // One failure is every failure: whatever is wrong with the
                // reflection is wrong on every frame, and a warning per frame
                // is worse than the missing chord.
                _current = null;
                UnityEngine.Debug.LogWarning("[BepInEx] Could not read the sticks: " + e.Message);
                return false;
            }
        }

        bool IsPressed(object button)
        {
            if (button == null) return false;
            if (_pressed == null)
            {
                _pressed = button.GetType().GetProperty("isPressed", BindingFlags.Public | BindingFlags.Instance);
                if (_pressed == null) return false;
            }
            return (bool)_pressed.GetValue(button, null);
        }
    }

    /// <summary>
    /// Makes a settings window written for a monitor usable on a handheld.
    ///
    /// Four things are wrong with it at this size, and each needs its own
    /// lever. All of them are on the ambient skin or on the plugin's own
    /// members, found by name; a plugin that has none of them is drawn at
    /// whatever size it chose.
    ///
    /// The text is eleven pixels, because that is IMGUI's default and the
    /// window does not override it. That one is the skin, which the window
    /// reads rather than replaces, so raising the sizes there raises
    /// everything it draws. Everything is put back when the window closes:
    /// the skin is the whole game's, not this window's.
    ///
    /// The headers are not, because a window like this caches its own copy of
    /// the skin's label at a fixed size -- and only builds that copy when it
    /// is missing, which is why it is enough to raise the size on whatever
    /// copy it is holding. Left alone the headings come out smaller than the
    /// text under them.
    ///
    /// The window will not stay where it is put. It is drawn with
    /// GUILayout.Window, which sizes a window to fit its CONTENTS and returns
    /// that size, and the plugin stores what it returns -- so a rect set from
    /// outside survives exactly one frame before collapsing back onto the
    /// widest row, which with every plugin collapsed is the row of buttons
    /// along the top. Setting the rect is therefore not enough and never can
    /// be: what holds it open is a fixedWidth and fixedHeight on the skin's
    /// window style, which the layout system treats as both the minimum and
    /// the maximum.
    ///
    /// The position has the same problem and the same shape of answer. It is
    /// also read back from the layout every frame, so any nudge -- a phantom
    /// pointer, a clamp, rounding -- is stored and applied again next frame,
    /// and the window walks off the edge of the screen where a handheld has no
    /// cursor to drag it back with. So it is written every frame instead of
    /// once: the window is a fixed panel in the middle of the screen, which is
    /// what it should have been on a device with no mouse anyway.
    ///
    /// The scrollbar is fifteen pixels wide, which is a fine target for a
    /// mouse and no target at all for a thumb. Scrollbar widths are fixed
    /// sizes on their styles rather than font metrics, so they do not follow
    /// the text and are scaled separately.
    /// </summary>
    internal sealed class Enlarge
    {
        /// IMGUI's own default, and the size the window was written against.
        const int Base = 11;

        readonly object _plugin;
        readonly PropertyInfo _rect, _leftColumn, _rightColumn;
        readonly FieldInfo[] _cachedStyles;

        int _applied;

        // Where the window belongs, and the screen it was worked out for.
        UnityEngine.Rect _target;
        int _screenWidth, _screenHeight;

        // The skin as the game had it, so it can be handed back untouched.
        UnityEngine.GUIStyle[] _styles;
        UnityEngine.GUIStyle _windowStyle;
        int[] _font;
        float[] _width, _height;
        int _bars;

        internal Enlarge(object plugin)
        {
            _plugin = plugin;
            var type = plugin.GetType();
            _rect = Property(type, "SettingWindowRect", typeof(UnityEngine.Rect));
            _leftColumn = Property(type, "LeftColumnWidth", typeof(int));
            _rightColumn = Property(type, "RightColumnWidth", typeof(int));

            // Static GUIStyles anywhere in the plugin's assembly: that is
            // where a cached header style lives, and there are only ever a
            // handful of them.
            var found = new System.Collections.Generic.List<FieldInfo>();
            try
            {
                foreach (var t in type.Assembly.GetTypes())
                    foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                        if (f.FieldType == typeof(UnityEngine.GUIStyle)) found.Add(f);
            }
            catch (Exception)
            {
                // A plugin whose types cannot all be walked still gets the
                // skin treatment, which is most of the benefit.
            }
            _cachedStyles = found.ToArray();
        }

        static PropertyInfo Property(Type type, string name, Type of)
        {
            var found = type.GetProperty(
                name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return found != null && found.PropertyType == of && found.CanRead && found.CanWrite ? found : null;
        }

        internal void Apply(float scale, bool opening)
        {
            scale = UnityEngine.Mathf.Clamp(scale, 1f, 4f);
            var size = UnityEngine.Mathf.RoundToInt(Base * scale);

            if (_applied != size)
            {
                Remember();
                for (var i = 0; i < _styles.Length; i++) _styles[i].fontSize = size;

                // Bars, from the index the scrollbar styles start at. Both
                // dimensions on each: a vertical bar is held by its width, a
                // horizontal one by its height, and the little end buttons by
                // both.
                for (var i = _bars; i < _styles.Length; i++)
                {
                    if (_width[i] > 0) _styles[i].fixedWidth = UnityEngine.Mathf.Round(_width[i] * scale);
                    if (_height[i] > 0) _styles[i].fixedHeight = UnityEngine.Mathf.Round(_height[i] * scale);
                }

                _applied = size;
                // The window's own measurements are in the old size until it
                // lays out again, so a change of scale resizes it too.
                opening = true;
            }

            // Whatever copy of the label the plugin is holding NOW: it makes a
            // fresh one, at its own fixed size, every time the window opens.
            foreach (var field in _cachedStyles)
            {
                try
                {
                    var style = field.GetValue(null) as UnityEngine.GUIStyle;
                    if (style == null || style.fontSize <= 0 || style.fontSize >= size) continue;
                    style.fontSize = UnityEngine.Mathf.RoundToInt(style.fontSize * scale);
                }
                catch (Exception)
                {
                }
            }

            if (opening ||
                _screenWidth != UnityEngine.Screen.width ||
                _screenHeight != UnityEngine.Screen.height)
            {
                Resize(scale);
            }
            else
            {
                Pin();
            }
        }

        /// <summary>
        /// Puts the window back where it was put.
        ///
        /// The position is not ours to set once and forget: the plugin stores
        /// whatever the layout system hands back every frame, so anything that
        /// nudges the window -- a phantom pointer, a clamp, the layout's own
        /// rounding -- is written into the rect and is there again next frame,
        /// a little further along. Left alone it walks off the side of the
        /// screen, and on a handheld there is no cursor to drag it back with.
        /// </summary>
        void Pin()
        {
            if (_rect == null || _target.width <= 0) return;
            try
            {
                if ((UnityEngine.Rect)_rect.GetValue(_plugin, null) != _target)
                    _rect.SetValue(_plugin, _target, null);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Puts the game's skin back the way the game had it.</summary>
        internal void Restore()
        {
            if (_applied == 0 || _styles == null) return;
            for (var i = 0; i < _styles.Length; i++)
            {
                _styles[i].fontSize = _font[i];
                _styles[i].fixedWidth = _width[i];
                _styles[i].fixedHeight = _height[i];
            }
            _applied = 0;
        }

        void Remember()
        {
            if (_styles != null) return;
            var skin = UnityEngine.GUI.skin;

            // Order matters: everything from _bars on is a scrollbar part,
            // and gets its fixed size scaled as well as its font.
            var styles = new System.Collections.Generic.List<UnityEngine.GUIStyle>
            {
                skin.label, skin.button, skin.box, skin.toggle, skin.textField, skin.textArea,
                skin.window, skin.horizontalSlider, skin.horizontalSliderThumb,
            };
            _windowStyle = skin.window;
            _bars = styles.Count;
            styles.AddRange(new[]
            {
                skin.verticalScrollbar, skin.verticalScrollbarThumb,
                skin.verticalScrollbarUpButton, skin.verticalScrollbarDownButton,
                skin.horizontalScrollbar, skin.horizontalScrollbarThumb,
                skin.horizontalScrollbarLeftButton, skin.horizontalScrollbarRightButton,
            });

            _styles = styles.ToArray();
            _font = new int[_styles.Length];
            _width = new float[_styles.Length];
            _height = new float[_styles.Length];
            for (var i = 0; i < _styles.Length; i++)
            {
                _font[i] = _styles[i].fontSize;
                _width[i] = _styles[i].fixedWidth;
                _height[i] = _styles[i].fixedHeight;
            }
        }

        void Resize(float scale)
        {
            try
            {
                // From the size the window asked for, not from the size it
                // currently is: by the time this runs a second time the window
                // has been through a layout, and scaling what came out of that
                // would compound.
                var wide = UnityEngine.Mathf.Min(650f * scale, UnityEngine.Screen.width * 0.98f);
                var tall = UnityEngine.Mathf.Min(560f * scale, UnityEngine.Screen.height * 0.98f);

                // What actually holds the window open. Without this the layout
                // system shrinks it back onto its contents on the very next
                // frame, however large a rect it was handed.
                if (_windowStyle != null)
                {
                    _windowStyle.fixedWidth = UnityEngine.Mathf.Round(wide);
                    _windowStyle.fixedHeight = UnityEngine.Mathf.Round(tall);
                }

                if (_rect == null) return;
                var grown = new UnityEngine.Rect(
                    UnityEngine.Mathf.Round((UnityEngine.Screen.width - wide) / 2f),
                    UnityEngine.Mathf.Round((UnityEngine.Screen.height - tall) / 2f),
                    UnityEngine.Mathf.Round(wide), UnityEngine.Mathf.Round(tall));
                _rect.SetValue(_plugin, grown, null);

                // Remembered so that Pin can put it back, and against the
                // screen it was centred on: a resolution change makes it wrong
                // rather than merely stale.
                _target = grown;
                _screenWidth = UnityEngine.Screen.width;
                _screenHeight = UnityEngine.Screen.height;

                // The columns are derived from the width and do not follow it
                // by themselves. The proportions are the window's own.
                if (_leftColumn != null && _rightColumn != null)
                {
                    var left = UnityEngine.Mathf.RoundToInt(grown.width / 2.5f);
                    _leftColumn.SetValue(_plugin, left, null);
                    _rightColumn.SetValue(
                        _plugin,
                        UnityEngine.Mathf.RoundToInt(grown.width - left - 115 * scale),
                        null);
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[BepInEx] Could not resize the settings window: " + e.Message);
            }
        }
    }
}
