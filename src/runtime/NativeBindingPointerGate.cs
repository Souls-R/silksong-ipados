// Capture before the action listeners run: a binding can finish on release
// earlier in the same frame than EventSystem dispatches its pointer click.
public sealed class NativeBindingPointerGate
{
    int blockedFrame = -1;
    bool draining;
    public void Observe(bool listening, bool pointerHeld, int frame)
    {
        if (listening) draining = true;
        if (draining)
        {
            blockedFrame = frame;
            if (!listening && !pointerHeld) draining = false;
        }
    }
    public bool Blocks(int frame) { return draining || blockedFrame == frame; }
}
