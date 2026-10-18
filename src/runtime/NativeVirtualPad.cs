using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.LowLevel;
using InputDevice = UnityEngine.InputSystem.InputDevice;

// The on-screen pad is a gamepad, never a keyboard. A synthetic iOS gamepad is
// fed the native snapshot, so InControl and gameplay bindings see a controller
// that behaves exactly like a connected one. The native overlay owns the display
// mode and visibility; this driver only mirrors its snapshot.
public sealed class NativeVirtualPad : MonoBehaviour
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Snapshot
    {
        public int state, buttons;
        public float leftX, leftY;
    }
    // One bit per control. These values mirror NativeVirtualPad.h; a build test
    // fails if the two tables ever drift apart.
    const int DpadUp = 1 << 0, DpadDown = 1 << 1, DpadLeft = 1 << 2, DpadRight = 1 << 3;
    const int South = 1 << 4, East = 1 << 5, West = 1 << 6, North = 1 << 7;
    const int LeftShoulder = 1 << 8, RightShoulder = 1 << 9;
    const int LeftTrigger = 1 << 10, RightTrigger = 1 << 11;
    const int Select = 1 << 12, Start = 1 << 13;

    readonly Func<Snapshot> readState = ReadNative;
    InputDevice device;
    int buttons;
    Vector2 stick;

    void Update()
    {
        Snapshot snapshot = readState();
        // -1: the display mode is off; 0: the overlay is hidden. Either way the
        // game must not see a virtual gamepad at all.
        if (snapshot.state <= 0) { Remove(); return; }
        // A real controller, keyboard or mouse took over; the overlay hides
        // itself and the next frame drops the virtual gamepad.
        YieldToRealDevice();
        if (device == null)
        {
            device = InputSystem.AddDevice(new InputDeviceDescription {
                interfaceName = "iOS", deviceClass = "iOSGameController",
                product = "Virtual Pad", manufacturer = "Silksong iPadOS" });
            buttons = 0;
            stick = Vector2.zero;
            if (NativePreferences.CompatibilityLogs) Debug.Log("NATIVE_PAD: virtual iOS gamepad added");
        }
        // Idle frames send nothing: the device keeps its last state and the
        // game's last-used-device tracking stays quiet.
        var next = new Vector2(snapshot.leftX, snapshot.leftY);
        if (snapshot.buttons == buttons && next == stick) return;
        buttons = snapshot.buttons;
        stick = next;
        InputSystem.QueueStateEvent(device, State(buttons, stick));
    }

    void OnDisable() { Remove(); }

    void Remove()
    {
        if (device == null) return;
        InputSystem.RemoveDevice(device);
        device = null;
        buttons = 0;
        stick = Vector2.zero;
        if (NativePreferences.CompatibilityLogs) Debug.Log("NATIVE_PAD: virtual iOS gamepad removed");
    }

    static void YieldToRealDevice()
    {
#if UNITY_IOS && !UNITY_EDITOR
        var keyboard = Keyboard.current;
        if (keyboard != null && keyboard.anyKey.wasPressedThisFrame) { NativeVirtualPad_RealDeviceInput(); return; }
        foreach (var other in InputSystem.devices)
        {
            // Screen touches are the pad's own input path, not a real device.
            if (other == null || other is Touchscreen || !other.wasUpdatedThisFrame) continue;
            NativeVirtualPad_RealDeviceInput();
            return;
        }
#endif
    }

    static GamepadState State(int buttons, Vector2 stick)
    {
        var state = new GamepadState(stick, Vector2.zero,
            (buttons & LeftTrigger) != 0 ? 1f : 0f, (buttons & RightTrigger) != 0 ? 1f : 0f);
        state = Button(state, buttons, DpadUp, GamepadButton.DpadUp);
        state = Button(state, buttons, DpadDown, GamepadButton.DpadDown);
        state = Button(state, buttons, DpadLeft, GamepadButton.DpadLeft);
        state = Button(state, buttons, DpadRight, GamepadButton.DpadRight);
        state = Button(state, buttons, South, GamepadButton.South);
        state = Button(state, buttons, East, GamepadButton.East);
        state = Button(state, buttons, West, GamepadButton.West);
        state = Button(state, buttons, North, GamepadButton.North);
        state = Button(state, buttons, LeftShoulder, GamepadButton.LeftShoulder);
        state = Button(state, buttons, RightShoulder, GamepadButton.RightShoulder);
        state = Button(state, buttons, Select, GamepadButton.Select);
        return Button(state, buttons, Start, GamepadButton.Start);
    }

    static GamepadState Button(GamepadState state, int buttons, int mask, GamepadButton button)
    {
        return (buttons & mask) != 0 ? state.WithButton(button) : state;
    }

    static Snapshot ReadNative()
    {
#if UNITY_IOS && !UNITY_EDITOR
        Snapshot value;
        NativeVirtualPad_ReadState(out value);
        return value;
#else
        return default(Snapshot);
#endif
    }

#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void NativeVirtualPad_ReadState(out Snapshot state);
    [DllImport("__Internal")] static extern void NativeVirtualPad_RealDeviceInput();
#endif
}
