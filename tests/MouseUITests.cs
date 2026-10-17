using System;
using InControl;
using UnityEngine;
using UnityEngine.EventSystems;

// Host API surface for the production menu bridge, without an iOS runtime.
namespace UnityEngine {
    public class Object { public static implicit operator bool(Object value) { return value != null; } }
    public class GameObject : Object {
        public int additions;
        public T AddComponent<T>() where T : new() { additions++; return new T(); }
    }
    public struct Vector2 {
        public float x,y;
        public Vector2(float x,float y) { this.x=x; this.y=y; }
        public static implicit operator Vector2(Vector3 v) { return new Vector2(v.x,v.y); }
        public static implicit operator Vector3(Vector2 v) { return new Vector3 { x=v.x,y=v.y }; }
    }
    public struct Vector3 { public float x,y,z; }
    public static class Time { public static int frameCount; }
    public static class Debug { public static void Log(string value) { } }
    public static class Mathf { public static float Abs(float v) { return Math.Abs(v); } public static float Max(float a,float b) { return Math.Max(a,b); } }
    public static class Input {
        public static int touchCount;
        public static Vector3 mousePosition;
        public static bool down,up,held;
        public static bool GetMouseButtonDown(int b) { return b==0 && down; }
        public static bool GetMouseButtonUp(int b) { return b==0 && up; }
        public static bool GetMouseButton(int b) { return b==0 && held; }
        public static float GetAxisRaw(string name) { return 0; }
    }
}
namespace UnityEngine.EventSystems {
    public class BaseInput : UnityEngine.Object {
        public virtual bool mousePresent { get { return true; } }
        public virtual Vector2 mousePosition { get { return Input.mousePosition; } }
        public virtual Vector2 mouseScrollDelta { get { return default(Vector2); } }
        public virtual bool GetMouseButtonDown(int b) { return Input.GetMouseButtonDown(b); }
        public virtual bool GetMouseButtonUp(int b) { return Input.GetMouseButtonUp(b); }
        public virtual bool GetMouseButton(int b) { return Input.GetMouseButton(b); }
    }
    public class BaseInputModule { public GameObject gameObject=new GameObject(); public BaseInput inputOverride; }
    public class StandaloneInputModule : BaseInputModule { }
    public class PointerInputModule {
        public class MouseButtonEventData {
            public PointerEventData buttonData=new PointerEventData();
            public string buttonState;
            public bool PressedThisFrame() { return buttonState=="Pressed"; }
            public bool ReleasedThisFrame() { return buttonState=="Released"; }
        }
    }
    public struct RaycastResult { public GameObject gameObject; }
    public class PointerEventData {
        public GameObject pointerPress,rawPointerPress,pointerDrag;
        public bool dragging,eligibleForClick;
        public string button;
        public Vector2 position;
        public RaycastResult pointerCurrentRaycast;
    }
    public static class ExecuteEvents {
        public static object pointerUpHandler,endDragHandler;
        public static void Execute(GameObject target,PointerEventData data,object handler) { }
    }
}
namespace InControl {
    public enum Mouse { None,LeftButton,RightButton,MiddleButton }
    public interface IMouseProvider {
        bool HasMousePresent(); Vector2 GetPosition(); float GetDeltaScroll(); float GetDeltaX(); float GetDeltaY();
        bool GetButtonWasPressed(Mouse b); bool GetButtonWasReleased(Mouse b); bool GetButtonIsPressed(Mouse b);
    }
    public static class InputManager { public static IMouseProvider MouseProvider; }
    public class HollowKnightInputModule : StandaloneInputModule { }
    public class PlayerAction { public string Name; }
    public class BindingSource { public string BindingSourceType,Name; }
}
public class InputHandler : UnityEngine.Object {
    public static InputHandler UnsafeInstance=new InputHandler();
    public ActionSet inputActions=new ActionSet();
    public class ActionSet { public bool IsListeningForBinding; }
}
public static class NativePreferences { public static bool CompatibilityLogs; }
public class NativeMouseProvider : IMouseProvider {
    public bool down,up,held;
    public bool HasMousePresent() { return true; }
    public Vector2 GetPosition() { return new Vector2(400,500); }
    public float GetDeltaScroll() { return 0; } public float GetDeltaX() { return 0; } public float GetDeltaY() { return 0; }
    public bool GetButtonWasPressed(Mouse b) { return b==Mouse.LeftButton && down; }
    public bool GetButtonWasReleased(Mouse b) { return b==Mouse.LeftButton && up; }
    public bool GetButtonIsPressed(Mouse b) { return b==Mouse.LeftButton && held; }
    public string DescribeSnapshot() { return "test"; }
}
static class MouseUITests {
    static void Check(bool ok,string message) { if (!ok) throw new Exception(message); }
    static void Main() {
        var mouse=new NativeMouseProvider(); InputManager.MouseProvider=mouse;
        var module=new HollowKnightInputModule(); NativeMouseUI.Prepare(module);
        BaseInput input=module.inputOverride;
        Check(input is NativeMouseUI,"Actual game's menu receives input override");
        NativeMouseUI.Prepare(module); Check(module.gameObject.additions==1,"Install once");
        mouse.down=mouse.held=true;
        Check(input.GetMouseButtonDown(0) && input.GetMouseButton(0),"Hardware left click bypasses empty legacy input");
        Check(input.mousePosition.x==400 && NativeMouseUI.MouseButtonDown(0),"Activation and UI share pointer snapshot");
        mouse.down=mouse.held=false; mouse.up=true;
        Check(input.GetMouseButtonUp(0),"Menu gets hardware release");
        mouse.up=false; Input.touchCount=1; Input.down=Input.held=true; Input.mousePosition=new Vector3 { x=20,y=30 };
        Check(input.GetMouseButtonDown(0) && input.mousePosition.x==20,"Real finger keeps original menu click and location");
        Check(!mouse.GetButtonWasPressed(Mouse.LeftButton),"Finger is not a gameplay mouse binding");
        Input.touchCount=0; Input.down=Input.held=false; Input.up=true;
        Check(input.GetMouseButtonUp(0) && input.mousePosition.x==20,"Final touch-up is retained after touch count clears");
        Input.up=false;
        Check(input.mousePosition.x==400,"Hardware pointer resumes after finger release");
        InputHandler.UnsafeInstance.inputActions.IsListeningForBinding=true;
        NativeMouseUI.BeforeInputUpdate();
        var data=new PointerInputModule.MouseButtonEventData(); data.buttonData.eligibleForClick=true;
        Check(!NativeMouseUI.ConsumeMouse(new StandaloneInputModule(),data),"Unrelated Unity module untouched");
        Check(NativeMouseUI.ConsumeMouse(module,data) && !data.buttonData.eligibleForClick,"Actual inherited dispatcher drains capture click");
        Console.WriteLine("PASS mouse menu bridge, real touch fallback, binding capture and unrelated module isolation");
    }
}
