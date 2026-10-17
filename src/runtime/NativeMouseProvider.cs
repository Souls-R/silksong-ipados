using System;
using System.Runtime.InteropServices;
using InControl;
using UnityEngine;

// Keep InControl's original update, binding listener and serialization.
public sealed class NativeMouseProvider : IMouseProvider
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Snapshot
    {
        public int buttons, devices, gcButtons, uiButtons, gcEvents, uiEvents, hasPosition;
        public float x, y;
    }
    readonly IMouseProvider fallback;
    readonly Func<Snapshot> readState;
    Snapshot snapshot;
    int previous, buttons;
    Vector2 position, delta;
    bool positioned;
    bool present, reported, lastNativePresent, nativePointer;
    int lastDeviceCount;
    const int ButtonMask = (1 << 1) | (1 << 2) | (1 << 3) |
        (1 << 10) | (1 << 11) | (1 << 12) | (1 << 13);

    [DllImport("__Internal")] static extern void NativeMouse_ReadState(out Snapshot state);
    static Snapshot ReadNative() { Snapshot value; NativeMouse_ReadState(out value); return value; }

    public NativeMouseProvider(IMouseProvider original) : this(original, ReadNative) { }
    internal NativeMouseProvider(IMouseProvider original, Func<Snapshot> read)
    {
        fallback = original;
        readState = read;
    }

    public void Setup() { Reset(); }
    public void Reset()
    {
        buttons = previous = 0;
        present = false;
        nativePointer = false;
        positioned = false;
        position = delta = Vector2.zero;
        if (fallback != null) fallback.Reset();
    }

    public void Update()
    {
        NativeMouseUI.BeforeInputUpdate();
        previous = buttons;
        buttons = 0;
        present = false;
        delta = Vector2.zero;
        if (!Application.isFocused) { positioned = false; return; }

        snapshot = readState();
        int native = snapshot.buttons;
        if (native == -2) { positioned = false; return; } // UIKit has already suspended the application.
        if (fallback != null) fallback.Update();
        bool nativePresent = native >= 0;
        if (!reported || nativePresent != lastNativePresent || snapshot.devices != lastDeviceCount)
        {
            if (NativePreferences.CompatibilityLogs)
                Debug.Log("NATIVE_INPUT: mouse present=" + nativePresent + "; GCMouse devices=" + snapshot.devices);
            reported = true;
            lastNativePresent = nativePresent;
            lastDeviceCount = snapshot.devices;
        }
        if (nativePresent)
        {
            buttons = native & ButtonMask;
            present = true;
        }
        else if (fallback != null && fallback.HasMousePresent())
        {
            present = true;
            for (int i = 1; i <= 13; i++)
                if ((ButtonMask & (1 << i)) != 0 && fallback.GetButtonIsPressed((Mouse)i))
                    buttons |= 1 << i;
        }
        nativePointer = snapshot.hasPosition != 0;
        Vector2 next = nativePointer ? new Vector2(snapshot.x * Screen.width, snapshot.y * Screen.height)
            : HasUnityPointer ? fallback.GetPosition() : (Vector2)Input.mousePosition;
        delta = positioned ? next - position : Vector2.zero;
        position = next;
        positioned = present;
        if (NativePreferences.CompatibilityLogs && previous != buttons)
            Debug.Log("NATIVE_INPUT: mouse buttons=" + buttons + "; previous=" + previous +
                "; " + DescribeSnapshot() + "; touches=" + Input.touchCount + "; cursor=" + Cursor.lockState);
    }

    public string DescribeSnapshot()
    {
        return "gc=" + snapshot.gcButtons + "; ui=" + snapshot.uiButtons +
            "; gcEvents=" + snapshot.gcEvents + "; uiEvents=" + snapshot.uiEvents +
            "; nativePointer=" + nativePointer;
    }

    static bool Pressed(int state, Mouse control)
    {
        int index = (int)control;
        return index >= 1 && index <= 13 && (state & ButtonMask & (1 << index)) != 0;
    }
    public bool GetButtonIsPressed(Mouse control) { return Application.isFocused && Pressed(buttons, control); }
    public bool GetButtonWasPressed(Mouse control) { return GetButtonIsPressed(control) && !Pressed(previous, control); }
    public bool GetButtonWasReleased(Mouse control) { return !GetButtonIsPressed(control) && Pressed(previous, control); }
    public bool HasMousePresent() { return Application.isFocused && present; }
    bool HasUnityPointer { get { return Application.isFocused && fallback != null && fallback.HasMousePresent(); } }
    public Vector2 GetPosition() { return Application.isFocused ? position : Vector2.zero; }
    public float GetDeltaX() { return !Application.isFocused ? 0f : !nativePointer && HasUnityPointer ? fallback.GetDeltaX() : delta.x; }
    public float GetDeltaY() { return !Application.isFocused ? 0f : !nativePointer && HasUnityPointer ? fallback.GetDeltaY() : delta.y; }
    public float GetDeltaScroll() { return !Application.isFocused ? 0f : HasUnityPointer ? fallback.GetDeltaScroll() : Input.mouseScrollDelta.y; }
}
