using System;
using System.Reflection;
using System.Runtime.InteropServices;
using InControl;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.iOS;
using UnityEngine.InputSystem.Layouts;
using InputManager = InControl.InputManager;
using InputDeviceMatcher = UnityEngine.InputSystem.Layouts.InputDeviceMatcher;

public static class NativeInputCompatibility
{
    static NativeKeyboardProvider keyboard;
    static NativeMouseProvider mouse;

    public static void Install()
    {
        // Same layouts and matchers as Unity Input System 1.14.2's iOS plugin.
        // The original Mac assembly contains the game adapters but omits these.
        InputSystem.RegisterLayout(typeof(iOSGameController), "iOSGameController", matches: Matcher());
        InputSystem.RegisterLayout(typeof(XboxOneGampadiOS), "XboxOneGampadiOS", matches: Matcher().WithProduct("Xbox Wireless Controller"));
        InputSystem.RegisterLayout(typeof(DualShock4GampadiOS), "DualShock4GampadiOS", matches: Matcher().WithProduct("DUALSHOCK 4 Wireless Controller"));
        InputSystem.RegisterLayout(typeof(DualSenseGampadiOS), "DualSenseGampadiOS", matches: Matcher().WithProduct("DualSense Wireless Controller"));
        InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsInDynamicUpdate;
        NativeControllerInput.Install();
        NativeControllerHaptics.Install();
        InputSystem.onDeviceChange += DeviceChanged;
        foreach (var device in InputSystem.devices) Describe(device, "present");
        InputManager.OnSetupCompleted += SetupKeyboard;
        InputManager.OnSetupCompleted += SetupMouse;
        if (InputManager.IsSetup) SetupKeyboard();
        if (InputManager.IsSetup) SetupMouse();
        if (NativePreferences.CompatibilityLogs) Debug.Log("NATIVE_INPUT: iOS gamepad layouts registered; dynamic input update");
    }

    static InputDeviceMatcher Matcher() { return new InputDeviceMatcher().WithInterface("iOS").WithDeviceClass("iOSGameController"); }
    static void DeviceChanged(UnityEngine.InputSystem.InputDevice device, InputDeviceChange change)
    {
        if (change == InputDeviceChange.Added || change == InputDeviceChange.Removed || change == InputDeviceChange.Reconnected)
            Describe(device, change.ToString());
    }
    static void Describe(UnityEngine.InputSystem.InputDevice device, string state)
    {
        if (NativePreferences.CompatibilityLogs) Debug.Log("NATIVE_INPUT: " + state + "; layout=" + device.layout + "; product=" + device.description.product + "; interface=" + device.description.interfaceName + "; nativeStyle=" + NativeControllerIdentity.DetectStyle(device));
    }
    static void SetupKeyboard()
    {
        if (ReferenceEquals(InputManager.KeyboardProvider, keyboard) && keyboard != null) return;
        try
        {
            keyboard = new NativeKeyboardProvider(InputManager.KeyboardProvider);
            // The original InControl API exposes only the getter. Change this
            // single provider reference once, retaining the original actions,
            // bindings, labels, input tick and controller manager.
            typeof(InputManager).GetProperty("KeyboardProvider", BindingFlags.Public | BindingFlags.Static).SetValue(null, keyboard, null);
            if (!ReferenceEquals(InputManager.KeyboardProvider, keyboard)) throw new Exception("Keyboard provider was not installed");
            keyboard.Setup();
            if (NativePreferences.CompatibilityLogs) Debug.Log("NATIVE_INPUT: Hardware keyboard state provider installed");
        }
        catch (Exception ex) { Debug.LogError("NATIVE_INPUT: Keyboard provider setup failed: " + ex); }
    }

    static void SetupMouse()
    {
        if (mouse != null && ReferenceEquals(InputManager.MouseProvider, mouse)) return;
        try
        {
            mouse = new NativeMouseProvider(InputManager.MouseProvider);
            typeof(InputManager).GetProperty("MouseProvider", BindingFlags.Public | BindingFlags.Static).SetValue(null, mouse, null);
            if (!ReferenceEquals(InputManager.MouseProvider, mouse)) throw new Exception("Mouse provider was not installed");
            mouse.Setup();
            if (NativePreferences.CompatibilityLogs) Debug.Log("NATIVE_INPUT: Hardware mouse button provider installed");
        }
        catch (Exception ex) { Debug.LogError("NATIVE_INPUT: Mouse provider setup failed: " + ex); }
    }

    sealed class NativeKeyboardProvider : IKeyboardProvider
    {
        readonly IKeyboardProvider fallback;
        readonly byte[] state = new byte[256];
        bool connected;
        bool reported;
        [DllImport("__Internal")] static extern int NativeKeyboard_CopyState(IntPtr destination, int length);
        public NativeKeyboardProvider(IKeyboardProvider previous) { fallback = previous; }
        public void Setup() { Update(); }
        public void Reset() { Array.Clear(state, 0, state.Length); connected = false; if (fallback != null) fallback.Reset(); }
        public unsafe void Update()
        {
            bool present;
            fixed (byte* destination = state) present = NativeKeyboard_CopyState((IntPtr)destination, state.Length) != 0;
            if (!reported || present != connected)
            {
                if (NativePreferences.CompatibilityLogs) Debug.Log("NATIVE_INPUT: GCKeyboard connected=" + present);
                reported = true;
            }
            connected = present;
            if (!connected && fallback != null) fallback.Update();
        }
        public bool AnyKeyIsPressed()
        {
            if (!connected) return Application.isFocused && fallback != null && fallback.AnyKeyIsPressed();
            for (int i = 4; i < state.Length; i++) if (state[i] != 0) return true;
            return false;
        }
        public bool GetKeyIsPressed(InControl.Key control)
        {
            return connected ? NativeKeyState.IsPressed(state, (int)control) : Application.isFocused && fallback != null && fallback.GetKeyIsPressed(control);
        }
        public string GetNameForKey(InControl.Key control) { return fallback == null ? control.ToString() : fallback.GetNameForKey(control); }
    }
}
