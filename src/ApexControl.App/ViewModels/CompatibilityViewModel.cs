using System.IO;
using ApexControl.App.Backend;
using ApexControl.App.Infrastructure;
using ApexControl.Core;

namespace ApexControl.App.ViewModels;

// The Compatibility window: what Windows sees for each SteelSeries device (nothing is sent), and an optional read-only check
// of Config 1 for devices that have the TKL's command interface. The whole result is one text report that can be copied or saved.
public sealed class CompatibilityViewModel : ObservableObject
{
    private readonly IKeyboardBackend _backend;
    private readonly IPanelHost _host;
    private readonly Dictionary<int, ReadCheckResult> _reads = new();
    private List<SteelSeriesDevice> _devices = new();
    private string _report = "";
    private string _saveNote = "";
    private string _exportNote = "";
    private bool _exportIncludePersonal;

    public CompatibilityViewModel(IKeyboardBackend backend, IPanelHost host)
    {
        _backend = backend;
        _host = host;
        ReportFolder = Compatibility.DefaultReportFolder();

        ScanCommand = new RelayCommand(_ => Scan());
        ReadCommand = new AsyncCommand(_ => ReadAsync(), _ => CanRead);
        SaveCommand = new RelayCommand(_ => Save(), _ => Report.Length > 0);
        ExportCommand = new RelayCommand(_ => Export(), _ => CanExport);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        Scan();
    }

    public string ReportFolder { get; set; }

    public RelayCommand ScanCommand { get; }
    public AsyncCommand ReadCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public string Report { get => _report; private set { if (Set(ref _report, value)) SaveCommand?.RaiseCanExecuteChanged(); } }
    public string SaveNote { get => _saveNote; private set => Set(ref _saveNote, value); }

    // Optional export for the developer: a local .zip, never uploaded. Only offered once a keyboard has been read.
    public bool CanExport => _reads.Values.Any(r => r.Ok && r.Region02 is not null);
    public string ExportNote { get => _exportNote; private set => Set(ref _exportNote, value); }
    public bool ExportIncludePersonal { get => _exportIncludePersonal; set => Set(ref _exportIncludePersonal, value); }
    public string ExportHint => CanExport ? "" : "Read a keyboard's profile first - there is nothing to export until then.";
    // Devices that have the TKL's command interface: the only ones a read check makes sense for.
    public IReadOnlyList<SteelSeriesDevice> Candidates =>
        _devices.Where(d => Compatibility.Assess(d).Match is LayoutMatch.ExactModel or LayoutMatch.SameInterfaceLayout).ToList();

    public bool CanRead => _host.CanUseHardware && Candidates.Count > 0;

    public string ReadHint => Candidates.Count == 0
        ? "No device with the TKL's command interface was found, so there is nothing to read."
        : _host.CanUseHardware ? "" : "Quit SteelSeries GG (and its Engine) and make sure nothing else is running first.";

    public void HardwareStateChanged()
    {
        Raise(nameof(CanRead)); Raise(nameof(ReadHint));
        ReadCommand.RaiseCanExecuteChanged();
    }

    private void Scan()
    {
        _devices = _backend.ScanDevices().ToList();
        _reads.Clear();
        Report = Compatibility.BuildReport(_devices, _reads);
        SaveNote = "";
        ExportNote = "";
        Raise(nameof(Candidates)); Raise(nameof(CanRead)); Raise(nameof(ReadHint));
        ReadCommand?.RaiseCanExecuteChanged();
        RaiseExportState();
    }

    private async Task ReadAsync()
    {
        var targets = Candidates;
        bool go = _host.ConfirmDetailed(
            "Read this keyboard",
            $"This reads Config 1 from: {string.Join(", ", targets.Select(d => d.Product))}.\n\n" +
            "It uses the same read commands GG sends when it starts up on the Apex Pro TKL, and writes nothing. Like GG's own read it ends by selecting Config 1 as the active profile. " +
            "Models other than the TKL have never been tried with these commands, so if the keyboard behaves oddly afterwards, unplug it and plug it back in.",
            "");
        if (!go) { _host.SetResult(true, "Cancelled. Nothing was sent."); return; }

        await _host.RunAsync("Reading Config 1 for the compatibility check (about 5 seconds)...", async sink =>
        {
            foreach (SteelSeriesDevice d in targets) _reads[d.ProductId] = await _backend.ReadCheckAsync(d, sink);
            bool allOk = targets.All(d => _reads[d.ProductId].Ok);
            return new OpResult(allOk, allOk ? "Read check finished. Nothing was written." : targets.Select(d => _reads[d.ProductId]).First(r => !r.Ok).Message);
        });
        Report = Compatibility.BuildReport(_devices, _reads);
        RaiseExportState();
    }

    private void RaiseExportState()
    {
        Raise(nameof(CanExport)); Raise(nameof(ExportHint));
        ExportCommand?.RaiseCanExecuteChanged();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(ReportFolder);
            string path = Path.Combine(ReportFolder, $"compatibility-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, Report);
            SaveNote = "Saved to " + path;
        }
        catch (Exception ex) { SaveNote = "Could not save the report: " + ex.Message; }
    }

    private void Export()
    {
        try
        {
            var items = _devices.Where(d => _reads.ContainsKey(d.ProductId)).Select(d => (d, _reads[d.ProductId])).ToList();
            string path = CompatibilityExport.WriteZip(ReportFolder, items, Report, ExportIncludePersonal, DateTime.Now);
            ExportNote = "Saved to " + path + " - nothing was sent anywhere. Attach that file yourself if you want to share it.";
        }
        catch (Exception ex) { ExportNote = "Could not create the export: " + ex.Message; }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(ReportFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + ReportFolder + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ExportNote = "Could not open the folder: " + ex.Message; }
    }
}