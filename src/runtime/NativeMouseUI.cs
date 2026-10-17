using InControl;
using UnityEngine;
using UnityEngine.EventSystems;

// Feed the original EventSystem from the same snapshot as InControl. Keep
// its navigation, click handlers, binding listener and persistence intact.
public sealed class NativeMouseUI : BaseInput
{
    static readonly NativeBindingPointerGate gate = new NativeBindingPointerGate();
    static bool wasListening;
    BaseInput fallback;

    static IMouseProvider Mouse { get { return InputManager.MouseProvider; } }
    // UnityView already removes hardware pointer touches. Keep real finger
    // taps on the menu's original touch-to-mouse path, including touch-up.
    static bool TouchPointer { get { return Input.touchCount > 0 || Input.GetMouseButtonUp(0); } }
    public static void BeforeInputUpdate()
    {
        var handler = InputHandler.UnsafeInstance;
        bool listening = handler && handler.inputActions != null && handler.inputActions.IsListeningForBinding;
        bool held = Input.touchCount > 0;
        if (Mouse != null)
            for (int i = 1; i <= 3; i++) held |= Mouse.GetButtonIsPressed((InControl.Mouse)i);
        gate.Observe(listening, held, Time.frameCount);
        if (NativePreferences.CompatibilityLogs && listening != wasListening)
            Debug.Log("NATIVE_INPUT: binding listening=" + listening + "; frame=" + Time.frameCount +
                "; " + (Mouse is NativeMouseProvider ? ((NativeMouseProvider)Mouse).DescribeSnapshot() : "original provider"));
        wasListening = listening;
    }

    public static void Prepare(HollowKnightInputModule module)
    {
        if (!(module.inputOverride is NativeMouseUI))
        {
            var bridge = module.gameObject.AddComponent<NativeMouseUI>();
            bridge.fallback = module.inputOverride;
            module.inputOverride = bridge;
            if (NativePreferences.CompatibilityLogs)
                Debug.Log("NATIVE_INPUT: mouse UI bridge=" + module.GetType().Name);
        }
        // Also observe binding entered by navigation after the input update.
        BeforeInputUpdate();
    }

    public static bool ConsumePointer(PointerEventData data)
    {
        if (!gate.Blocks(Time.frameCount)) return false;
        // A held pointer from before capture must not remain pressed or click
        // when capture ends. Hover still runs through the original module.
        ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
        if (data.dragging)
            ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.endDragHandler);
        data.eligibleForClick = false;
        data.pointerPress = null;
        data.rawPointerPress = null;
        data.pointerDrag = null;
        data.dragging = false;
        return true;
    }

    public static bool ConsumeMouse(StandaloneInputModule module, PointerInputModule.MouseButtonEventData data)
    {
        // HollowKnightInputModule inherits this dispatcher from Unity UI.
        // Other StandaloneInputModule instances must keep their own behavior.
        if (!(module is HollowKnightInputModule)) return false;
        bool blocked = ConsumePointer(data.buttonData);
        if (NativePreferences.CompatibilityLogs && (data.PressedThisFrame() || data.ReleasedThisFrame()))
            Debug.Log("NATIVE_INPUT: menu mouse=" + data.buttonData.button + "; state=" + data.buttonState +
                "; blocked=" + blocked + "; target=" + data.buttonData.pointerCurrentRaycast.gameObject +
                "; position=" + data.buttonData.position);
        return blocked;
    }

    public static Vector3 PointerPosition() { return !TouchPointer && Mouse != null ? (Vector3)Mouse.GetPosition() : Input.mousePosition; }
    public static bool MouseButtonDown(int button) { return !TouchPointer && Mouse != null ? Mouse.GetButtonWasPressed((InControl.Mouse)(button + 1)) : Input.GetMouseButtonDown(button); }

    public static void BindingAdded(PlayerAction action, BindingSource binding)
    {
        if (NativePreferences.CompatibilityLogs)
            Debug.Log("NATIVE_INPUT: binding added; action=" + action.Name + "; source=" + binding.BindingSourceType + "; control=" + binding.Name);
    }

    public override bool mousePresent { get { return TouchPointer || (Mouse != null && Mouse.HasMousePresent()); } }
    public override Vector2 mousePosition { get { return PointerPosition(); } }
    public override Vector2 mouseScrollDelta { get { return Mouse != null ? new Vector2(0, Mouse.GetDeltaScroll()) : base.mouseScrollDelta; } }
    public override bool GetMouseButtonDown(int button) { return TouchPointer ? base.GetMouseButtonDown(button) : Mouse != null ? Mouse.GetButtonWasPressed((InControl.Mouse)(button + 1)) : fallback ? fallback.GetMouseButtonDown(button) : base.GetMouseButtonDown(button); }
    public override bool GetMouseButtonUp(int button) { return TouchPointer ? base.GetMouseButtonUp(button) : Mouse != null ? Mouse.GetButtonWasReleased((InControl.Mouse)(button + 1)) : fallback ? fallback.GetMouseButtonUp(button) : base.GetMouseButtonUp(button); }
    public override bool GetMouseButton(int button) { return TouchPointer ? base.GetMouseButton(button) : Mouse != null ? Mouse.GetButtonIsPressed((InControl.Mouse)(button + 1)) : fallback ? fallback.GetMouseButton(button) : base.GetMouseButton(button); }

    public static float MouseWakeAxis(string axis)
    {
        if (axis == "mouse x" && Mouse != null && Mouse.HasMousePresent())
        {
            float x = Mouse.GetDeltaX(), y = Mouse.GetDeltaY();
            if (x != 0 || y != 0) return Mathf.Max(Mathf.Abs(x), Mathf.Abs(y));
        }
        return Input.GetAxisRaw(axis);
    }
}
