using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.iOS.LowLevel;
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
    const float PhysicalStickThresholdSquared = 0.04f; // Ignore drift below 20% travel.

    internal Func<Snapshot> readState = ReadNative;
    internal Action realDeviceInput = NotifyRealDeviceInput;
    InputDevice device;
    int buttons;
    Vector2 stick;
    bool suspended;

    internal void Update()
    {
        if (suspended || !Application.isFocused) { Remove(); return; }
        // Decide visibility before consuming pending taps: a physical device
        // taking over must release the virtual pad in this same update.
        YieldToRealDevice();
        Snapshot snapshot = readState();
        // -1: the display mode is off; 0: the overlay is hidden. Either way the
        // game must not see a virtual gamepad at all.
        if (snapshot.state <= 0) { Remove(); return; }
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

    internal void OnDisable() { Remove(); }
    internal void OnApplicationFocus(bool focused) { if (!focused) Remove(); }
    internal void OnApplicationPause(bool paused) { suspended = paused; if (paused) Remove(); }

    void Remove()
    {
        if (device == null) return;
        InputSystem.RemoveDevice(device);
        device = null;
        buttons = 0;
        stick = Vector2.zero;
        if (NativePreferences.CompatibilityLogs) Debug.Log("NATIVE_PAD: virtual iOS gamepad removed");
    }

    void YieldToRealDevice()
    {
        foreach (var other in InputSystem.devices)
        {
            if (!HasPhysicalInput(other, device)) continue;
            realDeviceInput();
            return;
        }
    }

    internal static bool HasPhysicalInput(InputDevice other, InputDevice virtualDevice)
    {
        if (other == null || other == virtualDevice || !other.wasUpdatedThisFrame) return false;
        var keyboard = other as Keyboard;
        if (keyboard != null) return keyboard.anyKey.wasPressedThisFrame;
        var mouse = other as Mouse;
        var pad = other as Gamepad;
        // Device updates also include sensors and unchanged native controller
        // polling. Neither means the player switched away from touch controls.
        if (mouse != null)
        {
            if (mouse.delta.ReadValue().sqrMagnitude > 0 || mouse.scroll.ReadValue().sqrMagnitude > 0) return true;
        }
        else if (pad != null)
        {
            if (pad.leftStick.ReadValue().sqrMagnitude > PhysicalStickThresholdSquared ||
                pad.rightStick.ReadValue().sqrMagnitude > PhysicalStickThresholdSquared) return true;
        }
        else return false;
        foreach (var control in other.allControls)
        {
            var button = control as ButtonControl;
            if (button != null && button.isPressed) return true;
        }
        return false;
    }

    static void NotifyRealDeviceInput()
    {
#if UNITY_IOS && !UNITY_EDITOR
        NativeVirtualPad_RealDeviceInput();
#endif
    }

    static iOSGameControllerState State(int buttons, Vector2 stick)
    {
        // The registered iOSGameController device expects IGC state events.
        var state = new iOSGameControllerState()
            .WithAxis(iOSAxis.LeftStickX, stick.x)
            .WithAxis(iOSAxis.LeftStickY, stick.y);
        state = Button(state, buttons, LeftTrigger, iOSButton.LeftTrigger);
        state = Button(state, buttons, RightTrigger, iOSButton.RightTrigger);
        state = Button(state, buttons, DpadUp, iOSButton.DpadUp);
        state = Button(state, buttons, DpadDown, iOSButton.DpadDown);
        state = Button(state, buttons, DpadLeft, iOSButton.DpadLeft);
        state = Button(state, buttons, DpadRight, iOSButton.DpadRight);
        state = Button(state, buttons, South, iOSButton.A);
        state = Button(state, buttons, East, iOSButton.B);
        state = Button(state, buttons, West, iOSButton.X);
        state = Button(state, buttons, North, iOSButton.Y);
        state = Button(state, buttons, LeftShoulder, iOSButton.LeftShoulder);
        state = Button(state, buttons, RightShoulder, iOSButton.RightShoulder);
        state = Button(state, buttons, Select, iOSButton.Select);
        return Button(state, buttons, Start, iOSButton.Start);
    }

    static iOSGameControllerState Button(iOSGameControllerState state, int buttons, int mask, iOSButton button)
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
