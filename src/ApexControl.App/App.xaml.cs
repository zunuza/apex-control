using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApexControl.App.Backend;
using ApexControl.App.Infrastructure;
using ApexControl.App.ViewModels;
using ApexControl.Core;

namespace ApexControl.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Development aid: `ApexControl.exe --preview <folder> [gg|nokb]` renders every tab to a PNG using canned
        // data (no hardware is touched) and exits.
        int pi = Array.IndexOf(e.Args, "--preview");
        if (pi >= 0 && pi + 1 < e.Args.Length)
        {
            string mode = pi + 2 < e.Args.Length ? e.Args[pi + 2] : "";
            RenderPreview(e.Args[pi + 1], mode);
            Shutdown(0);
            return;
        }

        // Development aid: `ApexControl.exe --selftest <file>` asks the real backend what it can see (keyboard
        // presence, GG, existing backups) and writes it to a text file, then exits. Sends nothing to the keyboard.
        int si = Array.IndexOf(e.Args, "--selftest");
        if (si >= 0 && si + 1 < e.Args.Length)
        {
            var real = new RealBackend();
            var st = real.GetState();
            var lines = new List<string>
            {
                $"keyboard present: {st.Present}",
                $"GG running: {st.GgRunning} ({string.Join(", ", st.GgNames)})",
                $"backups root: {real.BackupsRoot}",
                $"backups found: {real.ListBackups().Count}",
            };
            for (int slot = 1; slot <= 5; slot++)
                lines.Add($"slot {slot} latest summary: {real.LatestSummary(slot)?.ToString() ?? "(none)"}");
            File.WriteAllLines(e.Args[si + 1], lines);
            Shutdown(0);
            return;
        }
        // Development aid: `ApexControl.exe --selftest-macros <file>` drives the Macros tab's logic against the canned
        // backend (no hardware) and writes what happened to a text file.
        int mi = Array.IndexOf(e.Args, "--selftest-macros");
        if (mi >= 0 && mi + 1 < e.Args.Length)
        {
            File.WriteAllLines(e.Args[mi + 1], MacrosSelfTest());
            Shutdown(0);
            return;
        }

        // Development aid: `ApexControl.exe --selftest-actuation <file>`: the same for the Actuation tab.
        int ri = Array.IndexOf(e.Args, "--selftest-recorder");
        if (ri >= 0 && ri + 1 < e.Args.Length)
        {
            File.WriteAllLines(e.Args[ri + 1], RecorderSelfTest());
            Shutdown(0);
            return;
        }

        int di = Array.IndexOf(e.Args, "--selftest-socd");
        if (di >= 0 && di + 1 < e.Args.Length)
        {
            File.WriteAllLines(e.Args[di + 1], SocdSelfTest());
            Shutdown(0);
            return;
        }

        int ci = Array.IndexOf(e.Args, "--selftest-compat");
        if (ci >= 0 && ci + 1 < e.Args.Length)
        {
            File.WriteAllLines(e.Args[ci + 1], CompatSelfTest());
            Shutdown(0);
            return;
        }

        int ai = Array.IndexOf(e.Args, "--selftest-actuation");
        if (ai >= 0 && ai + 1 < e.Args.Length)
        {
            File.WriteAllLines(e.Args[ai + 1], ActuationSelfTest());
            Shutdown(0);
            return;
        }

        // Development aid: `ApexControl.exe --selftest-profiles <file>`: profile nicknames.
        int qi = Array.IndexOf(e.Args, "--selftest-profiles");
        if (qi >= 0 && qi + 1 < e.Args.Length)
        {
            File.WriteAllLines(e.Args[qi + 1], ProfilesSelfTest());
            Shutdown(0);
            return;
        }

        // Development aid: `ApexControl.exe --selftest-autostart <file>`: Start with Windows (uses a throwaway registry value, cleans up).
        int ui = Array.IndexOf(e.Args, "--selftest-autostart");
        if (ui >= 0 && ui + 1 < e.Args.Length)
        {
            File.WriteAllLines(e.Args[ui + 1], AutoStartSelfTest());
            Shutdown(0);
            return;
        }

        // Development aid: `ApexControl.exe --selftest-tray <file>`: close-to-tray and single-instance behaviour.
        int ti = Array.IndexOf(e.Args, "--selftest-tray");
        if (ti >= 0 && ti + 1 < e.Args.Length)
        {
            File.WriteAllLines(e.Args[ti + 1], TraySelfTest());
            Shutdown(0);
            return;
        }

        // Only one copy at a time: a second launch just brings the first one's window back.
        _single = SingleInstance.Acquire("ApexControl.SingleInstance", out bool first);
        if (!first)
        {
            _single.SignalFirst();
            _single.Dispose();
            Shutdown(0);
            return;
        }

        var vm = new MainViewModel(new RealBackend(), Confirm, ConfirmDetailed, autoRefresh: true);
        // "--tray" (used by Start with Windows) starts hidden in the notification area.
        bool startHidden = e.Args.Contains("--tray");
        var window = new MainWindow(vm);
        if (!startHidden) window.Show();
        _tray = new TrayHost(window, vm, startHidden);
        _single.ListenForSecondLaunch(() => Dispatcher.BeginInvoke(() => _tray?.ShowWindow()));
    }

    private SingleInstance? _single;
    private TrayHost? _tray;

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    // Plain yes/no questions use the same styled dialog (Cancel is the default button).
    // The dialog belongs to whichever window is in front (the main window, or the macro editor pop-up).
    private static Window? ActiveWindow() => Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Current.MainWindow;

    private static bool Confirm(string text, string caption) =>
        new ConfirmWriteWindow(caption, text, "") { Owner = ActiveWindow() }.ShowDialog() == true;

    private static bool ConfirmDetailed(string caption, string summary, string details) =>
        new ConfirmWriteWindow(caption, summary, details) { Owner = ActiveWindow() }.ShowDialog() == true;

    private static void RenderPreview(string folder, string mode)
    {
        Directory.CreateDirectory(folder);
        var backend = new PreviewBackend(ggRunning: mode == "gg", present: mode != "nokb");
        var vm = new MainViewModel(backend, (_, _) => false, (_, _, _) => false, autoRefresh: false);
        var window = new MainWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -6000,
            Top = 0,
            ShowInTaskbar = false,
        };
        window.Show();

        // Macros tab: read the made-up slot 1, then select F9 so the editor is filled from it.
        string prefix = mode == "" ? "normal" : mode;
        Flush(window);
        vm.Macros.LoadCommand.Execute(null);
        Flush(window);
        if (vm.Macros.Rows.Count > 0)
        {
            vm.Macros.SelectKeyCommand.Execute(vm.Macros.KeyCaps.First(k => k.Label == "F11"));   // an empty key, so the editor starts fresh
            vm.Macros.ClearEventsCommand.Execute(null);   // show GG's editor example: Q down, 300, Q up, 300, W down, 300, W up
            foreach (var kind in new[] { "Key down", "Wait", "Key up", "Wait", "Key down", "Wait", "Key up" })
            {
                if (kind == "Key down") vm.Macros.AddDownCommand.Execute(null);
                else if (kind == "Key up") vm.Macros.AddUpCommand.Execute(null);
                else vm.Macros.AddWaitCommand.Execute(null);
            }
            foreach (int i in new[] { 1, 3, 5 }) vm.Macros.Events[i].MsText = "300";
            vm.Macros.Events[4].Key = "W"; vm.Macros.Events[6].Key = "W";
        }

        vm.SocdTab.LoadCommand.Execute(null);
        Flush(window);
        vm.SocdTab.AddPairCommand.Execute(null);
        Flush(window);

        // Actuation tab: pick W, A, S, D so the selection state shows.
        foreach (string label in new[] { "W", "A", "S", "D" })
            vm.ActuationTab.ToggleKeyCommand.Execute(vm.ActuationTab.Keys.First(k => k.Label == label));

        string[] names = { "macros", "actuation", "socd" };
        for (int i = 0; i < names.Length; i++)
        {
            window.TabControl.SelectedIndex = i;
            Flush(window);
            Save(window, Path.Combine(folder, $"{prefix}-{names[i]}.png"));
        }

        // The confirm dialog for "bind F11 -> Q" against the made-up slot.
        if (mode == "")
        {
            var read = backend.ReadSlotAsync(1, NullSink.Instance).Result;
            var plan = MacroPlanner.Plan(read.Region02!, read.Region03!, MacroEditRequest.ForSteps(0x44, new[] { new MacroStep(new byte[] { 0x14 }, 300, 300), new MacroStep(new byte[] { 0x1A }, 300, 0) }));
            var dialog = new ConfirmWriteWindow("Write macro", plan.Headline + "\n\nThis overwrites slot 1 (Config 1) on the keyboard. The current slot 1 is saved to your backups first, the write is read back to check it, and slots 2-5 are not touched.", PlanFormatter.Describe(plan))
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -6000,
                Top = 0,
                ShowInTaskbar = false,
            };
            dialog.Show();
            Flush(dialog);
            Save(dialog, Path.Combine(folder, "confirm-dialog.png"));
            dialog.Close();

            var picker = new RestoreWindow(vm)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -6000,
                Top = 0,
                ShowInTaskbar = false,
            };
            picker.Show();
            Flush(picker);
            Save(picker, Path.Combine(folder, "restore-dialog.png"));
            picker.Close();

            // The macro editor pop-up: first with a recorded macro (idle), then mid-recording.
            var m = vm.Macros;
            if (!m.KeyCaps.First(k => k.Label == "F11").Selected) m.SelectKeyCommand.Execute(m.KeyCaps.First(k => k.Label == "F11"));
            m.StartRecordingCommand.Execute(null);
            TimeSpan t = TimeSpan.Zero;
            foreach (var (hid, down, gap) in new (byte, bool, int)[] { (0x1A, true, 0), (0x1A, false, 140), (0x14, true, 260), (0x08, true, 40), (0x14, false, 120), (0x08, false, 20) })
            {
                t += TimeSpan.FromMilliseconds(gap);
                m.RecordKey(hid, down, t);
            }
            var editor = new MacroEditorWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Top = 0, ShowInTaskbar = false };
            editor.Show();
            Flush(editor);
            Save(editor, Path.Combine(folder, "macro-editor-recording.png"));
            m.StopRecording();
            Flush(editor);
            Save(editor, Path.Combine(folder, "macro-editor.png"));
            editor.Close();

            // The compatibility window after a (made-up) read check; this view model answers yes to the read question.
            var vm2 = new MainViewModel(backend, (_, _) => false, (_, _, _) => true, autoRefresh: false);
            vm2.OpenCompatibility();
            vm2.Compatibility!.ReadCommand.Execute(null);
            Flush(window);
            var compat = new CompatibilityWindow(vm2) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Top = 0, ShowInTaskbar = false };
            compat.Show();
            Flush(compat);
            Save(compat, Path.Combine(folder, "compatibility.png"));
            compat.Close();
        }
        window.Close();
    }

    private static List<string> MacrosSelfTest()
    {
        var log = new List<string>();
        var confirms = new List<string>();
        bool answer = true;
        var backend = new PreviewBackend();
        var vm = new MainViewModel(backend, (_, _) => false, (cap, sum, det) => { confirms.Add(cap + " | " + sum.Split('\n')[0]); return answer; }, autoRefresh: false);
        var m = vm.Macros;
        var host = new Window { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Width = 10, Height = 10 };
        host.Show();

        void Pump() { for (int i = 0; i < 20; i++) { Flush(host); System.Threading.Thread.Sleep(10); } }
        void Note(string label, bool ok) => log.Add($"[{(ok ? "PASS" : "FAIL")}] {label}");
        string Rows() => string.Join("; ", m.Rows.Select(r => $"{r.KeyName}={r.Kind}"));
        string Timeline() => string.Join(", ", m.Events.Select(e => e.IsWait ? $"wait {e.MsText}" : $"{e.Key} {(e.KindName == EventViewModel.DownText ? "down" : "up")}"));

        Note("before loading: editing is disabled (nothing to edit yet)", !m.HasLoaded && !m.CanApply && !m.CanRemove);
        Note("the editor starts with a key tap: Q down, wait 100, Q up", Timeline() == "Q down, wait 100, Q up");
        m.LoadCommand.Execute(null); Pump();
        Note($"load: 2 macros shown ({Rows()})", m.HasLoaded && m.Rows.Count == 2 && m.Rows[0].KeyName == "F9" && m.Rows[1].KeyName == "F10");
        Note("  F10 is recognised as an editable macro: W+Q, 141 ms", m.Rows[1].IsEditable && m.Rows[1].Kind == "Press W+Q, hold 141 ms");

        m.SelectedRow = m.Rows[1]; Pump();
        Note($"select F10: the timeline loads ({Timeline()}), button says Replace",
            m.EditorKey == "F10" && Timeline() == "W down, Q down, wait 141, W up, Q up" && m.ApplyText == "Replace macro on F10...");

        m.EditorKey = "F11"; Pump();
        Note("change key to F11: button says Add macro to F11", m.ApplyText == "Add macro to F11..." && m.CanApply);

        // Build GG's editor example on F11: Q down, 300, Q up, 300, W down, 300, W up.
        m.ClearEventsCommand.Execute(null); Pump();
        Note("clear all: empty timeline, apply disabled with a message", m.Events.Count == 0 && !m.CanApply && m.ValidationMessage is not null);
        m.AddDownCommand.Execute(null); m.AddWaitCommand.Execute(null);
        Note("add key down + wait: a key that is never released is refused (message mentions Key up)", !m.CanApply && (m.ValidationMessage ?? "").Contains("Key up"));
        m.AddUpCommand.Execute(null);
        Note("add key up: the new 'Key up' defaults to the key still held (Q); timeline valid", m.Events[2].Key == "Q" && m.CanApply && m.ValidationMessage is null);
        m.AddWaitCommand.Execute(null); m.AddDownCommand.Execute(null); m.AddWaitCommand.Execute(null); m.AddUpCommand.Execute(null);
        m.Events[1].MsText = "300"; m.Events[3].MsText = "300"; m.Events[5].MsText = "300"; m.Events[4].Key = "W"; m.Events[6].Key = "W";
        Note($"timeline now {Timeline()}", Timeline() == "Q down, wait 300, Q up, wait 300, W down, wait 300, W up" && m.CanApply);
        Note("  summary: " + m.Summary, m.Summary == "Reads as: press Q (hold 300 ms), wait 300 ms, press W (hold 300 ms)");

        m.Events[1].MsText = "0";
        Note("wait 0 ms: apply disabled", !m.CanApply && (m.ValidationMessage ?? "").Contains("wait"));
        m.Events[1].MsText = "abc";
        Note("wait 'abc': apply disabled", !m.CanApply);
        m.Events[1].MsText = "300";
        m.Events[6].Key = "Q";
        Note("W is pressed but Q is released twice: refused", !m.CanApply);
        m.Events[6].Key = "W";

        // Reordering and removing.
        var first = m.Events[0];
        m.MoveDownCommand.Execute(first); Pump();
        Note("move 'Q down' one place later: it swaps with the wait (a leading wait, then a tap with no hold) - still valid", m.Events[1] == first && m.CanApply);
        m.MoveUpCommand.Execute(first); Pump();
        var qUp = m.Events[2];
        m.MoveUpCommand.Execute(qUp); m.MoveUpCommand.Execute(qUp); Pump();
        Note("move 'Q up' two places earlier so it comes before 'Q down': refused", m.Events[0] == qUp && !m.CanApply);
        m.MoveDownCommand.Execute(qUp); m.MoveDownCommand.Execute(qUp); Pump();
        Note("move it back: valid again, numbering follows the order", m.Events[0] == first && m.Events[0].Number == 1 && m.Events[6].Number == 7 && m.CanApply);
        m.Events[6].KindName = EventViewModel.WaitText;
        bool refused = !m.CanApply && (m.ValidationMessage ?? "").Contains("Key up");
        m.Events[6].KindName = EventViewModel.UpText;
        Note("change event 7 from 'Key up' to 'Wait' (W never released): refused; change it back: valid", refused && m.CanApply && m.Events[6].Key == "W");

        answer = false;
        m.ApplyCommand.Execute(null); Pump();
        Note("apply + Cancel in the dialog: nothing written, still 2 macros", confirms.Count == 1 && m.Rows.Count == 2 && vm.StatusMessage.StartsWith("Cancelled"));

        answer = true;
        m.ApplyCommand.Execute(null); Pump();
        string f11 = "Press Q (hold 300 ms), wait 300 ms, press W (hold 300 ms)";
        Note($"apply + confirm: the dialog was shown ({confirms.Count} times) and the list now has 3 macros ({Rows()})", confirms.Count == 2 && m.Rows.Count == 3 && m.Rows.Any(r => r.KeyName == "F11" && r.Kind == f11));
        Note("  status: " + vm.StatusMessage, vm.StatusMessage.StartsWith("Preview"));
        Note("  Config 1 is tagged active after the write", vm.Profiles[0].SwitchedHere);

        m.ClearEventsCommand.Execute(null); Pump();
        m.SelectedRow = m.Rows.First(r => r.KeyName == "F11"); Pump();
        Note($"select the new F11 macro: its whole timeline loads back into the editor ({Timeline()})", Timeline() == "Q down, wait 300, Q up, wait 300, W down, wait 300, W up" && m.Events.Count == 7);

        m.SelectedRow = m.Rows.First(r => r.KeyName == "F9"); Pump();
        m.RemoveCommand.Execute(null); Pump();
        Note($"remove F9 + confirm: 2 macros left, F9 gone ({Rows()})", confirms.Count == 3 && confirms[2].StartsWith("Remove macro") && m.Rows.Count == 2 && m.Rows.All(r => r.KeyName != "F9"));

        m.SelectedRow = null; Pump();
        Note("nothing selected: Remove disabled", !m.CanRemove);

        // The clickable keyboard.
        KeyCapViewModel Cap(string label) => m.KeyCaps.First(k => k.Label == label);
        Note("keys with a macro show 'macro' and are highlighted (F10, F11); others are not", Cap("F10").Highlight && Cap("F10").Line2 == "macro" && Cap("F11").Highlight && !Cap("F9").Highlight && !Cap("Q").Highlight);
        m.SelectKeyCommand.Execute(Cap("F10")); Pump();
        Note("click F10: its timeline loads, the key is marked selected, Remove is available, the title names its macro",
            m.SelectedRow?.KeyName == "F10" && m.EditorKey == "F10" && Cap("F10").Selected && Timeline() == "W down, Q down, wait 141, W up, Q up" && m.CanRemove && m.EditorTitle == "F10  -  Press W+Q, hold 141 ms");
        m.SelectKeyCommand.Execute(Cap("Q")); Pump();
        Note("click Q (no macro): the editor starts a fresh tap, nothing to remove, the title says so, F10 is no longer selected",
            m.SelectedRow is null && m.EditorKey == "Q" && Timeline() == "Q down, wait 100, Q up" && !m.CanRemove && Cap("Q").Selected && !Cap("F10").Selected && m.EditorTitle.StartsWith("Q  -  no macro yet"));
        Note("  the button now says Add macro to Q", m.ApplyText == "Add macro to Q...");
        m.SelectKeyCommand.Execute(Cap("PrtSc")); Pump();
        Note("PrtSc, ScrLk and Pause can carry macros too (they are named now)", m.EditorKey == "PRINTSCREEN" && Cap("Pause").Enabled && Cap("ScrLk").Enabled);
        m.SelectKeyCommand.Execute(Cap("PrtSc")); Pump();
        Note("click the picked key again: it is un-selected, the editor goes back to 'click a key', nothing to remove",
            m.EditorKey == "" && !Cap("PrtSc").Selected && m.SelectedRow is null && !m.CanRemove && !m.CanApply && m.EditorTitle.StartsWith("Click a key above") && Timeline() == "Q down, wait 100, Q up");
        m.SelectKeyCommand.Execute(Cap("F10")); Pump();
        m.SelectKeyCommand.Execute(Cap("F10")); Pump();
        Note("same for a key that has a macro: second click un-selects it (the macro is untouched on the keyboard and still glows)",
            m.EditorKey == "" && !Cap("F10").Selected && Cap("F10").Highlight && m.SelectedRow is null && !m.CanRemove);
        m.SelectKeyCommand.Execute(Cap("F10")); Pump();
        Note("...and a third click picks it again", m.SelectedRow?.KeyName == "F10" && Cap("F10").Selected);
        host.Close();
        return log;
    }
    private static List<string> ActuationSelfTest()
    {
        var log = new List<string>();
        var backend = new PreviewBackend();
        var vm = new MainViewModel(backend, (_, _) => false, (_, _, _) => false, autoRefresh: false);
        var a = vm.ActuationTab;
        var host = new Window { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Width = 10, Height = 10 };
        host.Show();

        void Pump() { for (int i = 0; i < 20; i++) { Flush(host); System.Threading.Thread.Sleep(10); } }
        void Note(string label, bool ok) => log.Add($"[{(ok ? "PASS" : "FAIL")}] {label}");
        KeyCapViewModel Key(string label) => a.Keys.First(k => k.Label == label);
        int IndexOfMm(double mm) => Actuation.KnownGoodValues.Select((v, i) => (v, i)).First(x => Math.Abs(x.v.Mm - mm) < 0.0005).i;

        // The offered depths.
        var offered = Actuation.KnownGoodValues.Select(v => v.Mm).ToList();
        Note($"slider offers {offered.Count} depths (0.1-3.1, 3.3-3.6, 4.0) and none of 3.2 / 3.7 / 3.8 / 3.9", a.MaxIndex == offered.Count - 1 && offered.Count == 36 && !offered.Any(m => Math.Abs(m - 3.2) < 0.001 || m is 3.7 or 3.8 or 3.9));

        // Starting state comes from what the app last sent (canned: 2.0 mm, S 0.1, W/A/D 1.5).
        Note("starts at the last-sent values: 2.0 mm for every key, S 0.1, W/A/D 1.5", a.GlobalText == "2.0 mm" && Key("S").MmText == "0.1" && Key("W").MmText == "1.5" && Key("Q").MmText == "2.0");
        Note("  state says it matches what was last sent; overridden keys are marked", a.StateText == "Matches what was last sent." && Key("S").Overridden && !Key("Q").Overridden);
        Note("  summary: " + a.Summary, a.Summary == "Will send: 2.0 mm for every key, except S at 0.1 mm; A, D, W at 1.5 mm.");

        // The keyboard drawing.
        int adjustable = a.Keys.Count(k => k.Adjustable);
        Note($"keyboard has {a.Keys.Count(k => !k.IsSpacer)} keys, {adjustable} of them adjustable; Esc, F1, Insert and the arrows are greyed",
            new[] { "Esc", "F1", "F12", "Ins", "\u2191", "\u2190", "PgDn" }.All(l => !Key(l).Adjustable) && Key("Q").Adjustable && Key("Space").Adjustable && Key("Enter").Adjustable);
        a.ToggleKeyCommand.Execute(Key("Esc"));
        Note("clicking a greyed key does nothing", !Key("Esc").Selected && a.SelectedCount == 0);

        // Global slider.
        a.GlobalIndex = IndexOfMm(4.0);
        Note("global to 4.0 mm: plain keys follow (Q 4.0), individual keys keep theirs (S 0.1); state says changes are not sent", Key("Q").MmText == "4.0" && Key("S").MmText == "0.1" && a.StateText.StartsWith("Changes NOT sent yet"));

        // Selecting keys and giving them a value.
        a.ToggleKeyCommand.Execute(Key("Q")); a.ToggleKeyCommand.Execute(Key("E"));
        a.SelectionIndex = IndexOfMm(0.6);
        Note("select Q and E, slider 0.6: the button reads \"Set 2 selected keys to 0.6 mm\"", a.SelectedCount == 2 && a.SetSelectedText == "Set 2 selected keys to 0.6 mm");
        a.SetSelectedCommand.Execute(null);
        Note("set them: Q and E show 0.6 and are marked", Key("Q").MmText == "0.6" && Key("E").MmText == "0.6" && Key("Q").Overridden);
        a.SelectionIndex = IndexOfMm(4.0);
        a.SetSelectedCommand.Execute(null);
        Note("set them to the same value as 'all keys' (4.0): they stop being individual values", !Key("Q").Overridden && Key("Q").MmText == "4.0");
        a.SelectNoneCommand.Execute(null);
        Note("select none", a.SelectedCount == 0 && !a.SetSelectedCommand.CanExecute(null));

        a.ToggleKeyCommand.Execute(Key("W"));
        Note("selecting W (which has 1.5) puts the depth slider at 1.5", a.SelectionText == "1.5 mm");
        a.ClearSelectedCommand.Execute(null);
        Note("'selected keys back to the all-keys value': W follows the global 4.0 again", Key("W").MmText == "4.0" && !Key("W").Overridden);
        a.SelectNoneCommand.Execute(null);

        // Sending.
        a.ApplyCommand.Execute(null); Pump();
        var sent = backend.ActuationRequests.LastOrDefault();
        Note("send: the backend got global 4.0 mm and exactly S 0.1, A 1.5, D 1.5",
            backend.ActuationRequests.Count == 1 && Math.Abs(sent.GlobalMm - 4.0) < 0.001 && sent.PerKeyMm.Count == 3
            && Math.Abs(sent.PerKeyMm[0x16] - 0.1) < 0.001 && Math.Abs(sent.PerKeyMm[0x04] - 1.5) < 0.001 && Math.Abs(sent.PerKeyMm[0x07] - 1.5) < 0.001);
        Note("  afterwards the state says it matches what was last sent", a.StateText == "Matches what was last sent.");

        // The frame the real backend builds from those numbers.
        byte[] frame = Actuation.BuildFrameMm(sent.GlobalMm, sent.PerKeyMm);
        byte[] expected = Actuation.BuildFrame(54489, new Dictionary<byte, ushort> { [0x16] = 1542, [0x04] = 8998, [0x07] = 8998 });
        Note("frame for (4.0 mm, S 0.1, A 1.5, D 1.5) == BuildFrame with GG's raw values 0xD4D9 / 0x0606 / 0x2326", frame.SequenceEqual(expected));
        Note("  the ISO/international keys still carry the fixed sentinel 0x1F23", Actuation.SentinelKeys.All(k => { int at = 2 + Actuation.KeyCodes.ToList().IndexOf(k) * 3; return frame[at] == k && frame[at + 1] == 0x23 && frame[at + 2] == 0x1F; }));
        Note("  the console tool's `4.0 S=0.1` frame equals ours", Actuation.BuildFrameMm(4.0, new Dictionary<byte, double> { [0x16] = 0.1 }).SequenceEqual(Actuation.BuildFrame(0xD4D9, new Dictionary<byte, ushort> { [0x16] = 0x0606 })));

        // Refusals: nothing that GG was not seen sending.
        bool Throws(Action act) { try { act(); return false; } catch (ArgumentException) { return true; } }
        Note("refuses 3.2 mm, 4.05 mm, 3.7 mm (global and per key) and the non-adjustable Esc key",
            Throws(() => Actuation.BuildFrameMm(3.2)) && Throws(() => Actuation.BuildFrameMm(4.05)) && Throws(() => Actuation.BuildFrameMm(2.0, new Dictionary<byte, double> { [0x04] = 3.7 }))
            && Throws(() => Actuation.BuildFrameMm(2.0, new Dictionary<byte, double> { [0x29] = 1.0 })) && Throws(() => Actuation.BuildFrameMm(2.0, new Dictionary<byte, double> { [0x32] = 1.0 })));

        // Bulk actions.
        a.SelectAllCommand.Execute(null);
        Note($"select all picks every adjustable key ({a.SelectedCount})", a.SelectedCount == adjustable);
        a.SelectionIndex = IndexOfMm(1.0);
        a.SetSelectedCommand.Execute(null);
        Note("set all to 1.0 mm: every adjustable key shows 1.0, and the summary lists the keys", a.Keys.Where(k => k.Adjustable).All(k => k.MmText == "1.0") && a.Summary.Contains("at 1.0 mm"));
        a.SelectNoneCommand.Execute(null);
        a.ClearAllOverridesCommand.Execute(null);
        Note("clear all individual values: every key follows 'all keys' again", a.Keys.Where(k => k.Adjustable).All(k => k.MmText == "4.0") && a.Summary == "Will send: every key at 4.0 mm.");
        a.RevertCommand.Execute(null);
        Note("'back to what was last sent' restores what the send above put on the keyboard: 4.0 mm with S 0.1 and A/D 1.5 (W follows the global again)", a.GlobalText == "4.0 mm" && Key("S").MmText == "0.1" && Key("A").MmText == "1.5" && Key("W").MmText == "4.0");
        host.Close();
        return log;
    }

    private static List<string> RecorderSelfTest()
    {
        var log = new List<string>();
        void Note(string label, bool ok) => log.Add($"[{(ok ? "PASS" : "FAIL")}] {label}");
        TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

        // ---- the key map ----
        var all = KeyMap.All;
        Note($"the key map covers {all.Count} keys, each maps to a distinct HID code, and every code has a name the macro editor knows",
            all.Values.Distinct().Count() == all.Count && all.Values.All(h => !KeyNames.Name(h).StartsWith("0x")));
        Note("spot checks: A=0x04, Z=0x1D, 1=0x1E, 0=0x27, F1=0x3A, F12=0x45, Enter=0x28, Esc=0x29, Space=0x2C, / = 0x38, Caps=0x39, Left shift=0xE1, Right alt=0xE6",
            all[System.Windows.Input.Key.A] == 0x04 && all[System.Windows.Input.Key.Z] == 0x1D && all[System.Windows.Input.Key.D1] == 0x1E && all[System.Windows.Input.Key.D0] == 0x27
            && all[System.Windows.Input.Key.F1] == 0x3A && all[System.Windows.Input.Key.F12] == 0x45 && all[System.Windows.Input.Key.Return] == 0x28 && all[System.Windows.Input.Key.Escape] == 0x29
            && all[System.Windows.Input.Key.Space] == 0x2C && all[System.Windows.Input.Key.OemQuestion] == 0x38 && all[System.Windows.Input.Key.CapsLock] == 0x39
            && all[System.Windows.Input.Key.LeftShift] == 0xE1 && all[System.Windows.Input.Key.RightAlt] == 0xE6);
        Note("numpad keys are not mapped (so they are skipped, not mis-recorded)", !KeyMap.TryGetHid(System.Windows.Input.Key.NumPad5, out _));

        // ---- recording in the view model, with made-up times ----
        var backend = new PreviewBackend();
        var confirms = 0;
        var vm = new MainViewModel(backend, (_, _) => false, (_, _, _) => { confirms++; return true; }, autoRefresh: false);
        var m = vm.Macros;
        var host = new Window { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Width = 10, Height = 10 };
        host.Show();
        void Pump() { for (int i = 0; i < 20; i++) { Flush(host); System.Threading.Thread.Sleep(10); } }
        string Timeline() => string.Join(", ", m.Events.Select(e => e.IsWait ? $"wait {e.MsText}" : $"{e.Key} {(e.KindName == EventViewModel.DownText ? "down" : "up")}"));
        const byte Q = 0x14, W = 0x1A;

        m.LoadCommand.Execute(null); Pump();
        KeyCapViewModel Cap(string label) => m.KeyCaps.First(k => k.Label == label);
        m.SelectKeyCommand.Execute(Cap("F11")); Pump();

        m.StartRecordingCommand.Execute(null);
        Note("Record: the old timeline is cleared, recording is on, the timeline editing is locked", m.IsRecording && m.Events.Count == 0 && !m.IsNotRecording && !m.StartRecordingCommand.CanExecute(null) && m.StopRecordingCommand.CanExecute(null));
        m.RecordKey(Q, true, Ms(1000)); m.RecordKey(Q, false, Ms(1120)); m.RecordKey(W, true, Ms(1320)); m.RecordKey(W, false, Ms(1400));
        m.StopRecordingCommand.Execute(null);
        Note($"Q held 120 ms, pause 200 ms, W held 80 ms records as: {Timeline()}", Timeline() == "Q down, wait 120, Q up, wait 200, W down, wait 80, W up" && !m.IsRecording);
        Note("  the recording is a valid macro and can be saved as is (key F11 is picked)", m.ValidationMessage is null && m.CanApply);
        Note("  the summary reads it back", m.Summary == "Reads as: press Q (hold 120 ms), wait 200 ms, press W (hold 80 ms)");

        m.StartRecordingCommand.Execute(null);
        m.RecordKey(Q, true, Ms(0)); m.RecordKey(W, true, Ms(0)); m.RecordKey(Q, false, Ms(100)); m.RecordKey(W, false, Ms(100)); m.StopRecording();
        Note("two keys pressed together (no gap between them) record with no zero-length waits: " + Timeline(), Timeline() == "Q down, W down, wait 100, Q up, W up");

        m.StartRecordingCommand.Execute(null);
        m.RecordKey(Q, true, Ms(0)); m.RecordKey(Q, true, Ms(30)); m.RecordKey(Q, true, Ms(60)); m.RecordKey(W, false, Ms(70)); m.RecordKey(Q, false, Ms(100)); m.StopRecording();
        Note("key auto-repeat and a release we never saw the press of are ignored: " + Timeline(), Timeline() == "Q down, wait 100, Q up");

        m.StartRecordingCommand.Execute(null);
        m.RecordKey(Q, true, Ms(0)); m.RecordKey(W, true, Ms(50)); m.StopRecording();
        Note("stopping while keys are held releases them, so no key is ever left down: " + Timeline(), Timeline() == "Q down, wait 50, W down, Q up, W up" && m.ValidationMessage is null);

        m.StartRecordingCommand.Execute(null);
        m.RecordKey(Q, true, Ms(0)); m.RecordKey(Q, false, Ms(20000)); m.StopRecording();
        Note("a very long pause is capped at 5000 ms: " + Timeline(), Timeline() == "Q down, wait 5000, Q up");

        m.StartRecordingCommand.Execute(null);
        for (int i = 0; i < 40 && m.IsRecording; i++) { m.RecordKey(Q, true, Ms(i * 100)); m.RecordKey(Q, false, Ms(i * 100 + 50)); }
        Note($"recording stops by itself at the block limit ({m.Events.Count} blocks, limit {MacroEditor.MaxEvents}), the macro is valid, and it says why", !m.IsRecording && m.Events.Count <= MacroEditor.MaxEvents && m.ValidationMessage is null && m.RecordingNote.StartsWith("Stopped"));

        m.StartRecordingCommand.Execute(null); m.StopRecordingCommand.Execute(null);
        Note("Record then Stop with nothing pressed restores the default tap and says nothing was recorded", Timeline() == "Q down, wait 100, Q up" && m.RecordingNote == "Nothing was recorded.");

        // ---- editing the timings afterwards ----
        m.StartRecordingCommand.Execute(null);
        m.RecordKey(Q, true, Ms(0)); m.RecordKey(Q, false, Ms(120)); m.RecordKey(W, true, Ms(320)); m.RecordKey(W, false, Ms(400)); m.StopRecording();
        m.Events[1].MsText = "150";
        Note("a recorded wait can be edited like any other (120 -> 150)", Timeline() == "Q down, wait 150, Q up, wait 200, W down, wait 80, W up" && m.ValidationMessage is null);
        m.BulkWaitText = "60"; m.SetAllWaitsCommand.Execute(null);
        Note("'All waits' sets every wait at once", Timeline() == "Q down, wait 60, Q up, wait 60, W down, wait 60, W up");
        m.BulkWaitText = "0"; m.SetAllWaitsCommand.Execute(null);
        Note("...and refuses a bad number without changing anything", Timeline() == "Q down, wait 60, Q up, wait 60, W down, wait 60, W up" && m.RecordingNote.StartsWith("Type a whole number"));

        // ---- the tab's small panel ----
        m.SelectKeyCommand.Execute(Cap("F11")); Pump();   // un-select F11
        Note("no key picked: the Edit button is unavailable", !m.CanOpenEditor && !m.OpenEditorCommand.CanExecute(null));
        m.SelectKeyCommand.Execute(Cap("F10")); Pump();
        Note("a key with a macro: the button says 'Edit macro...' and the panel shows what it does", m.CanOpenEditor && m.EditButtonText == "Edit macro..." && m.PanelText == "W down, Q down, wait 141 ms, W up, Q up");
        m.SelectKeyCommand.Execute(Cap("Q")); Pump();
        Note("a key without one: 'Create macro...' and a hint", m.EditButtonText == "Create macro..." && m.PanelText.StartsWith("This key has no macro yet"));
        int opened = 0; m.OpenEditorRequested += () => opened++;
        m.OpenEditorCommand.Execute(null);
        Note("pressing it asks the window to open the editor pop-up", opened == 1);

        // ---- the pop-up window itself ----
        var win = new MacroEditorWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Top = 0, ShowInTaskbar = false };
        win.Show(); Pump();
        m.StartRecordingCommand.Execute(null); Pump();
        var src = System.Windows.PresentationSource.FromVisual(win);
        bool Raise(System.Windows.Input.Key key, bool down)
        {
            var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, src!, Environment.TickCount, key)
            { RoutedEvent = down ? System.Windows.Input.Keyboard.PreviewKeyDownEvent : System.Windows.Input.Keyboard.PreviewKeyUpEvent };
            win.RaiseEvent(args);
            return args.Handled;
        }
        bool handled = Raise(System.Windows.Input.Key.Q, true);
        System.Threading.Thread.Sleep(150);
        Raise(System.Windows.Input.Key.Q, false);
        System.Threading.Thread.Sleep(250);
        Raise(System.Windows.Input.Key.W, true);
        System.Threading.Thread.Sleep(100);
        Raise(System.Windows.Input.Key.W, false);
        Raise(System.Windows.Input.Key.NumPad5, true);
        bool spaceHandled = Raise(System.Windows.Input.Key.Space, true); Raise(System.Windows.Input.Key.Space, false);
        m.StopRecording();
        int wait1 = int.Parse(m.Events[1].MsText), wait2 = int.Parse(m.Events[3].MsText), wait3 = int.Parse(m.Events[5].MsText);
        Note($"real key events in the pop-up are captured with real timing: {Timeline()}",
            m.Events[0].Key == "Q" && m.Events[2].Key == "Q" && m.Events[4].Key == "W" && wait1 is >= 100 and <= 300 && wait2 is >= 200 and <= 450 && wait3 is >= 60 and <= 300);
        Note("  keys are consumed while recording (Space can't press a button), and an unmapped key (numpad 5) is skipped", handled && spaceHandled && !m.Events.Any(e => e.Key == "NUMPAD5"));
        Note("  a Space press was recorded as Space", m.Events.Any(e => e.Key == "SPACE"));

        // the pop-up closes itself after a successful save
        m.SelectKeyCommand.Execute(Cap("F11")); Pump();
        m.ApplyCommand.Execute(null); Pump();
        Note("saving from the pop-up asks for confirmation, and the pop-up closes after it succeeds (preview: nothing written)", confirms >= 1 && !win.IsLoaded);
        host.Close();
        return log;
    }

    private static List<string> SocdSelfTest()
    {
        var log = new List<string>();
        void Note(string label, bool ok) => log.Add($"[{(ok ? "PASS" : "FAIL")}] {label}");
        var backend = new PreviewBackend();
        bool answer = true;
        var dialogs = new List<(string Caption, string Details)>();
        var vm = new MainViewModel(backend, (_, _) => false, (cap, sum, det) => { dialogs.Add((cap, det)); return answer; }, autoRefresh: false);
        var s = vm.SocdTab;
        var host = new Window { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Width = 10, Height = 10 };
        host.Show();
        void Pump() { for (int i = 0; i < 20; i++) { Flush(host); System.Threading.Thread.Sleep(10); } }
        const string Last = "Last input priority", Key1 = "Key 1 priority", Key2 = "Key 2 priority";

        Note("before loading nothing can be saved", !s.HasLoaded && !s.CanApply && !s.AddPairCommand.CanExecute(null));
        s.LoadCommand.Execute(null); Pump();
        Note("load: SOCD is on with one pair, A + D, last input priority (the made-up slot)", s.HasLoaded && s.Enabled && s.Pairs.Count == 1 && s.Pairs[0].Key1 == "A" && s.Pairs[0].Key2 == "D" && s.Pairs[0].Behavior == Last);
        Note("  nothing has changed yet, so Save is unavailable and the summary says so", !s.HasChanges && !s.CanApply && s.Summary.Contains("no changes yet"));
        Note("  the behaviour drop-down offers exactly the three GG behaviours", s.BehaviorChoices.SequenceEqual(new[] { Last, Key1, Key2 }));

        s.Pairs[0].Behavior = Key1;
        Note("change the behaviour to Key 1 priority: Save becomes available and the summary reads it", s.HasChanges && s.CanApply && s.Summary == "Will write: SOCD is ON; A + D: key 1 priority");
        s.Pairs[0].Behavior = Last;
        Note("...and changing it back means no changes again", !s.HasChanges && !s.CanApply);

        s.Pairs[0].Key2 = "A";
        Note("the same key twice in a pair is refused with a message", s.ValidationMessage == "Pair 1: the two keys must be different." && !s.CanApply);
        s.Pairs[0].Key2 = "D";
        s.AddPairCommand.Execute(null);
        Note("add a pair: it picks two unused keys (W + S), last input priority", s.Pairs.Count == 2 && s.Pairs[1].Key1 == "W" && s.Pairs[1].Key2 == "S" && s.Pairs[1].Behavior == Last && s.HasChanges);
        s.Pairs[1].Key1 = "A";
        Note("a key already used in another pair is refused", (s.ValidationMessage ?? "").Contains("more than one pair") && !s.CanApply);
        s.Pairs[1].Key1 = "W";
        while (s.AddPairCommand.CanExecute(null)) s.AddPairCommand.Execute(null);
        Note($"the list stops at {SocdEditor.MaxPairs} pairs and every default row is valid", s.Pairs.Count == SocdEditor.MaxPairs && s.ValidationMessage is null);
        for (int i = s.Pairs.Count - 1; i >= 1; i--) s.RemovePairCommand.Execute(s.Pairs[i]);
        Note("remove pairs: rows renumber and the first pair is kept", s.Pairs.Count == 1 && s.Pairs[0].Number == 1 && !s.HasChanges);

        s.RemovePairCommand.Execute(s.Pairs[0]);
        Note("SOCD on with no pairs is refused with advice", s.ValidationMessage == "Add at least one pair, or turn SOCD off." && !s.CanApply);
        s.Enabled = false;
        Note("switch it off: valid (this is GG's 'no pairs' state) and a change", s.ValidationMessage is null && s.HasChanges && s.CanApply);
        s.LoadCommand.Execute(null); Pump();
        Note("Load again puts the keyboard's settings back on screen, dropping the edits", s.Enabled && s.Pairs.Count == 1 && !s.HasChanges);

        // ---- saving ----
        s.Pairs[0].Behavior = Key2;
        answer = false;
        s.ApplyCommand.Execute(null); Pump();
        Note("Save + Cancel in the dialog: nothing is written", backend.AppliedPlans.Count == 0 && dialogs.Count == 1 && dialogs[0].Caption == "Write SOCD" && vm.StatusMessage.StartsWith("Cancelled"));
        Note("  the dialog shows the SOCD before/after and the exact bytes", dialogs[0].Details.Contains("SOCD settings now:") && dialogs[0].Details.Contains("A + D: last input priority") && dialogs[0].Details.Contains("A + D: key 2 priority") && dialogs[0].Details.Contains("SOCD block"));
        answer = true;
        s.ApplyCommand.Execute(null); Pump();
        MacroPlan sent = backend.AppliedPlans.LastOrDefault()!;
        Note("Save + confirm: the backend is handed a plan that changes only the behaviour, with no live command (the switch did not flip)",
            backend.AppliedPlans.Count == 1 && sent.Ok && sent.PreWriteCommand is null && SocdEditor.Read(sent.After).Pairs[0].Behavior == SocdBehavior.Key2Priority && sent.BackupTag == "pre-socd");
        Note("  afterwards the tab shows the written settings as the new baseline", s.LoadedText.StartsWith("Saved to Config 1 and verified") && !s.HasChanges && s.Pairs[0].Behavior == Key2);

        s.Enabled = false;
        s.ApplyCommand.Execute(null); Pump();
        MacroPlan off = backend.AppliedPlans.Last();
        Note("switching SOCD off: the plan carries GG's live command 1a 00 and the dialog says so",
            off.PreWriteCommand is { Length: 64 } c && c[0] == 0x1A && c[1] == 0 && dialogs.Last().Details.Contains("live command 1a 00") && !SocdEditor.Read(off.After).Enabled);
        s.Enabled = true;
        s.ApplyCommand.Execute(null); Pump();
        Note("and back on: live command 1a 01", backend.AppliedPlans.Last().PreWriteCommand is { } c2 && c2[1] == 1 && SocdEditor.Read(backend.AppliedPlans.Last().After).Enabled);

        // ---- both tabs work on one shared copy of the profile, so neither can act on a stale copy of it ----
        var m = vm.Macros;
        m.LoadCommand.Execute(null); Pump();
        Note("the Macros tab has the profile loaded", m.HasLoaded);
        s.Pairs[0].Behavior = Key1;
        s.ApplyCommand.Execute(null); Pump();
        Note("after a SOCD write the Macros tab still shows the profile (it now holds what was written)", m.HasLoaded && !m.LoadedText.Contains("elsewhere") && s.LoadedText.StartsWith("Saved to Config 1"));
        m.SelectKeyCommand.Execute(m.KeyCaps.First(k => k.Label == "F11")); Pump();
        m.ApplyCommand.Execute(null); Pump();
        MacroPlan afterMacro = backend.AppliedPlans.Last();
        Note("a macro saved next is planned on top of the SOCD change just written (nothing is lost)",
            afterMacro.Ok && SocdEditor.Read(afterMacro.Before).Pairs[0].Behavior == SocdBehavior.Key1Priority && SocdEditor.Read(afterMacro.After).Pairs[0].Behavior == SocdBehavior.Key1Priority);
        Note("after a macro write the SOCD tab still shows its pairs", s.HasLoaded && s.Pairs.Count == 1 && m.LoadedText.StartsWith("Saved to Config 1"));
        host.Close();
        return log;
    }

    private static List<string> CompatSelfTest()
    {
        var log = new List<string>();
        void Note(string label, bool ok) => log.Add($"[{(ok ? "PASS" : "FAIL")}] {label}");
        var backend = new PreviewBackend();
        bool answer = false;
        var dialogs = new List<(string Caption, string Summary, string Details)>();
        var vm = new MainViewModel(backend, (_, _) => false, (cap, sum, det) => { dialogs.Add((cap, sum, det)); return answer; }, autoRefresh: false);
        var host = new Window { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Width = 10, Height = 10 };
        host.Show();
        void Pump() { for (int i = 0; i < 20; i++) { Flush(host); System.Threading.Thread.Sleep(10); } }

        vm.OpenCompatibility();
        CompatibilityViewModel c = vm.Compatibility!;
        Note("opening the window scans and shows a report naming both devices, with no read", c.Report.Contains("1038:1614") && c.Report.Contains("1038:1610") && !c.Report.Contains("Read check") && backend.ReadChecksRequested.Count == 0);
        Note("  the TKL is recognised as the tested model; the other one as 'same command interface' but unproven",
            c.Report.Contains("that Apex Control was built and tested on") && c.Report.Contains("same command interface as the TKL") && c.Report.Contains("proves nothing about its profile format"));
        Note("  both have the TKL's command interface, so the read is available", c.Candidates.Count == 2 && c.CanRead && c.ReadHint == "");

        answer = false;
        c.ReadCommand.Execute(null); Pump();
        Note("Read + Cancel in the dialog: nothing is read and the status says so", backend.ReadChecksRequested.Count == 0 && vm.StatusMessage.StartsWith("Cancelled") && dialogs.Count == 1 && dialogs[0].Caption == "Read this keyboard");
        Note("  the dialog says it reads only, uses GG's startup read commands, writes nothing, and warns the model is untested",
            dialogs[0].Summary.Contains("writes nothing") && dialogs[0].Summary.Contains("same read commands GG sends") && dialogs[0].Summary.Contains("never been tried") && dialogs[0].Details == "");

        answer = true;
        c.ReadCommand.Execute(null); Pump();
        Note("Read + confirm: both devices are read", backend.ReadChecksRequested.OrderBy(x => x).SequenceEqual(new[] { 0x1610, 0x1614 }) && !vm.StatusIsError && vm.StatusMessage.StartsWith("Read check finished"));
        Note("  the report now has a read check for each: the TKL matches, the other model does not",
            c.Report.Split("Read check").Length == 3 && c.Report.Contains("The profile format matches the Apex Pro TKL") && c.Report.Contains("The checksum does not match"));
        Note("  it lists checksum, key table, macro area and SOCD results", c.Report.Contains("checksum: stored") && c.Report.Contains("key table: 112 of 112") && c.Report.Contains("macro area: understood (2 macros)") && c.Report.Contains("SOCD block: understood"));
        Note("  and nothing in it identifies the machine's hardware beyond model IDs (no serial numbers, no device paths)", !c.Report.Contains("hid#") && !c.Report.ToLowerInvariant().Contains("serial"));

        string folder = Path.Combine(Path.GetTempPath(), "ApexControl-compat-selftest-" + Guid.NewGuid().ToString("N"));
        c.ReportFolder = folder;
        c.SaveCommand.Execute(null);
        string[] files = Directory.Exists(folder) ? Directory.GetFiles(folder) : Array.Empty<string>();
        Note("Save report writes the report to a text file", files.Length == 1 && File.ReadAllText(files[0]) == c.Report && c.SaveNote.StartsWith("Saved to"));
        try { Directory.Delete(folder, true); } catch (IOException) { }

        Note("export is offered after the read, and the window explains what it holds", c.CanExport && c.ExportHint == "" && !c.ExportIncludePersonal);
        string exFolder = Path.Combine(Path.GetTempPath(), "ApexControl-export-selftest-" + Guid.NewGuid().ToString("N"));
        c.ReportFolder = exFolder;
        c.ExportCommand.Execute(null);
        string[] zips = Directory.Exists(exFolder) ? Directory.GetFiles(exFolder, "*.zip") : Array.Empty<string>();
        Note("Export (default) writes one local zip, and the note says nothing was sent", zips.Length == 1 && c.ExportNote.Contains("nothing was sent anywhere"));
        if (zips.Length == 1)
        {
            using var z = System.IO.Compression.ZipFile.OpenRead(zips[0]);
            var names = z.Entries.Select(e => e.FullName).ToList();
            Note("  default zip: report + contents note + a region 02 per read device, and no region 03", names.Contains("report.txt") && names.Contains("EXPORT-CONTENTS.txt") && names.Contains("1614/slot1-region02.bin") && names.Contains("1610/slot1-region02.bin") && !names.Any(n => n.Contains("region03")));
            using var st = z.GetEntry("1614/slot1-region02.bin")!.Open(); using var ms = new MemoryStream(); st.CopyTo(ms);
            bool blank = ms.ToArray().AsSpan(2, 16).ToArray().All(b => b == 0);
            Note("  and the profile name in it is blank", blank);
        }
        c.ExportIncludePersonal = true;
        System.Threading.Thread.Sleep(1100);
        c.ExportCommand.Execute(null);
        string[] zips2 = Directory.Exists(exFolder) ? Directory.GetFiles(exFolder, "*.zip") : Array.Empty<string>();
        bool hasR3 = false;
        foreach (string zp in zips2) { using var z = System.IO.Compression.ZipFile.OpenRead(zp); if (z.Entries.Any(e => e.FullName.Contains("region03"))) hasR3 = true; }
        Note("with 'include my macros and profile name' ticked the second zip also holds region 03", zips2.Length == 2 && hasR3);
        try { Directory.Delete(exFolder, true); } catch (IOException) { }
        c.ScanCommand.Execute(null);
        Note("Scan again clears the read results and takes the export away", !c.Report.Contains("Read check") && c.SaveNote == "" && !c.CanExport && c.ExportHint.Contains("Read a keyboard") && c.ExportNote == "");

        var gg = new MainViewModel(new PreviewBackend(ggRunning: true), (_, _) => false, (_, _, _) => true, autoRefresh: false);
        gg.OpenCompatibility();
        Note("with GG running the read is unavailable and the window says what to quit, but the scan still works", !gg.Compatibility!.CanRead && gg.Compatibility.ReadHint.Contains("Quit SteelSeries GG") && gg.Compatibility.Report.Contains("1038:1614"));

        var win = new CompatibilityWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Top = 0, ShowInTaskbar = false };
        win.Show(); Pump();
        Note("the window opens", win.IsLoaded && win.IsVisible);
        win.Close();
        host.Close();
        return log;
    }

    private static List<string> AutoStartSelfTest()
    {
        var log = new List<string>();
        void Note(string label, bool ok) => log.Add($"[{(ok ? "PASS" : "FAIL")}] {label}");

        // 1. The real registry code, on a throwaway value name (the real "ApexControl" entry is never touched).
        string name = "ApexControl-selftest-" + Guid.NewGuid().ToString("N");
        var auto = new AutoStart(name, null);
        string exe = @"C:\Some Folder\ApexControl.exe";
        try
        {
            Note("nothing is set to begin with", auto.Command is null && !auto.IsEnabled && !auto.DisabledInWindows);
            auto.Enable(exe);
            using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                Note("enabling writes a per-user Run value: the exe path in quotes (it has a space) plus --tray", run?.GetValue(name) as string == "\"C:\\Some Folder\\ApexControl.exe\" --tray");
            Note("  it reads back as enabled", auto.IsEnabled && !auto.DisabledInWindows && auto.Command == AutoStart.BuildCommand(exe));
            auto.SimulateDisabledInWindows();
            Note("switched off in Windows' Startup settings: reads back as NOT enabled and says why", !auto.IsEnabled && auto.DisabledInWindows && auto.Command is not null);
            auto.Enable(exe);
            Note("turning it on again from the app clears Windows' switch-off", auto.IsEnabled && !auto.DisabledInWindows);
            auto.Disable();
            Note("disabling removes the Run value (and any Windows switch-off record)", auto.Command is null && !auto.IsEnabled && !auto.DisabledInWindows);
            auto.Disable();
            Note("disabling when nothing is set is harmless", auto.Command is null);
        }
        finally { auto.Disable(); }
        // The old "SSAP" name: an entry made under it still counts, and turning the switch on/off cleans it up.
        string oldName = "SSAP-selftest-" + Guid.NewGuid().ToString("N"), newName = "ApexControl-selftest-" + Guid.NewGuid().ToString("N");
        var migrating = new AutoStart(newName, oldName);
        try
        {
            using (var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                run.SetValue(oldName, AutoStart.BuildCommand(@"C:\Old\ApexControl.exe"));
            Note("an entry made under the old SSAP name still reads as enabled", migrating.IsEnabled && migrating.Command == AutoStart.BuildCommand(@"C:\Old\ApexControl.exe"));
            migrating.Enable(@"C:\New\ApexControl.exe");
            using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                Note("turning it on writes the new name and removes the old one", run?.GetValue(newName) as string == AutoStart.BuildCommand(@"C:\New\ApexControl.exe") && run.GetValue(oldName) is null);
            using (var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                run.SetValue(oldName, AutoStart.BuildCommand(@"C:\Old\ApexControl.exe"));
            migrating.Disable();
            Note("turning it off removes both names", migrating.Command is null);
        }
        finally { migrating.Disable(); }
        Note("the real startup entries are untouched by this test (Apex Control: " + (new AutoStart().Command ?? "not set") + ")", true);

        // 2. The switch in the window.
        var backend = new PreviewBackend();
        var vm = new MainViewModel(backend, (_, _) => false, (_, _, _) => false, autoRefresh: false);
        var host = new Window { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Width = 10, Height = 10 };
        host.Show();
        Note("the switch starts off, with no hint", !vm.StartWithWindows && vm.AutoStartHint == "");
        vm.StartWithWindows = true;
        Note("turning it on asks the backend to enable it and says so", backend.AutoStartOn && vm.StartWithWindows && !vm.StatusIsError && vm.StatusMessage.Contains("start with Windows"));
        backend.AutoStartDisabledInWindows = true; vm.Refresh();
        Note("if Windows has it switched off, the switch shows off and explains where to change it", !vm.StartWithWindows && vm.AutoStartHint.Contains("Settings"));
        vm.StartWithWindows = true;
        Note("turning it on again fixes that", vm.StartWithWindows && vm.AutoStartHint == "");
        backend.AutoStartShouldFail = true;
        vm.StartWithWindows = false;
        Note("if the change fails the switch springs back and the error is shown", vm.StartWithWindows && vm.StatusIsError && vm.StatusMessage.StartsWith("Couldn"));
        backend.AutoStartShouldFail = false;
        vm.StartWithWindows = false;
        Note("turning it off works", !vm.StartWithWindows && !backend.AutoStartOn);

        // 3. Starting hidden (what the Run entry does at login).
        var w = new MainWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Top = 0, ShowInTaskbar = false };
        using (var tray = new TrayHost(w, vm, startHidden: true))
        {
            Note("--tray start: the window is created but not shown, and the tray icon is there", !w.IsVisible && tray.TrayAdded);
            tray.ShowWindow();
            Note("  a tray click then shows it", w.IsVisible);
            tray.RequestQuit(shutdown: false);
            w.Close();
        }
        host.Close();
        return log;
    }

    private static List<string> TraySelfTest()
    {
        var log = new List<string>();
        void Note(string label, bool ok) => log.Add($"[{(ok ? "PASS" : "FAIL")}] {label}");
        var vm = new MainViewModel(new PreviewBackend(), (_, _) => false, (_, _, _) => false, autoRefresh: false);
        var window = new MainWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Top = 0, ShowInTaskbar = false };
        window.Show();
        void Pump() { for (int i = 0; i < 20; i++) { Flush(window); System.Threading.Thread.Sleep(10); } }
        Pump();

        using var host = new TrayHost(window, vm);
        Note("the tray icon is placed in the notification area", host.TrayAdded);

        window.Close(); Pump();
        Note("closing the window hides it instead of ending the app (still loaded, no longer visible)", window.IsLoaded && !window.IsVisible && !host.Quitting);
        host.ShowWindow(); Pump();
        Note("a tray click shows the window again", window.IsVisible);
        window.Close(); Pump();
        Note("...and closing it again hides it again", window.IsLoaded && !window.IsVisible);

        // Quit is refused while a keyboard operation runs.
        var running = vm.RunAsync("test operation", async _ => { await Task.Delay(400); return new OpResult(true, "done"); });
        Pump();
        bool refused = !host.RequestQuit(shutdown: false) && !host.Quitting;
        Note("Quit is refused while an operation is running", refused && vm.IsBusy);
        for (int i = 0; i < 200 && !running.IsCompleted; i++) Pump();   // let it finish on the UI thread (blocking here would deadlock)
        Note("...and allowed once it has finished", host.RequestQuit(shutdown: false) && host.Quitting);
        host.ShowWindow(); Pump();
        window.Close(); Pump();
        Note("after Quit the close button really closes the window", !window.IsLoaded);

        // Single instance: the second launch is refused and asks the first to show itself.
        string name = "ApexControl.SingleInstance.selftest." + Guid.NewGuid().ToString("N");
        using var first = SingleInstance.Acquire(name, out bool firstIsFirst);
        var shown = new System.Threading.ManualResetEventSlim();
        first.ListenForSecondLaunch(() => shown.Set());
        using var second = SingleInstance.Acquire(name, out bool secondIsFirst);
        Note("the first launch is the first instance and the second is not", firstIsFirst && !secondIsFirst);
        second.SignalFirst();
        Note("the second launch's signal reaches the first instance", shown.Wait(2000));
        return log;
    }

    private static List<string> ProfilesSelfTest()
    {
        var log = new List<string>();
        var backend = new PreviewBackend();
        var vm = new MainViewModel(backend, (_, _) => false, (_, _, _) => false, autoRefresh: false);
        var host = new Window { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -6000, Width = 10, Height = 10 };
        host.Show();
        void Pump() { for (int i = 0; i < 20; i++) { Flush(host); System.Threading.Thread.Sleep(10); } }
        void Note(string label, bool ok) => log.Add($"[{(ok ? "PASS" : "FAIL")}] {label}");
        void Rename(int slot, string text) { var c = vm.Profiles[slot - 1]; c.BeginRenameCommand.Execute(null); c.EditText = text; c.CommitRename(); }
        string Saved() => backend.SavedProfileNames.Count == 0 ? "(nothing saved)" : string.Join(", ", backend.SavedProfileNames[^1].OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"));

        Note("saved names load: Config 1 shows 'Work', Config 2 'Gaming', the rest keep 'Config N'",
            vm.Profiles[0].Title == "Work" && vm.Profiles[1].Title == "Gaming" && vm.Profiles[2].Title == "Config 3" && vm.Profiles[4].Title == "Config 5");

        vm.Profiles[2].BeginRenameCommand.Execute(null);
        Note("pencil: Config 3 goes into edit mode with its current name in the box", vm.Profiles[2].IsEditing && vm.Profiles[2].EditText == "Config 3" && !vm.Profiles[2].IsNotEditing);
        vm.Profiles[2].EditText = "  Stream  "; vm.Profiles[2].CommitRename();
        Note("rename to '  Stream  ': trimmed to 'Stream', edit mode closed, saved with the others (" + Saved() + ")",
            vm.Profiles[2].Title == "Stream" && !vm.Profiles[2].IsEditing && Saved() == "1=Work, 2=Gaming, 3=Stream");

        int before = backend.SavedProfileNames.Count;
        vm.Profiles[2].BeginRenameCommand.Execute(null); vm.Profiles[2].EditText = "Nope"; vm.Profiles[2].CancelRename();
        Note("Esc / cancel: the name stays 'Stream' and nothing is saved", vm.Profiles[2].Title == "Stream" && backend.SavedProfileNames.Count == before);

        Rename(3, "");
        Note("rename to empty: back to 'Config 3' and dropped from the saved names (" + Saved() + ")", vm.Profiles[2].Title == "Config 3" && Saved() == "1=Work, 2=Gaming");
        Rename(2, "Config 2");
        Note("rename to the default text: clears the nickname", vm.Profiles[1].Title == "Config 2" && Saved() == "1=Work");
        Rename(4, new string('x', 40));
        Note("a 40-character name is cut to 24", vm.Profiles[3].Title.Length == 24);
        Rename(4, "");

        vm.SwitchProfileCommand.Execute(1); Pump();
        Note("switching uses the nickname in the log ('Switching to Work...')", vm.LogText.Contains("Switching to Work..."));
        Note("the sidebar tags Work as active after the switch", vm.Profiles[0].SwitchedHere && !vm.Profiles[1].SwitchedHere);

        // ---- the tabs edit the profile that is selected in the sidebar --------------------------------------------------
        var confirms = new List<string>();
        var eBackend = new PreviewBackend();
        var e = new MainViewModel(eBackend, (_, _) => true, (cap, sum, det) => { confirms.Add(cap + " | " + sum); return true; }, autoRefresh: false);

        // Real backup folders for the restore cases: one holding all five profiles, one pre-write snapshot of Config 1.
        string bkRoot = Path.Combine(Path.GetTempPath(), "ApexControl-editslot-selftest-" + Guid.NewGuid().ToString("N"));
        var seed = eBackend.ReadSlotAsync(1, NullSink.Instance).Result;
        foreach ((string name, int[] slots) in new[] { ("20260919-100000", new[] { 1, 2, 3, 4, 5 }), ("20260919-110000-pre-bind-F11", new[] { 1 }) })
            foreach (int s in slots)
            {
                Directory.CreateDirectory(Path.Combine(bkRoot, name));
                File.WriteAllBytes(Path.Combine(bkRoot, name, $"slot{s}-region02.bin"), seed.Region02!);
                File.WriteAllBytes(Path.Combine(bkRoot, name, $"slot{s}-region03.bin"), seed.Region03!);
            }
        eBackend.SlotsRead.Clear();                       // the seed read above is not part of what the tabs do
        eBackend.BackupsOverride = BackupStore.List(bkRoot);
        e.Refresh();
        Note("editing starts on Config 1, and the label shows the nickname", e.EditSlot == 1 && e.EditingText == "Editing: Work (Config 1)");
        e.Macros.LoadCommand.Execute(null); Pump();
        Note("Macros load reads Config 1", e.Macros.HasLoaded && eBackend.SlotsRead.SequenceEqual(new[] { 1 }) && e.Macros.Rows.Count == 2);
        Note("  the Load button announces the result with a pop-up", e.ToastVisible && e.ToastOk && e.ToastText.Contains("Read Config 1"));

        e.SwitchProfileCommand.Execute(2); Pump();
        Note("double-click Config 2: it becomes the edited profile, and the label says so", e.EditSlot == 2 && e.EditingText == "Editing: Gaming (Config 2)" && e.Profiles[1].SwitchedHere);
        Note("  Config 2 has not been seen yet, so it is read once on the switch and the Macros tab shows it straight away (no blank tab)",
            e.Macros.HasLoaded && e.SocdTab.HasLoaded && eBackend.SlotsRead.SequenceEqual(new[] { 1, 2 }) && e.Macros.LoadedText.Contains("Config 2"));

        e.Macros.LoadCommand.Execute(null); Pump();
        Note("Load from keyboard always reads again", eBackend.SlotsRead.SequenceEqual(new[] { 1, 2, 2 }) && e.LogText.Contains("Reading Config 2"));

        e.Macros.EditorKey = "F11"; Pump();
        e.Macros.ApplyCommand.Execute(null); Pump();
        MacroPlan? p2 = eBackend.AppliedPlans.LastOrDefault();
        Note("writing a macro plans and sends it for Config 2 (not Config 1)", p2 is { Ok: true, Slot: 2 } && eBackend.AppliedPlans.Count == 1);
        Note("  the confirm text names Config 2 and says the other profiles are not touched",
            confirms.Count == 1 && confirms[0].Contains("overwrites Config 2") && confirms[0].Contains("other profiles are not touched") && !confirms[0].Contains("slot 1"));
        Note("  the plan's write is 164 steps, the length of GG's own transaction", p2 is not null && p2.WriteSteps == 164);
        Note("  afterwards Config 2 is still the profile being edited and is tagged active", e.EditSlot == 2 && e.Profiles[1].SwitchedHere && !e.Profiles[0].SwitchedHere);
        Note("  a pop-up says it was saved, and the tab says so too", e.ToastVisible && e.ToastOk && e.ToastText.StartsWith("✓") && e.Macros.LoadedText.StartsWith("Saved to Config 2") && e.Macros.Rows.Count == 3);
        Note("  the SOCD tab shows Config 2 with no extra read", e.SocdTab.HasLoaded && eBackend.SlotsRead.SequenceEqual(new[] { 1, 2, 2 }));
        e.SocdTab.Enabled = false;
        e.SocdTab.ApplyCommand.Execute(null); Pump();
        MacroPlan? socdOff2 = eBackend.AppliedPlans.LastOrDefault();
        Note("SOCD off on Config 2 is planned and written for Config 2 with GG's live command 1a 00 first (captured on Config 2)",
            socdOff2 is { Ok: true, Slot: 2 } && socdOff2.PreWriteCommand is { } off2 && off2[0] == 0x1A && off2[1] == 0 && !SocdEditor.Read(socdOff2.After).Enabled);
        e.SocdTab.Enabled = true;
        e.SocdTab.ApplyCommand.Execute(null); Pump();
        MacroPlan? socdOn2 = eBackend.AppliedPlans.LastOrDefault();
        Note("and back on: live command 1a 01, still Config 2", socdOn2 is { Ok: true, Slot: 2 } && socdOn2.PreWriteCommand is { } on2 && on2[1] == 1 && SocdEditor.Read(socdOn2.After).Enabled && e.ToastOk);

        // Each profile shows its own settings: no blank tab, no reading again, and nothing from the other profile.
        e.SwitchProfileCommand.Execute(1); Pump();
        Note("switching back to Config 1 shows Config 1's macros (2), not Config 2's (3), without reading the keyboard again",
            e.EditSlot == 1 && e.Macros.HasLoaded && e.Macros.Rows.Count == 2 && e.SocdTab.HasLoaded && eBackend.SlotsRead.SequenceEqual(new[] { 1, 2, 2 }));
        e.SwitchProfileCommand.Execute(2); Pump();
        Note("and Config 2 again shows the macro just saved there (3)", e.EditSlot == 2 && e.Macros.Rows.Count == 3 && eBackend.SlotsRead.SequenceEqual(new[] { 1, 2, 2 }));

        // Actuation is remembered and shown per profile; switching puts the profile's own table back on the keyboard.
        var act = e.ActuationTab;
        Note("the re-send-on-switch box is off by default (saved tables stay with the profile on the keyboard)", !act.ReapplyOnSwitch);
        act.ReapplyOnSwitch = true;                // the next cases exercise the re-send
        Note("Config 2 has no remembered actuation yet, Config 1 does", act.LastSentText.StartsWith("Config 2: nothing sent") && eBackend.LoadLastActuation(1) is not null);
        int reqBefore = eBackend.ActuationRequests.Count;
        act.GlobalIndex = 9;                       // 1.0 mm
        act.ApplyCommand.Execute(null); Pump();
        Note("sending actuation on Config 2 is remembered for Config 2 and a pop-up says it was sent",
            eBackend.ActuationSlots.Last() == 2 && eBackend.ActuationRequests.Last().GlobalMm == 1.0 && act.LastSentText.StartsWith("Config 2: last sent") && e.ToastVisible && e.ToastOk);
        e.SwitchProfileCommand.Execute(1); Pump();
        Note("switching to Config 1 shows Config 1's own table (2.0 mm) and re-sends it to the keyboard",
            act.GlobalText == "2.0 mm" && act.LastSentText.StartsWith("Config 1:") && eBackend.ActuationSlots.Last() == 1 && eBackend.ActuationRequests.Last().GlobalMm == 2.0);
        e.SwitchProfileCommand.Execute(2); Pump();
        Note("switching back to Config 2 shows 1.0 mm and re-sends that", act.GlobalText == "1.0 mm" && eBackend.ActuationSlots.Last() == 2 && eBackend.ActuationRequests.Last().GlobalMm == 1.0);
        // A scenario a user hit: all keys 0.8 on a profile, then single keys to 0.1 - every step has to say what it did.
        KeyCapViewModel S() => act.Keys.First(k => k.Label == "S");
        KeyCapViewModel Esc() => act.Keys.First(k => k.Label == "Esc");
        act.GlobalIndex = 7;                       // 0.8 mm
        act.ApplyCommand.Execute(null); Pump();
        Note("all keys to 0.8 mm and sent: the pop-up says exactly what went to the keyboard for which profile",
            e.ToastText.Contains("Config 2") && e.ToastText.Contains("every key at 0.8 mm") && act.StateText == "Matches what was last sent." && !act.HasUnsentChanges);
        act.ToggleKeyCommand.Execute(S()); Pump();
        act.SelectionIndex = 0;                    // 0.1 mm
        act.SetSelectedCommand.Execute(null); Pump();
        Note("select S and set it to 0.1: the key cap shows 0.1 (outlined), and a pop-up says it is in the draft but not on the keyboard yet",
            S().MmText == "0.1" && S().Overridden && e.ToastText.Contains("S set to 0.1 mm") && e.ToastText.Contains("NOT on the keyboard yet"));
        Note("  the bar at the bottom now warns that the change has not been sent", act.HasUnsentChanges && act.StateText.StartsWith("Changes NOT sent yet") && act.Summary.Contains("except S at 0.1 mm"));
        act.ApplyCommand.Execute(null); Pump();
        Note("  send: the backend gets 0.8 mm plus S 0.1, and the pop-up says so", eBackend.ActuationRequests.Last().PerKeyMm.Count == 1 && eBackend.ActuationRequests.Last().PerKeyMm[0x16] == 0.1
            && e.ToastText.Contains("0.8 mm for every key, except S at 0.1 mm") && !act.HasUnsentChanges);
        act.ToggleKeyCommand.Execute(Esc()); Pump();
        Note("clicking a key that cannot be changed (Esc) says why instead of doing nothing", !e.ToastOk && e.ToastText.Contains("Esc can't be changed here"));
        act.SelectNoneCommand.Execute(null);        // Saving the table into the profile itself (GG-style: live table, then the slot 2 image with the actuation area changed).
        int plansBefore = eBackend.AppliedPlans.Count, confirmsBefore = confirms.Count;
        act.SaveToProfileCommand.Execute(null); Pump();
        MacroPlan? actPlan = eBackend.AppliedPlans.Count > plansBefore ? eBackend.AppliedPlans[^1] : null;
        Note("'Save to Config 2...' asks for confirmation naming Config 2, then plans the profile write with GG's live table first",
            actPlan is { Ok: true, Slot: 2, BackupTag: "pre-actuation" } && actPlan.PreWriteFeature is { Length: 642 } && confirms.Count == confirmsBefore + 1
            && confirms[^1].StartsWith("Save actuation") && confirms[^1].Contains("overwrites Config 2") && confirms[^1].Contains("0.8 mm for every key, except S at 0.1 mm"));
        Note("  afterwards the profile copy holds the table (read back from the image), the tab says it was saved into the profile, and a pop-up confirms",
            actPlan is not null && ActuationImage.Read(actPlan.After).PerKey.Count == 1 && ActuationImage.RawAt(actPlan.After, 0x16) == 1542 && act.LastSentText.Contains("saved into the profile") && !act.HasUnsentChanges && e.ToastOk);
        Note("  saving the same table again says there is nothing to write and touches nothing", ((Func<bool>)(() => { int n = eBackend.AppliedPlans.Count; act.SaveToProfileCommand.Execute(null); Pump(); return eBackend.AppliedPlans.Count == n && !e.ToastOk && e.ToastText.Contains("nothing to write"); }))());        act.ReapplyOnSwitch = false;
        int reqs = eBackend.ActuationRequests.Count;
        e.SwitchProfileCommand.Execute(1); Pump();
        Note("with the re-send box unticked a switch sends no actuation", eBackend.ActuationRequests.Count == reqs && act.GlobalText == "2.0 mm");
        act.ReapplyOnSwitch = true;
        e.SwitchProfileCommand.Execute(2); Pump();

        // Backup and restore follow the edited profile.
        e.BackupAllCommand.Execute(null); Pump();
        Note("Save backup leaves the profile being edited active (Config 2), not Config 1", eBackend.BackupAllActive.SequenceEqual(new[] { 2 }) && e.EditSlot == 2);
        e.SelectedBackup = e.Backups.First(b => b.Info.HasAllSlots);
        e.RestoreCommand.Execute(null); Pump();
        Note("Restore from an all-profiles backup writes the profile being edited (Config 2) and the tabs then show what was restored",
            eBackend.RestoresRequested.Count == 1 && eBackend.RestoresRequested[0].Slot == 2 && e.Macros.Rows.Count == 2);
        e.SelectedBackup = e.Backups.First(b => !b.Info.HasAllSlots);
        Note("a pre-write snapshot is labelled with its profile", e.SelectedBackup.Kind == "Config 1 only (saved before a write)");
        e.RestoreCommand.Execute(null); Pump();
        Note("Restore from a Config 1 snapshot writes Config 1 whatever was being edited, and editing follows it",
            eBackend.RestoresRequested.Count == 2 && eBackend.RestoresRequested[1].Slot == 1 && e.EditSlot == 1 && e.Profiles[0].SwitchedHere && !e.Profiles[1].SwitchedHere);

        int reads = eBackend.SlotsRead.Count;
        e.SwitchProfileCommand.Execute(3); Pump();
        Note("switching to a profile never seen before (Config 3) reads it once and shows it; no nickname so the label is plain",
            e.EditSlot == 3 && e.Macros.HasLoaded && eBackend.SlotsRead.Count == reads + 1 && eBackend.SlotsRead[^1] == 3 && e.EditingText == "Editing: Config 3");
        try { Directory.Delete(bkRoot, true); } catch (IOException) { }
        host.Close();
        return log;
    }

    private static void Flush(Window w)
    {
        w.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        w.UpdateLayout();
    }

    private static void Save(Window w, string path)
    {
        int width = (int)w.ActualWidth, height = (int)w.ActualHeight;
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(w);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
