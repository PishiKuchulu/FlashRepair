using System.IO;
using System.Windows;
using FlashRepair.Agent;
using FlashRepair.Backends.Virtual;
using FlashRepair.Backends.Windows;
using FlashRepair.Core;
using FlashRepair.SafetyGate;
using FlashRepair.Tools;

namespace FlashRepair.App;

/// <summary>Phase 1 window: device list + read-only diagnosis + report.
/// All logic lives in the libraries; this window only displays and forwards
/// the user's clicks (spec: no repair logic inside buttons).</summary>
public partial class MainWindow : Window
{
    private readonly string _appData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlashRepairAgent");

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(_appData);
        Loaded += async (_, _) => await RefreshDevicesAsync();
    }

    private IDiskBackend MakeBackend() =>
        SimulatedCheck.IsChecked == true
            ? VirtualDiskBackend.BuiltInSamples()
            : new WindowsDiskBackend();

    private async Task RefreshDevicesAsync()
    {
        try
        {
            StatusText.Text = "Reading devices…";
            var devices = await MakeBackend().ListDevicesAsync();
            DeviceList.ItemsSource = devices;
            StatusText.Text = $"{devices.Count} device(s)";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error";
            ReportBox.Text = ex.Message + "\n\nTip: real-hardware access needs running as Administrator.";
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();
    private async void Simulated_Changed(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async void Diagnose_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceRecord device)
        {
            VerdictText.Text = "Select a device first.";
            return;
        }
        DiagnoseButton.IsEnabled = false;
        try
        {
            var runner = new SessionRunner(MakeBackend(), ToolRegistry.CreatePhase1(),
                new FlashRepair.SafetyGate.SafetyGate(), _appData);
            var result = await runner.RunDiagnosisAsync(device.DeviceId);
            VerdictText.Text = $"{result.Verdict.Category} — confidence {result.Verdict.Confidence:P0}";
            ReportBox.Text = result.ReportMarkdown;

            var outDir = Path.Combine(_appData, "reports");
            Directory.CreateDirectory(outDir);
            await File.WriteAllTextAsync(
                Path.Combine(outDir, $"report-{result.Session.SessionId}.md"), result.ReportMarkdown);
        }
        catch (Exception ex)
        {
            VerdictText.Text = "Diagnosis failed.";
            ReportBox.Text = ex.Message;
        }
        finally
        {
            DiagnoseButton.IsEnabled = true;
        }
    }
}
