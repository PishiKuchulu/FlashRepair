using System.Text.Json;
using FlashRepair.Agent;
using FlashRepair.Backends.Virtual;
using FlashRepair.Core;
using FlashRepair.ModelProvider;
using FlashRepair.SafetyGate;
using FlashRepair.Tests;
using FlashRepair.Tools;

// Zero-dependency test harness: prints PASS/FAIL lines, exit code = failures.

var failures = 0;
void Check(string name, bool condition, string detail = "")
{
    Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? " — " + detail : "")}");
    if (!condition) failures++;
}

// Locate recorded samples (repo: samples/faulty-cases), else built-in set.
string? FindSamples()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "samples", "faulty-cases");
        if (Directory.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return null;
}

var samplesDir = FindSamples();
var backend = samplesDir is not null
    ? VirtualDiskBackend.FromDirectory(samplesDir)
    : VirtualDiskBackend.BuiltInSamples();
Console.WriteLine($"Backend samples: {(samplesDir ?? "built-in")}");

var appData = Path.Combine(Path.GetTempPath(), "fra-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(appData);
var runner = new SessionRunner(backend, ToolRegistry.CreatePhase1(), new SafetyGate(), appData);

async Task<FaultCategory> Diagnose(string id) =>
    (await runner.RunDiagnosisAsync(id)).Verdict.Category;

Check("healthy device -> Healthy", await Diagnose("VIRT-HEALTHY-001") == FaultCategory.Healthy);
Check("raw filesystem -> FilesystemCorruption", await Diagnose("VIRT-RAW-002") == FaultCategory.FilesystemCorruption);
Check("zero capacity -> FirmwareOrControllerSuspect", await Diagnose("VIRT-ZERO-003") == FaultCategory.FirmwareOrControllerSuspect);
Check("fake capacity -> FakeCapacity", await Diagnose("VIRT-FAKE-004") == FaultCategory.FakeCapacity);
Check("dead regions -> MediaWornOut", await Diagnose("VIRT-DEAD-005") == FaultCategory.MediaWornOut);
Check("corrupt partition table -> PartitionTableDamage", await Diagnose("VIRT-NOLAYOUT-006") == FaultCategory.PartitionTableDamage);

// Report integrity: built from tool outputs, contains serial + category.
var reportRun = await runner.RunDiagnosisAsync("VIRT-RAW-002");
Check("report contains serial and category",
    reportRun.ReportMarkdown.Contains("SIM20260002") && reportRun.ReportMarkdown.Contains("FilesystemCorruption"));
Check("history was appended", (await HistoryStore.ReadAsync(appData)).Count >= 7);

// Phase 1 tool surface: exactly 14 tools, all read-only.
var tools = ToolRegistry.CreatePhase1();
Check("phase1 registry has 14 tools", tools.Count == 14, $"actual {tools.Count}");
Check("all phase1 tools are risk 0", tools.All(t => t.Risk == RiskLevel.ReadOnly));

// SafetyGate behaviour with a destructive stub tool (StubTool lives in StubTool.cs).
var device = (await backend.GetDeviceAsync("VIRT-HEALTHY-001"))!;
var sysDisk = device with { IsSystemDisk = true, DeviceId = "SYS-0" };
var session = new SessionState { SessionId = "test-session" };
var flashTool = new StubTool(RiskLevel.Firmware);

var phase1Gate = new SafetyGate();
Check("phase1 gate denies firmware tool",
    !phase1Gate.Evaluate(flashTool, device, session).Allowed);

var phase2Gate = new SafetyGate(new SafetyGateOptions { AllowAboveReadOnly = true, MaxAllowedRisk = RiskLevel.Firmware });
Check("gate denies firmware tool without approval",
    !phase2Gate.Evaluate(flashTool, device, session).Allowed);

SafetyGate.GrantApproval(session, "stub_flash", device, RiskLevel.Firmware);
Check("gate allows firmware tool with matching approval",
    phase2Gate.Evaluate(flashTool, device, session).Allowed);

var otherSerial = device with { Serial = "DIFFERENT" };
Check("gate denies when serial changed after approval",
    !phase2Gate.Evaluate(flashTool, otherSerial, session).Allowed);

var sysSession = new SessionState { SessionId = "sys-session" };
SafetyGate.GrantApproval(sysSession, "stub_flash", sysDisk, RiskLevel.Firmware);
Check("gate denies system disk even with approval",
    !phase2Gate.Evaluate(flashTool, sysDisk, sysSession).Allowed);

// Structured fallback parser (models without native tool-calling).
var parsed = OpenAiCompatibleClient.TryParseStructuredCall(
    "sure: {\"tool\":\"explain_finding\",\"arguments\":{\"category\":\"Healthy\"}}", out var call);
Check("structured fallback parses tool call",
    parsed && call is not null && call.ToolName == "explain_finding");

Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"{failures} TEST(S) FAILED");
return failures;
