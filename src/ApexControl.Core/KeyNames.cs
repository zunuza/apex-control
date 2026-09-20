namespace ApexControl.Core;

public static class KeyNames
{
    private static readonly Dictionary<string, byte> Map = Build();

    public static bool TryParse(string name, out byte hid) => Map.TryGetValue(name, out hid);

    // Every name we know, in the order they were defined (letters, digits, F-keys, then the named keys): for pickers.
    public static IReadOnlyList<string> OrderedNames { get; } = Map.Keys.Select(k => k.ToUpperInvariant()).ToList();

    public static string Name(byte hid) => Map.FirstOrDefault(kv => kv.Value == hid).Key ?? $"0x{hid:X2}";

    private static Dictionary<string, byte> Build()
    {
        var m = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 26; i++) m[((char)('A' + i)).ToString()] = (byte)(0x04 + i);
        for (int i = 1; i <= 9; i++) m[i.ToString()] = (byte)(0x1E + i - 1);
        m["0"] = 0x27;
        for (int i = 1; i <= 12; i++) m["F" + i] = (byte)(0x3A + i - 1);
        m["ENTER"] = 0x28; m["ESC"] = 0x29; m["BACKSPACE"] = 0x2A; m["TAB"] = 0x2B; m["SPACE"] = 0x2C;
        m["PRINTSCREEN"] = 0x46; m["SCROLLLOCK"] = 0x47; m["PAUSE"] = 0x48; m["INSERT"] = 0x49; m["HOME"] = 0x4A; m["PAGEUP"] = 0x4B; m["DELETE"] = 0x4C; m["END"] = 0x4D; m["PAGEDOWN"] = 0x4E;
        m["MINUS"] = 0x2D; m["EQUALS"] = 0x2E; m["LBRACKET"] = 0x2F; m["RBRACKET"] = 0x30; m["BACKSLASH"] = 0x31;
        m["SEMICOLON"] = 0x33; m["QUOTE"] = 0x34; m["GRAVE"] = 0x35; m["COMMA"] = 0x36; m["PERIOD"] = 0x37; m["SLASH"] = 0x38; m["CAPSLOCK"] = 0x39;
        m["LCTRL"] = 0xE0; m["LSHIFT"] = 0xE1; m["LALT"] = 0xE2; m["LWIN"] = 0xE3; m["RCTRL"] = 0xE4; m["RSHIFT"] = 0xE5; m["RALT"] = 0xE6; m["RWIN"] = 0xE7;
        m["RIGHT"] = 0x4F; m["LEFT"] = 0x50; m["DOWN"] = 0x51; m["UP"] = 0x52;
        return m;
    }
}
