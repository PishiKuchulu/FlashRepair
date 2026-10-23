using System.Text;
using System.Text.Json;
using FlashRepair.Core;

namespace FlashRepair.Tools;

/// <summary>Deterministic, template-based explanations (FA + EN). The model may
/// rephrase, but the canonical text — especially the data-fate line — comes from
/// here, so the user is never told something softer than the truth.</summary>
public static class FindingExplanations
{
    public static (string Fa, string En, string DataFateFa, string DataFateEn) For(FaultCategory c) => c switch
    {
        FaultCategory.Healthy => (
            "فلش سالم است و مشکل ساختاری دیده نشد.",
            "The drive looks healthy; no structural fault was found.",
            "داده‌ها دست‌نخورده‌اند.",
            "Data is untouched."),
        FaultCategory.FilesystemCorruption => (
            "فایل‌سیستم خراب است ولی سطح حافظه و پارتیشن سالم به نظر می‌رسد؛ این رایج‌ترین و امیدوارکننده‌ترین نوع خرابی است.",
            "The filesystem is corrupted while the surface and partition look intact; this is the most common and most repairable fault.",
            "قبل از هر تعمیری که بنویسد، اول باید برای نجات داده تلاش شود؛ فرمت یعنی پاک شدن داده.",
            "Attempt data rescue before any write repair; formatting erases data."),
        FaultCategory.PartitionTableDamage => (
            "جدول پارتیشن خوانا نیست؛ حافظه خوانده می‌شود ولی ویندوز نمی‌داند داده‌ها کجا چیده شده‌اند.",
            "The partition table is unreadable; the memory reads fine but Windows cannot see the layout.",
            "با ساخت دوباره چیدمان معمولاً داده قبلی از دست می‌رود؛ پشتیبان جدول قبل از تعمیر اجباری است.",
            "Rebuilding the layout usually loses previous data; a table backup before repair is mandatory."),
        FaultCategory.WriteLock => (
            "فلش قفل نوشتن دارد. اگر قفل نرم‌افزاری نباشد، معمولاً کنترلر حافظه را برای محافظت قفل کرده و تعمیر نرم‌افزاری عمومی جواب نمی‌دهد.",
            "The drive is write-locked. If the flag is not software-level, the controller usually locked the memory for protection and generic software repair will not help.",
            "خواندن داده معمولاً ممکن است؛ اول داده را کپی کنید.",
            "Reading data is usually possible; copy data off first."),
        FaultCategory.FakeCapacity => (
            "نشانه‌های قوی ظرفیت جعلی دیده شد؛ ظرفیت واقعی از ظرفیت اعلامی کمتر است و فایل‌هایی که در ناحیه جعلی نوشته شده‌اند قبلاً از دست رفته‌اند.",
            "Strong fake-capacity signals were found; real capacity is below the declared size and files written into the fake region are already lost.",
            "فقط داده‌های داخل ظرفیت واقعی شانس سالم ماندن دارند.",
            "Only data inside the real capacity has a chance of being intact."),
        FaultCategory.FirmwareOrControllerSuspect => (
            "ظرفیت گزارش‌شده با واقعیت نمی‌خواند و نشانه‌ها به خرابی فریمور یا جدول نگاشت کنترلر اشاره می‌کند؛ تعمیر عمومی ویندوزی برای این دسته کافی نیست.",
            "Reported capacity contradicts reality and signs point to firmware or controller mapping-table failure; generic Windows repair is not enough for this class.",
            "داده در این وضعیت معمولاً با ابزار عمومی قابل نجات نیست.",
            "Data is usually not recoverable with generic tools in this state."),
        FaultCategory.MediaWornOut => (
            "بخش بزرگی از سطح حافظه خوانده نمی‌شود؛ این نشانه فرسودگی یا مرگ حافظه است و هیچ نرم‌افزاری آن را برنمی‌گرداند.",
            "Large parts of the surface fail to read; this indicates worn-out or dead memory that no software can revive.",
            "هر داده‌ای که هنوز خوانده می‌شود باید همین حالا کپی شود.",
            "Copy whatever still reads, right now."),
        _ => (
            "تشخیص قطعی با تست‌های فعلی ممکن نشد؛ قدم بعدی تست عمیق‌تر یا بررسی روی سیستم دیگر است.",
            "A definite diagnosis was not possible with the current tests; the next step is deeper testing or trying another machine.",
            "تا روشن شدن وضعیت، روی فلش چیزی ننویسید.",
            "Do not write anything to the drive until the state is clear.")
    };
}

public sealed class ExplainFindingTool : ToolBase
{
    public override string Name => "explain_finding";
    public override string Description => "Canonical plain-language explanation of a fault category (FA/EN) incl. data fate.";
    public override string InputSchemaJson =>
        """{"type":"object","properties":{"category":{"type":"string"}},"required":["category"]}""";

    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var name = ToolInput.OptionalString(input, "category") ?? "Unknown";
        var category = Enum.TryParse<FaultCategory>(name, ignoreCase: true, out var parsed)
            ? parsed : FaultCategory.Unknown;
        var (fa, en, fateFa, fateEn) = FindingExplanations.For(category);
        return Task.FromResult(ToolResult.Success(
            new { category = category.ToString(), explanationFa = fa, explanationEn = en, dataFateFa = fateFa, dataFateEn = fateEn },
            en));
    }
}

public sealed class RequestApprovalTool : ToolBase
{
    public override string Name => "request_approval";
    public override string Description =>
        "Builds a standard approval card (action, target device, consequence, risk). Granting is a user action handled by the SafetyGate, never by the model.";
    public override string InputSchemaJson =>
        """{"type":"object","properties":{"tool_name":{"type":"string"},"device_id":{"type":"string"},"consequence":{"type":"string"}},"required":["tool_name","device_id"]}""";

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var device = await RequireDevice(input, ctx, ct);
        var toolName = ToolInput.OptionalString(input, "tool_name") ?? "unknown_tool";
        var consequence = ToolInput.OptionalString(input, "consequence") ?? "";
        var card = new
        {
            tool = toolName,
            deviceId = device.DeviceId,
            deviceLabel = $"{device.Model} — {device.Serial} — {device.DeclaredCapacityBytes} bytes",
            consequence,
            buttons = new[] { "approve", "cancel" }
        };
        return ToolResult.Success(new { card }, $"Approval card built for {toolName} on {device.DeviceId}.");
    }
}

public sealed class AskUserQuestionTool : ToolBase
{
    public override string Name => "ask_user_question";
    public override string Description => "Asks one bounded question with fixed options.";
    public override string InputSchemaJson =>
        """{"type":"object","properties":{"question":{"type":"string"},"options":{"type":"array","items":{"type":"string"}}},"required":["question"]}""";

    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var question = ToolInput.OptionalString(input, "question") ?? "";
        var options = input.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
            : Array.Empty<string>();
        return Task.FromResult(ToolResult.Success(new { question, options }, question));
    }
}

public sealed class GenerateRepairReportTool : ToolBase
{
    public override string Name => "generate_repair_report";
    public override string Description =>
        "Builds the final session report strictly from recorded tool outputs in the session state.";
    public override string InputSchemaJson =>
        """{"type":"object","properties":{"device_id":{"type":"string"},"category":{"type":"string"},"confidence":{"type":"number"},"recommendation":{"type":"string"}},"required":["device_id","category"]}""";

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var device = await RequireDevice(input, ctx, ct);
        var category = ToolInput.OptionalString(input, "category") ?? "Unknown";
        var confidence = input.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetDouble() : 0;
        var recommendation = ToolInput.OptionalString(input, "recommendation") ?? "";
        var md = RepairReportBuilder.Build(ctx.Session, device, category, confidence, recommendation);
        return ToolResult.Success(new { reportMarkdown = md }, "Report generated from recorded tool outputs.");
    }
}

public sealed class AbortSessionTool : ToolBase
{
    public override string Name => "abort_session";
    public override string Description => "Stops the session at a safe point and marks it aborted.";
    public override string InputSchemaJson => """{"type":"object","properties":{}}""";

    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        ctx.Session.Aborted = true;
        return Task.FromResult(ToolResult.Success(new { aborted = true }, "Session marked aborted."));
    }
}

/// <summary>Report builder shared by the report tool and the agent runner.
/// Only facts recorded in SessionState + the diagnosis verdict are rendered.</summary>
public static class RepairReportBuilder
{
    public static string Build(SessionState session, DeviceRecord device, string category,
        double confidence, string recommendation)
    {
        var parsed = Enum.TryParse<FaultCategory>(category, ignoreCase: true, out var cat)
            ? cat : FaultCategory.Unknown;
        var (fa, _, fateFa, _) = FindingExplanations.For(parsed);
        var sb = new StringBuilder();
        sb.AppendLine("# گزارش عیب‌یابی فلش");
        sb.AppendLine();
        sb.AppendLine($"- نشست: `{session.SessionId}`");
        sb.AppendLine($"- دستگاه: {device.Model} ({device.Vendor})");
        sb.AppendLine($"- شماره سریال: `{device.Serial}`");
        sb.AppendLine($"- ظرفیت اعلامی: {device.DeclaredCapacityBytes:N0} بایت");
        sb.AppendLine($"- زمان شروع: {session.StartedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();
        sb.AppendLine("## تشخیص");
        sb.AppendLine();
        sb.AppendLine($"- دسته خرابی: **{category}** (اطمینان {confidence:P0})");
        sb.AppendLine($"- توضیح: {fa}");
        sb.AppendLine($"- سرنوشت داده: {fateFa}");
        if (!string.IsNullOrWhiteSpace(recommendation))
            sb.AppendLine($"- قدم بعدی پیشنهادی: {recommendation}");
        sb.AppendLine();
        sb.AppendLine("## قدم‌های اجراشده");
        sb.AppendLine();
        foreach (var step in session.Steps)
            sb.AppendLine($"- [{step.At:HH:mm:ss}] `{step.ToolName}` (ریسک {step.Risk}) — {(step.Ok ? "موفق" : "ناموفق")}: {step.Summary}");
        if (session.Aborted) sb.AppendLine("- نشست توسط کاربر متوقف شد.");
        sb.AppendLine();
        sb.AppendLine("این گزارش فقط از خروجی واقعی ابزارها ساخته شده است؛ هیچ عددی حدس زده نشده است.");
        return sb.ToString();
    }
}

public static class ToolRegistry
{
    public static IReadOnlyList<ITool> CreatePhase1(FirmwareCatalog? catalog = null)
    {
        catalog ??= new FirmwareCatalog();
        return new ITool[]
        {
            new ListUsbDevicesTool(),
            new GetDeviceDetailsTool(),
            new GetDiskLayoutTool(),
            new CheckFilesystemTool(),
            new ReadTestSampleTool(),
            new DetectFakeCapacityRiskTool(),
            new IdentifyControllerTool(),
            new LookupFirmwareCatalogTool(catalog),
            new GetDeviceHistoryTool(),
            new ExplainFindingTool(),
            new RequestApprovalTool(),
            new AskUserQuestionTool(),
            new GenerateRepairReportTool(),
            new AbortSessionTool(),
        };
    }
}
