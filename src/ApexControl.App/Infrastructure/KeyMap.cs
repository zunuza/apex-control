using System.Windows.Input;

namespace ApexControl.App.Infrastructure;

// WPF key -> USB HID keyboard usage code (the codes the keyboard's macros use). Only keys the macro editor knows by
// name are here, so a recorded key can always be shown, edited and written. Numpad keys are not included.
public static class KeyMap
{
    private static readonly Dictionary<Key, byte> Map = Build();

    public static bool TryGetHid(Key key, out byte hid) => Map.TryGetValue(key, out hid);

    public static IReadOnlyDictionary<Key, byte> All => Map;

    private static Dictionary<Key, byte> Build()
    {
        var m = new Dictionary<Key, byte>();
        for (int i = 0; i < 26; i++) m[Key.A + i] = (byte)(0x04 + i);
        for (int i = 0; i < 9; i++) m[Key.D1 + i] = (byte)(0x1E + i);
        m[Key.D0] = 0x27;
        for (int i = 0; i < 12; i++) m[Key.F1 + i] = (byte)(0x3A + i);

        m[Key.Return] = 0x28; m[Key.Escape] = 0x29; m[Key.Back] = 0x2A; m[Key.Tab] = 0x2B; m[Key.Space] = 0x2C;
        m[Key.OemMinus] = 0x2D; m[Key.OemPlus] = 0x2E; m[Key.OemOpenBrackets] = 0x2F; m[Key.OemCloseBrackets] = 0x30;
        m[Key.OemPipe] = 0x31; m[Key.OemSemicolon] = 0x33; m[Key.OemQuotes] = 0x34; m[Key.OemTilde] = 0x35;
        m[Key.OemComma] = 0x36; m[Key.OemPeriod] = 0x37; m[Key.OemQuestion] = 0x38; m[Key.CapsLock] = 0x39;

        m[Key.PrintScreen] = 0x46; m[Key.Scroll] = 0x47; m[Key.Pause] = 0x48;
        m[Key.Insert] = 0x49; m[Key.Home] = 0x4A; m[Key.PageUp] = 0x4B; m[Key.Delete] = 0x4C; m[Key.End] = 0x4D; m[Key.PageDown] = 0x4E;
        m[Key.Right] = 0x4F; m[Key.Left] = 0x50; m[Key.Down] = 0x51; m[Key.Up] = 0x52;

        m[Key.LeftCtrl] = 0xE0; m[Key.LeftShift] = 0xE1; m[Key.LeftAlt] = 0xE2; m[Key.LWin] = 0xE3;
        m[Key.RightCtrl] = 0xE4; m[Key.RightShift] = 0xE5; m[Key.RightAlt] = 0xE6; m[Key.RWin] = 0xE7;
        return m;
    }
}
