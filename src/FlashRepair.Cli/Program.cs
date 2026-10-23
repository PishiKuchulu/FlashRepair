using System.Text.Json;
using FlashRepair.Agent;
using FlashRepair.Backends.Virtual;
using FlashRepair.Backends.Windows;
using FlashRepair.Core;
using FlashRepair.ModelProvider;
using FlashRepair.SafetyGate;
using FlashRepair.Tools;

// FlashRepair CLI — Phase 1 host (diagnosis & reporting only).
// Commands:
//   devices [--backend auto|windows|virtual] [--samples <dir>]
//   diagnose --device <id> [--backend ...] [--samples <dir>] [--out <dir>] [--model <file.json>]
//   demo [--out <dir>]                      (virtual built-in faulty samples)
//   model-test --model <file.json>          (checks the user's model endpoint)

var argsList = args.ToList();
string Opt(string name, string fallback) =>
    argsList.IndexOf(name) is var i && i >= 0 && i + 1 < argsList.Count ? argsList[i + 1] : fallback;

IDiskBackend MakeBackend()
{
    var choice = Opt("--backend", "auto").ToLowerInvariant();
    var samples = Opt("--samples", "");
    if (choice == "virtual" || (choice == "auto" && !OperatingSystem.IsWindows()))
    {
        var backend = samples.Length > 0 && Directory.Exists(samples)
            ? VirtualDiskBackend.FromDirectory(samples)
            : VirtualDiskBackend.BuiltInSamples();
        Console.Error.WriteLine($"[backend: virtual — simulated devices, no hardware is touched]");
        return backend;
    }
    return new WindowsDiskBackend();
}

ModelConfig? LoadModel()
{
    var file = Opt("--model", "");
    if (file.Length == 0 || !File.Exists(file)) return null;
    var doc = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
    var key = Environment.GetEnvironmentVariable("FRA_API_KEY")
              ?? (doc.TryGetValue("apiKey", out var k) ? k : "");
    return new ModelConfig(doc["baseUrl"], key, doc["model"]);
}

SessionRunner MakeRunner(IDiskBackend backend, string appData) =>
    new(backend, ToolRegistry.CreatePhase1(), new SafetyGate(), appData);

var command = argsList.Count > 0 ? argsList[0] : "help";
var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlashRepairAgent");
Directory.CreateDirectory(appData);

switch (command)
{
    case "devices":
    {
        var backend = MakeBackend();
        foreach (var d in await backend.ListDevicesAsync())
            Console.WriteLine($"{d.DeviceId}\t{d.Model}\tserial={d.Serial}\t{d.DeclaredCapacityBytes:N0} bytes\tbus={d.BusType}\tremovable={d.IsRemovable}");
        break;
    }
    case "diagnose":
    {
        var deviceId = Opt("--device", "");
        if (deviceId.Length == 0) { Console.Error.WriteLine("--device <id> is required"); return 2; }
        var backend = MakeBackend();
        var runner = MakeRunner(backend, appData);
        var result = await runner.RunDiagnosisAsync(deviceId);

        var model = LoadModel();
        var orchestrator = new AgentOrchestrator(
            model is null ? null : new OpenAiCompatibleClient(model), ToolRegistry.CreatePhase1());
        var ctx = new ToolContext { Backend = backend, Session = result.Session, AppDataDir = appData };
        var explanation = await orchestrator.ExplainAsync(result, ctx);

        Console.WriteLine($"Device:      {result.Device.Model} (serial {result.Device.Serial})");
        Console.WriteLine($"Category:    {result.Verdict.Category}  (confidence {result.Verdict.Confidence:P0})");
        Console.WriteLine($"Evidence:    {string.Join(" | ", result.Verdict.Evidence)}");
        Console.WriteLine($"Explanation: {explanation}");

        var outDir = Opt("--out", "reports");
        Directory.CreateDirectory(outDir);
        var reportPath = Path.Combine(outDir, $"report-{result.Session.SessionId}.md");
        await File.WriteAllTextAsync(reportPath, result.ReportMarkdown);
        Console.WriteLine($"Report:      {Path.GetFullPath(reportPath)}");
        break;
    }
    case "demo":
    {
        var backend = VirtualDiskBackend.BuiltInSamples();
        var runner = MakeRunner(backend, appData);
        Console.WriteLine("ID\tMODEL\tCATEGORY\tCONFIDENCE");
        foreach (var d in await backend.ListDevicesAsync())
        {
            var r = await runner.RunDiagnosisAsync(d.DeviceId);
            Console.WriteLine($"{d.DeviceId}\t{d.Model}\t{r.Verdict.Category}\t{r.Verdict.Confidence:P0}");
            var outDir = Opt("--out", "reports");
            Directory.CreateDirectory(outDir);
            await File.WriteAllTextAsync(Path.Combine(outDir, $"report-{r.Session.SessionId}.md"), r.ReportMarkdown);
        }
        break;
    }
    case "model-test":
    {
        var model = LoadModel();
        if (model is null) { Console.Error.WriteLine("--model <file.json> is required"); return 2; }
        var client = new OpenAiCompatibleClient(model);
        Console.WriteLine(await client.TestConnectionAsync() ? "Model endpoint OK." : "Model endpoint NOT reachable.");
        break;
    }
    default:
        Console.WriteLine("flashrepair <devices|diagnose|demo|model-test> [options]");
        break;
}
return 0;
