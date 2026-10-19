using System;
using System.Collections.Generic;
using System.Text.Json;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.LowLevel;

namespace UnityEngine {
    public static class Application {
        public static bool isFocused = true;
        public static event Action<bool> focusChanged;
        public static void Focus(bool value) { isFocused=value; focusChanged?.Invoke(value); }
    }
    public static class Debug { public static void Log(string value) { } }
}
namespace UnityEngine.InputSystem {
    public struct Description { public string interfaceName,product; }
    public class InputDevice { public int deviceId; public Description description; }
    public class Button {
        readonly Gamepad owner;
        readonly uint bit;
        public Button(Gamepad owner,uint bit) { this.owner=owner;this.bit=bit; }
        public float value {
            get { return (owner.rawButtons & bit)!=0 ? 1f : 0f; }
            set { owner.rawButtons=value>0.5f ? owner.rawButtons|bit : owner.rawButtons&~bit; }
        }
        public bool isPressed { get { return value>0.5f; } }
    }
    public class Gamepad : InputDevice {
        public static readonly List<Gamepad> all=new List<Gamepad>();
        public uint rawButtons;
        public float leftTrigger=0.75f,leftStickX=-0.6f;
        public Button buttonSouth,startButton,leftShoulder;
        public Gamepad() {
            buttonSouth=new Button(this,1u<<12);startButton=new Button(this,1u<<14);leftShoulder=new Button(this,1u<<6);
        }
        internal void CopyState(out NativeControllerButtons snapshot) { snapshot=new NativeControllerButtons { buttons=rawButtons }; }
    }
    public enum InputDeviceChange { Added,Removed,Disconnected,Disabled,Reconnected }
    public static class InputSystem {
        public static readonly List<string> layouts=new List<string>();
        public static event Action onAfterUpdate;
        public static event Action<InputDevice,InputDeviceChange> onDeviceChange;
        public static void RegisterLayoutOverride(string json) { layouts.Add(json); }
        public static void Update() { onAfterUpdate?.Invoke(); }
        public static void Change(InputDevice device,InputDeviceChange change) { onDeviceChange?.Invoke(device,change); }
    }
}
namespace UnityEngine.InputSystem.DualShock {
    public class DualShockGamepad : Gamepad {
        public Button touchpadButton;
        public DualShockGamepad() { touchpadButton=new Button(this,1u<<16); }
    }
}
namespace UnityEngine.InputSystem.Utilities {
    public struct FourCC {
        public FourCC(char a,char b,char c,char d) { }
    }
}
namespace UnityEngine.InputSystem.LowLevel {
    public interface IInputStateTypeInfo { UnityEngine.InputSystem.Utilities.FourCC format { get; } }
    public enum InputUpdateType { Dynamic,Fixed,BeforeRender }
    public static class InputState {
        public static InputUpdateType currentUpdateType;
        public static void Change(Button button,float value) { throw new ArgumentException("InputState.Change does not accept bitfield controls"); }
        internal static void Change(Gamepad pad,NativeControllerButtons snapshot) { pad.rawButtons=snapshot.buttons; }
    }
}
public static class NativePreferences { public static bool CompatibilityLogs; }

static class ControllerInputTests {
    static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    static DualShockGamepad Pad(int id,string backend) {
        return new DualShockGamepad { deviceId=id,description=new Description { interfaceName=backend,product="pad-"+id } };
    }
    static void Main() {
        var first=Pad(11,"iOS");var second=Pad(22,"iOS");var hid=Pad(33,"HID");
        var xbox=new Gamepad { deviceId=44,description=new Description { interfaceName="iOS",product="Xbox" } };
        Gamepad.all.AddRange(new Gamepad[]{first,second,hid,xbox});
        first.buttonSouth.value=first.startButton.value=first.leftShoulder.value=hid.touchpadButton.value=1;
        var states=new Dictionary<int,int>{{11,1},{22,0}};int reads=0;
        NativeControllerInput.ReadTouchpad=(id,product)=> { reads++;Check(product=="pad-"+id,"Match each device's product");return states[id]; };
        NativeControllerInput.Install();NativeControllerInput.Install();
        Check(InputSystem.layouts.Count==2,"Install the bridge once for the two iOS PS layouts");
        foreach(string layout in InputSystem.layouts) {
            using(var doc=JsonDocument.Parse(layout)) {
                var root=doc.RootElement;
                Check(root.GetProperty("extend").GetString().EndsWith("GampadiOS"),"Override only iOS layouts");
                var control=root.GetProperty("controls")[0];
                Check(control.GetProperty("name").GetString()=="touchpadButton" && control.GetProperty("offset").GetInt32()==0 && control.GetProperty("bit").GetInt32()==16,"Use an unused IGC button bit, preserving existing controls");
            }
        }
        InputState.currentUpdateType=InputUpdateType.Fixed;InputSystem.Update();Check(reads==0,"Do not consume short presses in fixed updates");
        InputState.currentUpdateType=InputUpdateType.Dynamic;InputSystem.Update();
        Check(reads==2 && first.touchpadButton.isPressed && !second.touchpadButton.isPressed,"Independent native state after one dynamic update");
        Check(first.buttonSouth.isPressed && first.startButton.isPressed && first.leftShoulder.isPressed,"Other gamepad controls remain unchanged");
        Check(first.leftTrigger==0.75f && first.leftStickX==-0.6f,"Partial button snapshot preserves analog state");
        Check(hid.touchpadButton.isPressed,"Desktop HID input remains untouched");
        first.touchpadButton.value=0;InputSystem.Update();Check(first.touchpadButton.isPressed,"Restore held touchpad after native IGC state overwrites its bit");
        states[11]=0;states[22]=1;InputSystem.Update();Check(!first.touchpadButton.isPressed && second.touchpadButton.isPressed,"Release and another controller's press remain independent");
        states[22]=-1;InputSystem.Update();Check(!second.touchpadButton.isPressed,"Unavailable/ambiguous devices release stale presses");
        states[11]=1;InputSystem.Update();UnityEngine.Application.Focus(false);
        Check(!first.touchpadButton.isPressed && hid.touchpadButton.isPressed,"Focus loss clears only supported iOS controllers");
        int before=reads;InputSystem.Update();Check(reads==before,"Background updates cannot reintroduce a held native button");
        states[11]=0;UnityEngine.Application.Focus(true);InputSystem.Update();Check(!first.touchpadButton.isPressed,"Resume starts from released native state");
        states[11]=1;InputSystem.Update();states[11]=-2;InputSystem.Update();Check(!first.touchpadButton.isPressed,"UIKit inactivity wins even before Unity focus changes");
        InputSystem.Change(first,InputDeviceChange.Disconnected);
        Console.WriteLine("PASS iOS touchpad update order, layout bits, independent devices, Xbox/HID isolation, release and focus");
    }
}
