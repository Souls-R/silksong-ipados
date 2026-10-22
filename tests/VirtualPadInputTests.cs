using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.iOS.LowLevel;

namespace UnityEngine
{
    public class MonoBehaviour {}
    public static class Application { public static bool isFocused = true; }
    public static class Debug { public static void Log(string message) {} }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x=x; this.y=y; }
        public static Vector2 zero => new Vector2();
        public float sqrMagnitude => x*x+y*y;
        public static bool operator ==(Vector2 a, Vector2 b) => a.x==b.x && a.y==b.y;
        public static bool operator !=(Vector2 a, Vector2 b) => !(a==b);
        public override bool Equals(object other) => other is Vector2 value && this==value;
        public override int GetHashCode() => HashCode.Combine(x,y);
    }
}
namespace UnityEngine.InputSystem.Controls
{
    public class InputControl {}
    public class ButtonControl : InputControl { public bool isPressed, wasPressedThisFrame; }
    public class Vector2Control : InputControl
    {
        public Vector2 value;
        public Vector2 ReadValue() => value;
    }
}
namespace UnityEngine.InputSystem.Layouts
{
    public struct InputDeviceDescription { public string interfaceName, deviceClass, product, manufacturer; }
}
namespace UnityEngine.InputSystem
{
    public class InputDevice
    {
        public bool wasUpdatedThisFrame;
        public readonly List<InputControl> allControls = new List<InputControl>();
    }
    public class Touchscreen : InputDevice {}
    public class Keyboard : InputDevice { public readonly ButtonControl anyKey = new ButtonControl(); }
    public class Mouse : InputDevice
    {
        public readonly Vector2Control delta = new Vector2Control(), scroll = new Vector2Control();
    }
    public class Gamepad : InputDevice
    {
        public readonly Vector2Control leftStick = new Vector2Control(), rightStick = new Vector2Control();
    }
    public static class InputSystem
    {
        public static readonly List<InputDevice> devices = new List<InputDevice>();
        public static int events, removed, added;
        public static Gamepad synthetic;
        internal static iOSGameControllerState last;
        public static InputDevice AddDevice(Layouts.InputDeviceDescription description)
        {
            if (description.interfaceName!="iOS" || description.deviceClass!="iOSGameController")
                throw new Exception("The virtual controller must use the registered iOS layout");
            added++;
            synthetic = new Gamepad();
            devices.Add(synthetic);
            return synthetic;
        }
        public static void RemoveDevice(InputDevice device) { removed++; devices.Remove(device); }
        internal static void QueueStateEvent(InputDevice device, iOSGameControllerState state)
        {
            if (!devices.Contains(device)) throw new Exception("Event sent to a removed device");
            last = state;
            events++;
        }
    }
}
namespace UnityEngine.InputSystem.iOS.LowLevel
{
    // Unity Input System 1.14.2's IGC button/axis numbering. The real Unity
    // compile additionally checks this driver against the package itself.
    internal enum iOSButton
    {
        DpadUp, DpadDown, DpadLeft, DpadRight, LeftStick, RightStick,
        LeftShoulder, RightShoulder, LeftTrigger, RightTrigger, X, Y, A, B, Start, Select
    }
    internal enum iOSAxis { LeftStickX, LeftStickY, RightStickX, RightStickY }
    internal struct iOSGameControllerState
    {
        public uint buttons;
        public float[] values, axes;
        public string format => "IGC ";
        public iOSGameControllerState WithButton(iOSButton button)
        {
            if (values==null) values=new float[16];
            buttons |= 1u<<(int)button;
            values[(int)button]=1;
            return this;
        }
        public iOSGameControllerState WithAxis(iOSAxis axis, float value)
        {
            if (axes==null) axes=new float[4];
            axes[(int)axis]=value;
            return this;
        }
    }
}
public static class NativePreferences { public static bool CompatibilityLogs; }

static class VirtualPadInputTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Main()
    {
        var sensor = new InputDevice { wasUpdatedThisFrame=true };
        sensor.allControls.Add(new ButtonControl { isPressed=true });
        var touch = new Touchscreen { wasUpdatedThisFrame=true };
        var physical = new Gamepad { wasUpdatedThisFrame=true };
        var physicalButton = new ButtonControl();
        physical.allControls.Add(physicalButton);
        var mouse = new Mouse { wasUpdatedThisFrame=true };
        var mouseButton = new ButtonControl();
        mouse.allControls.Add(mouseButton);
        var keyboard = new Keyboard { wasUpdatedThisFrame=true };
        InputSystem.devices.AddRange(new InputDevice[] {sensor, touch, physical, mouse, keyboard});

        var snapshot = new NativeVirtualPad.Snapshot { state=1, buttons=1<<4, leftX=.5f, leftY=-.25f };
        int reads=0, takeovers=0;
        var driver = new NativeVirtualPad();
        driver.readState=()=> { reads++; return snapshot; };
        driver.realDeviceInput=()=> { takeovers++; snapshot.state=0; };
        driver.Update();
        Check(takeovers==0 && InputSystem.added==1, "Sensor and idle polling must not hide the overlay");
        Check(InputSystem.last.format=="IGC " && InputSystem.last.buttons==(1u<<12), "A tap must reach the iOS buttonSouth control");
        Check(InputSystem.last.axes[0]==.5f && InputSystem.last.axes[1]==-.25f, "Preserve stick polarity and magnitude");
        int before=InputSystem.events;
        driver.Update();
        Check(InputSystem.events==before, "Idle frames do not queue duplicate virtual input");

        // Every native control must address its original iOS gamepad control,
        // especially SELECT/START and the trigger float slots.
        int[] mapping={0,1,2,3,12,13,10,11,6,7,8,9,15,14};
        for (int bit=0; bit<mapping.Length; bit++)
        {
            snapshot.buttons=1<<bit;
            driver.Update();
            Check(InputSystem.last.buttons==(1u<<mapping[bit]), "Button mapping for native bit "+bit);
        }
        snapshot.buttons=(1<<10)|(1<<11);
        driver.Update();
        Check(InputSystem.last.values[8]==1 && InputSystem.last.values[9]==1, "Both analog triggers report full pressure");
        snapshot.buttons=0; snapshot.leftX=snapshot.leftY=0;
        driver.Update();
        Check(InputSystem.last.buttons==0 && InputSystem.last.axes[0]==0, "Release clears buttons and stick");
        Check(InputSystem.last.values==null, "Released trigger pressure defaults to zero");

        InputSystem.synthetic.wasUpdatedThisFrame=true;
        InputSystem.synthetic.allControls.Add(new ButtonControl { isPressed=true });
        physical.leftStick.value=new Vector2(.05f, -.03f);
        driver.Update();
        Check(takeovers==0, "Synthetic input and physical stick jitter do not count as a takeover");
        Check(!NativeVirtualPad.HasPhysicalInput(null,null), "Missing devices are harmless");
        physicalButton.isPressed=true;
        physical.wasUpdatedThisFrame=false;
        Check(!NativeVirtualPad.HasPhysicalInput(physical,null), "Ignore held state with no current device update");
        physical.wasUpdatedThisFrame=true;
        Check(NativeVirtualPad.HasPhysicalInput(physical,null), "A real gamepad button takes over");
        physicalButton.isPressed=false;
        physical.rightStick.value=new Vector2(.4f,0);
        Check(NativeVirtualPad.HasPhysicalInput(physical,null), "A purposeful stick movement takes over");
        physical.rightStick.value=Vector2.zero;
        mouse.scroll.value=new Vector2(0,1);
        Check(NativeVirtualPad.HasPhysicalInput(mouse,null), "Mouse scrolling takes over");
        mouse.scroll.value=Vector2.zero;
        mouseButton.isPressed=true;
        Check(NativeVirtualPad.HasPhysicalInput(mouse,null), "Mouse side/buttons take over");
        mouseButton.isPressed=false;
        keyboard.anyKey.wasPressedThisFrame=true;
        Check(NativeVirtualPad.HasPhysicalInput(keyboard,null), "A hardware key takes over");
        keyboard.anyKey.wasPressedThisFrame=false;

        // The native overlay hides synchronously on takeover. Do not consume
        // its previous held snapshot or queue one more press after hiding it.
        mouse.delta.value=new Vector2(1,0);
        snapshot.buttons=1<<4;
        before=InputSystem.events;
        driver.Update();
        Check(takeovers==1 && !InputSystem.devices.Contains(InputSystem.synthetic), "Remove the virtual pad in the takeover frame");
        Check(InputSystem.events==before, "No delayed virtual press after physical input takes over");
        mouse.delta.value=Vector2.zero;

        // Always On may leave its view visible. Suspension must still release
        // the virtual device immediately and prevent reads until resume.
        snapshot.state=1;
        driver.Update();
        driver.OnApplicationPause(true);
        Check(!InputSystem.devices.Contains(InputSystem.synthetic), "Pause removes an Always On virtual pad");
        before=reads;
        driver.Update();
        Check(reads==before, "Do not read stale native input while suspended");
        snapshot.buttons=0;
        driver.OnApplicationPause(false);
        driver.Update();
        Check(InputSystem.devices.Contains(InputSystem.synthetic), "Resume can recreate a visible neutral pad");
        Application.isFocused=false;
        driver.OnApplicationFocus(false);
        before=reads;
        driver.Update();
        Check(!InputSystem.devices.Contains(InputSystem.synthetic) && reads==before, "Focus loss clears input before another frame");
        Application.isFocused=true;
        driver.OnApplicationFocus(true);
        driver.Update();
        snapshot.state=-1;
        driver.Update();
        Check(!InputSystem.devices.Contains(InputSystem.synthetic), "Always Off removes the virtual device");
        snapshot.state=0;
        driver.Update();
        Check(!InputSystem.devices.Contains(InputSystem.synthetic), "The layout editor/hidden overlay cannot report gameplay input");
        snapshot.state=1;
        driver.Update();
        driver.OnDisable();
        Check(!InputSystem.devices.Contains(InputSystem.synthetic), "Disabling the boundary releases its device");
        Check(InputSystem.devices.OfType<Keyboard>().Count()==1, "Virtual pad input never creates a keyboard");
        Console.WriteLine("PASS virtual IGC controls, trigger pressure, physical takeover, sensors, idle polling, same-frame removal and lifecycle");
    }
}
