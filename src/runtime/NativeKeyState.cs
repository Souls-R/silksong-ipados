// Physical USB keyboard usages used by Apple's GCKeyboard (iOS 14+).
// Source IDs are the original, unchanged InControl.Key enum.
public static class NativeKeyState
{
    public static bool IsPressed(byte[] state, int control)
    {
        if (control == 1) return state[225] != 0 || state[229] != 0;
        if (control == 2) return state[226] != 0 || state[230] != 0;
        if (control == 3) return state[227] != 0 || state[231] != 0;
        if (control == 4) return state[224] != 0 || state[228] != 0;
        int usage = Usage(control);
        return usage > 0 && state[usage] != 0;
    }

    static int Usage(int key)
    {
        if (key >= 36 && key <= 61) return key - 36 + 4; // A-Z
        if (key >= 14 && key <= 25) return key - 14 + 58; // F1-F12
        if (key >= 27 && key <= 35) return key - 27 + 30; // 1-9
        if (key >= 88 && key <= 96) return key - 88 + 89; // keypad 1-9
        if (key >= 106 && key <= 108) return key - 106 + 104; // F13-F15
        switch (key)
        {
            case 5: return 225; case 6: return 226; case 7: return 227; case 8: return 224;
            case 9: return 229; case 10: return 230; case 11: return 231; case 12: return 228;
            case 13: return 41; case 26: return 39;
            case 62: case 112: return 53;
            case 63: case 122: return 45; case 64: case 123: return 46;
            case 65: return 42; case 66: return 43;
            case 67: case 124: return 47; case 68: case 125: return 48;
            case 69: case 126: return 49; case 70: case 127: return 51;
            case 71: case 128: return 52; case 72: return 40;
            case 73: case 129: return 54; case 74: case 130: return 55;
            case 75: case 131: return 56; case 76: return 44;
            case 77: return 73; case 78: return 76; case 79: return 74;
            case 80: return 77; case 81: return 75; case 82: return 78;
            case 83: return 80; case 84: return 79; case 85: return 82; case 86: return 81;
            case 87: return 98; case 97: case 104: return 83;
            case 98: return 84; case 99: return 85; case 100: return 86;
            case 101: return 87; case 102: return 88; case 103: return 99; case 105: return 103;
            case 109: return 230; case 110: return 57;
            case 111: return 30; case 113: return 31; case 114: return 32; case 115: return 33;
            case 116: return 34; case 117: return 35; case 118: return 36; case 119: return 37;
            case 120: return 38; case 121: return 39;
            default: return 0;
        }
    }
}
