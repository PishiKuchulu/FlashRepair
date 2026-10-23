using System.Text.Json;
using FlashRepair.Core;
using FlashRepair.ModelProvider;
using FlashRepair.SafetyGate;
using FlashRepair.Tools;

namespace FlashRepair.Agent;

public sealed record DiagnosisFindings(
    DeviceRecord Device,
    DiskLayoutInfo? Layout,
    FilesystemProbe? Filesystem,
    double DeadRatio,
    double FakeCapacityScore,
    ControllerGuess? Controller);

public sealed record DiagnosisVerdict(
    FaultCategory Category, double Confidence,
    IReadOnlyList<string> Evidence, string RecommendationFa);

/// <summary>Deterministic decision tree (spec section 8). The model does NOT
/// classify freely in Phase 1 — this tree classifies from tool outputs, and
/// the model only explains. Every verdict carries its evidence.</summary>
public static class DiagnosisEngine
{
    public static DiagnosisVerdict Categorize(DiagnosisFindings f)
    {
        var evidence = new List<string>();
        var d = f.Device;

        if (d.DeclaredCapacityBytes <= 1_048_576)
        {
            evidence.Add($"Declared capacity is {d.DeclaredCapacityBytes} bytes (≈0).");
            evidence.Add("Layout/filesystem cannot exist on a zero-capacity report.");
            return Verdict(FaultCategory.FirmwareOrControllerSuspect, 0.80, evidence,
                "تعمیر این دسته به ماژول اختصاصی کنترلر در فاز ۴ نیاز دارد؛ در نسخه فعلی فقط گزارش صادر می‌شود.");
        }

        if (f.DeadRatio >= 0.5)
        {
            evidence.Add($"Sampled dead-read ratio is {f.DeadRatio:P0}.");
            return Verdict(FaultCategory.MediaWornOut, 0.80, evidence,
                "داده‌های خوانا را فوراً کپی کنید؛ برای حافظه فرسوده تعمیر نرم‌افزاری وجود ندارد.");
        }

        if (f.FakeCapacityScore >= 0.7)
        {
            evidence.Add($"Fake-capacity risk score is {f.FakeCapacityScore:0.00}.");
            return Verdict(FaultCategory.FakeCapacity, 0.75, evidence,
                "برای حکم قطعی، تست نوشتن روی کل سطح در فاز ۲ لازم است؛ تا آن زمان فایل مهم روی فلش ننویسید.");
        }

        if (d.IsWriteProtected)
        {
            evidence.Add("Device reports write-protected state.");
            return Verdict(FaultCategory.WriteLock, 0.60, evidence,
                "اول داده‌های خوانا را کپی کنید؛ بعد تست قفل نرم‌افزاری در فاز ۲ انجام می‌شود.");
        }

        if (f.Layout is { PartitionTableType: "Raw" } or { PartitionTableType: "None" } &&
            f.DeadRatio < 0.5)
        {
            evidence.Add($"Partition table type is {f.Layout.PartitionTableType} while reads mostly succeed.");
            return Verdict(FaultCategory.PartitionTableDamage, 0.80, evidence,
                "قدم بعدی فاز ۲ است: پشتیبان جدول پارتیشن و ساخت دوباره چیدمان، فقط با تأیید شما.");
        }

        if (f.Filesystem is { State: FilesystemState.Raw or FilesystemState.Corrupted })
        {
            evidence.Add($"Filesystem probe: {f.Filesystem.State} — {f.Filesystem.Summary}");
            evidence.Add($"Dead-read ratio is only {f.DeadRatio:P0}, so the surface itself looks intact.");
            return Verdict(FaultCategory.FilesystemCorruption, 0.85, evidence,
                "قدم بعدی فاز ۲ است: اول تلاش برای نجات داده، بعد تعمیر فایل‌سیستم یا فرمت با تأیید شما.");
        }

        if (f.Filesystem is { State: FilesystemState.Healthy } && f.DeadRatio < 0.2)
        {
            evidence.Add("Filesystem mounts cleanly and sampled reads pass.");
            return Verdict(FaultCategory.Healthy, 0.90, evidence,
                "اقدامی لازم نیست؛ از داده‌های مهم نسخه پشتیبان نگه دارید.");
        }

        evidence.Add("Signals are mixed; no branch reached its confidence threshold.");
        return Verdict(FaultCategory.Unknown, 0.40, evidence,
            "تکرار عیب‌یابی روی یک سیستم دیگر و تست عمیق‌تر پیشنهاد می‌شود.");
    }

    private static DiagnosisVerdict Verdict(FaultCategory c, double conf, List<string> ev, string rec) =>
        new(c, conf, ev, rec);
}

public sealed record DiagnosisSessionResult(
    SessionState Session, DeviceRecord Device, DiagnosisVerdict Verdict,
    string ReportMarkdown, IReadOnlyList<ToolResult> ToolResults);

/// <summary>Runs one full Phase 1 diagnosis session: read-only tools only,
/// each through the SafetyGate, each recorded into the session (the report's
/// single source of truth).</summary>
public sealed class SessionRunner(
    IDiskBackend backend,
    IReadOnlyList<ITool> tools,
    SafetyGate.SafetyGate gate,
    string appDataDir)
{
    private readonly Dictionary<string, ITool> _tools = tools.ToDictionary(t => t.Name);

    public async Task<DiagnosisSessionResult> RunDiagnosisAsync(string deviceId, CancellationToken ct = default)
    {
        var session = new SessionState { SessionId = Guid.NewGuid().ToString("N")[..12], DeviceId = deviceId };
        var ctx = new ToolContext { Backend = backend, Session = session, AppDataDir = appDataDir };
        var results = new List<ToolResult>();

        async Task<ToolResult> Run(string toolName, object args)
        {
            var tool = _tools[toolName];
            var device = await backend.GetDeviceAsync(deviceId, ct);
            var decision = gate.Evaluate(tool, device, session);
            ToolResult result;
            if (!decision.Allowed)
            {
                result = ToolResult.Fail("BLOCKED_BY_SAFETY_GATE", decision.DenyReason ?? "Blocked.");
            }
            else
            {
                var input = JsonSerializer.SerializeToElement(args, JsonDefaults.Options);
                result = await tool.ExecuteAsync(input, ctx, ct);
            }
            session.Steps.Add(new SessionStep(DateTimeOffset.UtcNow, toolName, tool.Risk, result.Ok, result.Summary));
            results.Add(result);
            return result;
        }

        var details = await Run("get_device_details", new { device_id = deviceId });
        if (!details.Ok)
            throw new InvalidOperationException($"Diagnosis aborted: {details.Summary}");
        var device = details.Data.GetProperty("device").Deserialize<DeviceRecord>(JsonDefaults.Options)!;

        var layoutResult = await Run("get_disk_layout", new { device_id = deviceId });
        var fsResult = await Run("check_filesystem", new { device_id = deviceId });
        var readResult = await Run("read_test_sample", new { device_id = deviceId });
        var fakeResult = await Run("detect_fake_capacity_risk", new { device_id = deviceId });
        var ctrlResult = await Run("identify_controller", new { device_id = deviceId });
        await Run("lookup_firmware_catalog", new { device_id = deviceId });
        await Run("get_device_history", new { device_id = deviceId });

        var findings = new DiagnosisFindings(
            device,
            layoutResult.Ok ? layoutResult.Data.GetProperty("layout").Deserialize<DiskLayoutInfo>(JsonDefaults.Options) : null,
            fsResult.Ok ? fsResult.Data.GetProperty("probe").Deserialize<FilesystemProbe>(JsonDefaults.Options) : null,
            readResult.Ok ? readResult.Data.GetProperty("deadRatio").GetDouble() : 1.0,
            fakeResult.Ok ? fakeResult.Data.GetProperty("score").GetDouble() : 0.0,
            ctrlResult.Ok ? ctrlResult.Data.GetProperty("guess").Deserialize<ControllerGuess>(JsonDefaults.Options) : null);

        var verdict = DiagnosisEngine.Categorize(findings);

        await Run("explain_finding", new { category = verdict.Category.ToString() });
        var reportResult = await Run("generate_repair_report", new
        {
            device_id = deviceId,
            category = verdict.Category.ToString(),
            confidence = verdict.Confidence,
            recommendation = verdict.RecommendationFa
        });
        var report = reportResult.Ok
            ? reportResult.Data.GetProperty("reportMarkdown").GetString() ?? ""
            : "";

        await HistoryStore.AppendAsync(appDataDir,
            new HistoryEntry(device.Serial, DateTimeOffset.UtcNow, verdict.Category.ToString(), "diagnosed"), ct);

        return new DiagnosisSessionResult(session, device, verdict, report, results);
    }
}

/// <summary>Optional model narration layer. When a model is configured, it
/// receives the *finished, evidence-backed verdict* and may call explain_finding
/// or answer in text. It cannot change the category, invent numbers, or run
/// diagnosis tools itself — classification already happened deterministically.</summary>
public sealed class AgentOrchestrator(OpenAiCompatibleClient? model, IReadOnlyList<ITool> tools)
{
    public async Task<string> ExplainAsync(
        DiagnosisSessionResult result, ToolContext ctx, CancellationToken ct = default)
    {
        var canonical = FindingExplanations.For(result.Verdict.Category);
        if (model is null) return canonical.Fa;

        var explainTool = tools.First(t => t.Name == "explain_finding");
        var messages = new List<object>
        {
            new { role = "system", content = "You explain a finished USB flash diagnosis to the user in simple Persian. Use ONLY the verdict JSON given. Never invent numbers. You may call explain_finding once." },
            new { role = "user", content = JsonSerializer.Serialize(new
                {
                    category = result.Verdict.Category.ToString(),
                    confidence = result.Verdict.Confidence,
                    evidence = result.Verdict.Evidence,
                    device = result.Device.Model,
                    capacityBytes = result.Device.DeclaredCapacityBytes
                }, JsonDefaults.Options) }
        };
        try
        {
            var reply = await model.ChatAsync(messages, new[] { explainTool }, ct);
            foreach (var call in reply.ToolCalls.Where(c => c.ToolName == "explain_finding"))
            {
                var toolResult = await explainTool.ExecuteAsync(call.Arguments, ctx, ct);
                if (toolResult.Ok) return toolResult.Data.GetProperty("explanationFa").GetString() ?? canonical.Fa;
            }
            return string.IsNullOrWhiteSpace(reply.Text) ? canonical.Fa : reply.Text;
        }
        catch (Exception)
        {
            // Model is an enhancement, never a dependency of the diagnosis.
            return canonical.Fa;
        }
    }
}
