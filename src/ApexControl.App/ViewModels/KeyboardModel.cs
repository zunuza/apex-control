using System.Collections.ObjectModel;
using System.Windows.Media;
using ApexControl.App.Infrastructure;
using ApexControl.Core;

namespace ApexControl.App.ViewModels;

// One key on the on-screen keyboard, shared by the Macros and Actuation tabs. A disabled key is drawn dim and can't be picked.
// A highlighted key is one that has something set on it (a macro, an individual actuation value); Line2 is the small
// second line of text (e.g. "macro" or "1.5").
public sealed class KeyCapViewModel : ObservableObject
{
    private static readonly Brush FillOff = Frozen("#08FFFFFF"), FillNormal = Frozen("#1CFFFFFF"), FillHigh = Frozen("#33FFFFFF"), FillSel = Frozen("#EDEAE5");
    private static readonly Brush LineOff = Frozen("#12FFFFFF"), LineNormal = Frozen("#2EFFFFFF"), LineHigh = Frozen("#D6D3CD"), LineSel = Frozen("#FFFFFF");
    private static readonly Brush TextOff = Frozen("#5A5A60"), TextNormal = Frozen("#EFEDE9"), TextHigh = Frozen("#F5F3EF"), TextSel = Frozen("#111113");
    private static readonly Brush SubNormal = Frozen("#9D9DA3"), SubHigh = Frozen("#D6D3CD");

    private readonly string _disabledTip;
    private bool _selected, _highlight;
    private string _line2 = "";

    public KeyCapViewModel(string label, byte? hid, double width, bool enabled, string disabledTip = "")
    {
        Label = label;
        Hid = hid;
        Width = width;
        Enabled = hid is not null && enabled;
        _disabledTip = disabledTip;
    }

    public string Label { get; }
    public byte? Hid { get; }
    public double Width { get; }
    public bool IsSpacer => Hid is null;
    public bool Enabled { get; }

    public bool Selected { get => _selected; set { if (Set(ref _selected, value)) RaiseLook(); } }
    public bool Highlight => _highlight;
    public string Line2 => _line2;

    // Older names used by the Actuation tab's logic and tests.
    public bool Adjustable => Enabled;
    public string MmText => _line2;
    public bool Overridden => _highlight;

    public Brush Fill => !Enabled ? FillOff : Selected ? FillSel : _highlight ? FillHigh : FillNormal;
    public Brush Line => !Enabled ? LineOff : Selected ? LineSel : _highlight ? LineHigh : LineNormal;
    public Brush LabelBrush => !Enabled ? TextOff : Selected ? TextSel : _highlight ? TextHigh : TextNormal;
    public Brush SubBrush => !Enabled ? TextOff : Selected ? TextSel : _highlight ? SubHigh : SubNormal;
    public string Tip => IsSpacer ? "" : Enabled ? KeyNames.Name(Hid!.Value) : $"{Label}: {_disabledTip}";

    public void Show(string line2, bool highlight)
    {
        _line2 = Enabled ? line2 : "";
        _highlight = Enabled && highlight;
        Raise(nameof(Line2)); Raise(nameof(MmText)); Raise(nameof(Highlight)); Raise(nameof(Overridden));
        RaiseLook();
    }

    private void RaiseLook()
    {
        Raise(nameof(Fill)); Raise(nameof(Line)); Raise(nameof(LabelBrush)); Raise(nameof(SubBrush));
    }

    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}

// Lays out a US TKL from key caps. `enabled` says which keys can be picked on this tab.
public static class KeyboardBuilder
{
    public const double KeyUnit = 40;

    public static (ObservableCollection<ObservableCollection<KeyCapViewModel>> Rows, List<KeyCapViewModel> Keys) Build(Func<byte, bool> enabled, string disabledTip)
    {
        KeyCapViewModel K(string label, byte hid, double units = 1) => new(label, hid, units * KeyUnit - 4, enabled(hid), disabledTip);
        KeyCapViewModel L(string letter) => K(letter, KeyNames.TryParse(letter, out byte h) ? h : (byte)0);
        KeyCapViewModel Gap(double units) => new("", null, units * KeyUnit - 4, false);
        KeyCapViewModel[] Fs(int from, int to) => Enumerable.Range(from, to - from + 1).Select(n => K("F" + n, (byte)(0x3A + n - 1))).ToArray();

        var rows = new List<KeyCapViewModel[]>
        {
            new[] { K("Esc", 0x29), Gap(1) }.Concat(Fs(1, 4)).Append(Gap(0.5)).Concat(Fs(5, 8)).Append(Gap(0.5)).Concat(Fs(9, 12))
                .Concat(new[] { Gap(0.5), K("PrtSc", 0x46), K("ScrLk", 0x47), K("Pause", 0x48) }).ToArray(),
            new[] { K("`", 0x35) }.Concat("1234567890".Select(c => L(c.ToString()))).Concat(new[] { K("-", 0x2D), K("=", 0x2E), K("Bksp", 0x2A, 2), Gap(0.5), K("Ins", 0x49), K("Home", 0x4A), K("PgUp", 0x4B) }).ToArray(),
            new[] { K("Tab", 0x2B, 1.5) }.Concat("QWERTYUIOP".Select(c => L(c.ToString()))).Concat(new[] { K("[", 0x2F), K("]", 0x30), K("\\", 0x31, 1.5), Gap(0.5), K("Del", 0x4C), K("End", 0x4D), K("PgDn", 0x4E) }).ToArray(),
            new[] { K("Caps", 0x39, 1.75) }.Concat("ASDFGHJKL".Select(c => L(c.ToString()))).Concat(new[] { K(";", 0x33), K("'", 0x34), K("Enter", 0x28, 2.25) }).ToArray(),
            new[] { K("Shift", 0xE1, 2.25) }.Concat("ZXCVBNM".Select(c => L(c.ToString()))).Concat(new[] { K(",", 0x36), K(".", 0x37), K("/", 0x38), K("Shift", 0xE5, 2.75), Gap(1.5), K("↑", 0x52) }).ToArray(),
            new[] { K("Ctrl", 0xE0, 1.25), K("Win", 0xE3, 1.25), K("Alt", 0xE2, 1.25), K("Space", 0x2C, 6.25), K("Alt", 0xE6, 1.25), K("Win", 0xE7, 1.25), Gap(1.25), K("Ctrl", 0xE4, 1.25), Gap(0.5), K("←", 0x50), K("↓", 0x51), K("→", 0x4F) },
        };

        var all = new List<KeyCapViewModel>();
        var result = new ObservableCollection<ObservableCollection<KeyCapViewModel>>();
        foreach (KeyCapViewModel[] row in rows)
        {
            result.Add(new ObservableCollection<KeyCapViewModel>(row));
            all.AddRange(row);
        }
        return (result, all);
    }
}
