using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;
using InputDevice = UnityEngine.InputSystem.InputDevice;

// InControl already calls Gamepad.SetMotorSpeeds. Supply the missing iOS output
// path without polling the vibration mixer or changing its timing/settings.
public static class NativeControllerHaptics
{
    static readonly FourCC Rumble = new FourCC('R', 'M', 'B', 'L');
    static readonly Dictionary<int, int> reported = new Dictionary<int, int>();
    static bool installed;

    [DllImport("__Internal")] static extern void NativeHaptics_Register(int id, string product);
    [DllImport("__Internal")] static extern int NativeHaptics_Set(int id, float low, float high);
    [DllImport("__Internal")] static extern void NativeHaptics_Remove(int id);
    [DllImport("__Internal")] static extern void NativeHaptics_Stop();

    public static unsafe void Install()
    {
        if (installed) return;
        installed = true;
        InputSystem.onDeviceCommand += Command;
        InputSystem.onDeviceChange += Changed;
        Application.focusChanged += FocusChanged;
        Application.quitting += NativeHaptics_Stop;
        foreach (var device in InputSystem.devices) Register(device);
    }

    static bool Supported(InputDevice device)
    {
        return device is Gamepad && device.description.interfaceName == "iOS";
    }

    static void Register(InputDevice device)
    {
        if (Supported(device)) NativeHaptics_Register(device.deviceId, device.description.product ?? "");
    }

    static void Changed(InputDevice device, InputDeviceChange change)
    {
        if (!Supported(device)) return;
        if (change == InputDeviceChange.Added || change == InputDeviceChange.Reconnected)
            Register(device);
        else if (change == InputDeviceChange.Removed || change == InputDeviceChange.Disconnected || change == InputDeviceChange.Disabled)
        {
            NativeHaptics_Remove(device.deviceId);
            reported.Remove(device.deviceId);
        }
        else if (change == InputDeviceChange.Enabled) Register(device);
    }

    static void FocusChanged(bool focused) { if (!focused) NativeHaptics_Stop(); }

    static float Speed(float value)
    {
        return float.IsNaN(value) ? 0f : Mathf.Clamp01(value);
    }

    static unsafe long? Command(InputDevice device, InputDeviceCommand* command)
    {
        if (!Supported(device) || command->type != Rumble ||
            command->sizeInBytes < sizeof(InputDeviceCommand) + sizeof(float) * 2)
            return null;
        // DualMotorRumbleCommand is internal in Input System 1.14.2. Its public
        // command ABI is the base header followed by two 32-bit motor speeds.
        float* motors = (float*)(command + 1);
        float low = Speed(motors[0]), high = Speed(motors[1]);
        int result = NativeHaptics_Set(device.deviceId, low, high);
        int previous;
        if (NativePreferences.CompatibilityLogs && (low > 0 || high > 0) &&
            (!reported.TryGetValue(device.deviceId, out previous) || previous != result))
        {
            reported[device.deviceId] = result;
            Debug.Log("NATIVE_HAPTICS: product=" + device.description.product + "; output=" + result +
                " (2=dual, 1=single, 0=unavailable, -1=engine error)");
        }
        return result > 0 ? InputDeviceCommand.GenericSuccess : InputDeviceCommand.GenericFailure;
    }
}
