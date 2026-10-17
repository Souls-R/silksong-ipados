using System;

static class BindingPointerGateTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Main()
    {
        foreach (bool eventSystemFirst in new[] { false, true })
        {
            var gate = new NativeBindingPointerGate();
            gate.Observe(false, false, 1);
            Check(!gate.Blocks(1), "Normal menu click remains enabled");
            gate.Observe(true, false, 2); // Click has opened the binding listener.
            gate.Observe(true, true, 3);  // Button down belongs to capture.
            Check(gate.Blocks(3), "Capture press cannot activate another option");
            gate.Observe(true, false, 4); // Snapshot before InControl completes on release.
            if (!eventSystemFirst) gate.Observe(false, false, 4);
            Check(gate.Blocks(4), "Binding release cannot reopen the menu listener");
            gate.Observe(false, false, 4);
            Check(gate.Blocks(4), "Repeated observation in the same frame remains blocked");
            gate.Observe(false, false, 5);
            Check(!gate.Blocks(5), "Next separate click works normally");
        }
        var cancel = new NativeBindingPointerGate();
        cancel.Observe(true, true, 1);
        cancel.Observe(false, true, 2); // Cancel by keyboard while mouse held.
        Check(cancel.Blocks(2), "Cancellation must drain held pointer");
        cancel.Observe(false, false, 3);
        Check(cancel.Blocks(3), "Cancellation release cannot click through");
        cancel.Observe(false, false, 4);
        Check(!cancel.Blocks(4), "Cancel/disconnect cannot leave input stuck");
        Console.WriteLine("PASS: capture release ordering, cancellation, held-pointer drain, normal menu recovery");
    }
}
