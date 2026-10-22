using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;
using InputDevice = UnityEngine.InputSystem.InputDevice;

// A partial IGC snapshot: only its first uint, preserving all existing bits.
// InputState.Change copies raw state; it cannot write a bitfield control/value.
internal struct NativeControllerButtons : IInputStateTypeInfo
{
    public uint buttons;
    public FourCC format { get { return new FourCC('I', 'G', 'C', ' '); } }
}

// Keep the original InControl controls and inventory binding. Unity's iOS
// state has no touchpad button, so fill just that control after native events.
public static class NativeControllerInput
{
    static bool installed;
    static readonly Dictionary<int, int> reported = new Dictionary<int, int>();
    internal static Func<int, string, int> ReadTouchpad = NativeController_ReadTouchpad;

    [DllImport("__Internal")] static extern int NativeController_ReadTouchpad(int deviceId, string product);

    public static void Install()
    {
        if (installed) return;
        installed = true;
        // IGC uses bits 0..15 of its first uint. Reserve the next bit, rather
        // than relying on automatic placement of the inherited PS control.
        foreach (string layout in new[] { "DualShock4GampadiOS", "DualSenseGampadiOS" })
            InputSystem.RegisterLayoutOverride("{\"name\":\"NativeTouchpad" + layout +
                "\",\"extend\":\"" + layout + "\",\"controls\":[{\"name\":\"touchpadButton\"," +
                "\"layout\":\"Button\",\"offset\":0,\"bit\":16,\"format\":\"BIT\"}]}");
        InputSystem.onAfterUpdate += AfterUpdate;
        InputSystem.onDeviceChange += Changed;
        Application.focusChanged += FocusChanged;
    }

    static bool Supported(Gamepad pad)
    {
        return pad is DualShockGamepad && pad.description.interfaceName == "iOS";
    }

    static void Changed(InputDevice device, InputDeviceChange change)
    {
        if (change == InputDeviceChange.Removed || change == InputDeviceChange.Disconnected ||
            change == InputDeviceChange.Disabled || change == InputDeviceChange.Reconnected)
            reported.Remove(device.deviceId);
    }

    static void FocusChanged(bool focused)
    {
        if (focused) return;
        foreach (var pad in Gamepad.all)
            if (Supported(pad)) Set((DualShockGamepad)pad, 0);
        reported.Clear();
    }

    static void Set(DualShockGamepad pad, int state)
    {
        if (pad.touchpadButton == null) return;
        NativeControllerButtons snapshot;
        pad.CopyState(out snapshot);
        const uint touchpad = 1u << 16;
        uint buttons = state > 0 ? snapshot.buttons | touchpad : snapshot.buttons & ~touchpad;
        if (buttons == snapshot.buttons) return;
        snapshot.buttons = buttons;
        InputState.Change(pad, snapshot);
    }

    static void AfterUpdate()
    {
        if (InputState.currentUpdateType != InputUpdateType.Dynamic) return;
        foreach (var pad in Gamepad.all)
        {
            if (!Supported(pad)) continue;
            int state = Application.isFocused
                ? ReadTouchpad(pad.deviceId, pad.description.product ?? "") : -2;
            // Unavailable/ambiguous native devices must release a previous press.
            Set((DualShockGamepad)pad, state);
            int previous;
            if (!reported.TryGetValue(pad.deviceId, out previous) || previous != state)
            {
                reported[pad.deviceId] = state;
                if (NativePreferences.CompatibilityLogs)
                    Debug.Log("NATIVE_CONTROLLER: product=" + pad.description.product +
                        "; id=" + pad.deviceId + "; touchpad=" + state +
                        "; unityPressed=" + ((DualShockGamepad)pad).touchpadButton.isPressed +
                        " (1=pressed, 0=released, -1=unavailable/ambiguous, -2=inactive)");
            }
        }
    }
}
