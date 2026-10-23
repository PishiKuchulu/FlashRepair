using System.Text.Json;
using FlashRepair.Core;

namespace FlashRepair.Tools;

public abstract class ToolBase : ITool
{
    public abstract string Name { get; }
    public virtual RiskLevel Risk => RiskLevel.ReadOnly;
    public abstract string Description { get; }
    public virtual string InputSchemaJson =>
        """{"type":"object","properties":{"device_id":{"type":"string"}},"required":["device_id"]}""";

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct = default)
    {
        try
        {
            return await ExecuteCoreAsync(input, ctx, ct);
        }
        catch (ToolInputException ex)
        {
            return ToolResult.Fail(ex.Code, $"Invalid tool input: {ex.Code}");
        }
        catch (KeyNotFoundException)
        {
            return ToolResult.Fail("DEVICE_NOT_FOUND", "Device not found (unplugged or wrong id).");
        }
        catch (IOException ex)
        {
            return ToolResult.Fail("IO_ERROR", ex.Message);
        }
    }

    protected abstract Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext ctx, CancellationToken ct);

    protected static async Task<DeviceRecord> RequireDevice(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var id = ToolInput.RequireDeviceId(input);
        return await ctx.Backend.GetDeviceAsync(id, ct)
               ?? throw new KeyNotFoundException("DEVICE_NOT_FOUND");
    }
}

public static class SampleReader
{
    public const int SampleSize = 4096;

    /// <summary>Samples reads across the surface: start, 10/33/50/75/90% and the tail.</summary>
    public static async Task<IReadOnlyList<ReadPoint>> SampleAsync(
        IDiskBackend backend, DeviceRecord device, CancellationToken ct)
    {
        var points = new List<ReadPoint>();
        var capacity = device.DeclaredCapacityBytes;
        if (capacity <= 0)
        {
            points.Add(new ReadPoint(0, false, false, "ZERO_CAPACITY"));
            return points;
        }
        var offsets = new[] { 0.0, 0.10, 0.33, 0.50, 0.75, 0.90 }
            .Select(f => (long)(capacity * f))
            .Append(Math.Max(0, capacity - SampleSize))
            .Distinct()
            .ToList();

        foreach (var offset in offsets)
        {
            try
            {
                var data = await backend.ReadRangeAsync(device.DeviceId, offset, SampleSize, ct);
                points.Add(new ReadPoint(offset, true, data.All(b => b == 0), null));
            }
            catch (IOException ex)
            {
                points.Add(new ReadPoint(offset, false, false, ex.Message));
            }
        }
        return points;
    }
}

public sealed class ListUsbDevicesTool : ToolBase
{
    public override string Name => "list_usb_devices";
    public override string Description => "Lists removable/USB storage devices with stable ids.";
    public override string InputSchemaJson => """{"type":"object","properties":{}}""";

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var devices = await ctx.Backend.ListDevicesAsync(ct);
        return ToolResult.Success(new { devices }, $"{devices.Count} device(s) found via {ctx.Backend.Name} backend.");
    }
}

public sealed class GetDeviceDetailsTool : ToolBase
{
    public override string Name => "get_device_details";
    public override string Description => "Full details for one device id.";

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var device = await RequireDevice(input, ctx, ct);
        return ToolResult.Success(new { device },
            $"{device.Model} serial {device.Serial}, {device.DeclaredCapacityBytes} bytes, bus {device.BusType}.");
    }
}

public sealed class GetDiskLayoutTool : ToolBase
{
    public override string Name => "get_disk_layout";
    public override string Description => "Partition table type and partitions, read-only.";

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var device = await RequireDevice(input, ctx, ct);
        var layout = await ctx.Backend.GetLayoutAsync(device.DeviceId, ct);
        return ToolResult.Success(new { layout },
            $"Layout {layout.PartitionTableType}, {layout.Partitions.Count} partition(s).");
    }
}

public sealed class CheckFilesystemTool : ToolBase
{
    public override string Name => "check_filesystem";
    public override string Description => "Read-only filesystem probe: Healthy / Corrupted / Raw / Unknown.";

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var device = await RequireDevice(input, ctx, ct);
        var probe = await ctx.Backend.ProbeFilesystemAsync(device.DeviceId, ct);
        return ToolResult.Success(new { probe }, $"Filesystem state: {probe.State}. {probe.Summary}");
    }
}

public sealed class ReadTestSampleTool : ToolBase
{
    public override string Name => "read_test_sample";
    public override string Description => "Samples reads across the surface to map dead regions.";

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var device = await RequireDevice(input, ctx, ct);
        var points = await SampleReader.SampleAsync(ctx.Backend, device, ct);
        var dead = points.Count(p => !p.Ok);
        var ratio = points.Count == 0 ? 1.0 : (double)dead / points.Count;
        return ToolResult.Success(new { points, deadRatio = ratio },
            $"{points.Count - dead}/{points.Count} sampled reads OK (dead ratio {ratio:P0}).");
    }
}

public sealed class DetectFakeCapacityRiskTool : ToolBase
{
    public override string Name => "detect_fake_capacity_risk";
    public override string Description =>
        "Read-only fake-capacity heuristic: near-end regions that read as all-zero while early regions hold data raise the score. A full surface write test (risk 2, Phase 2) is the only definitive proof.";

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var device = await RequireDevice(input, ctx, ct);
        var points = await SampleReader.SampleAsync(ctx.Backend, device, ct);
        var early = points.FirstOrDefault(p => p.Ok);
        double score = 0.05;
        string verdict = "No fake-capacity signal in sampled reads.";
        if (early is { Ok: true, AllZero: false })
        {
            var lateZero = points.Where(p => p.Ok && p.OffsetBytes > device.DeclaredCapacityBytes / 2 && p.AllZero).Count();
            if (lateZero >= 2)
            {
                score = 0.85;
                verdict = "Strong fake-capacity signal: multiple regions past 50% read as all-zero while early regions hold data.";
            }
            else if (lateZero == 1)
            {
                score = 0.45;
                verdict = "Weak fake-capacity signal: one late region reads as all-zero.";
            }
        }
        return ToolResult.Success(new { score, verdict },
            $"Fake-capacity risk score {score:0.00}. {verdict}");
    }
}

public sealed class IdentifyControllerTool : ToolBase
{
    public override string Name => "identify_controller";
    public override string Description =>
        "Candidate controller family from the local catalog (brand-level in Phase 1, confidence always reported).";

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var device = await RequireDevice(input, ctx, ct);
        var guess = ControllerCatalog.Guess(device);
        return ToolResult.Success(new { guess },
            $"Candidate controller: {guess.Family} (confidence {guess.Confidence:0.00}). {guess.Source}");
    }
}

public sealed class LookupFirmwareCatalogTool(FirmwareCatalog catalog) : ToolBase
{
    public override string Name => "lookup_firmware_catalog";
    public override string Description =>
        "Looks up tested firmware candidates for an exactly identified controller family. Guesses never match.";
    public override string InputSchemaJson =>
        """{"type":"object","properties":{"device_id":{"type":"string"},"controller_family":{"type":"string"}},"required":["device_id"]}""";

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var device = await RequireDevice(input, ctx, ct);
        var family = ToolInput.OptionalString(input, "controller_family")
                     ?? ControllerCatalog.Guess(device).Family;
        var candidates = catalog.Lookup(family);
        return ToolResult.Success(new { family, candidates },
            candidates.Count == 0
                ? $"No tested firmware candidate for '{family}' in the local catalog."
                : $"{candidates.Count} tested candidate(s) for '{family}'.");
    }
}

public sealed class GetDeviceHistoryTool : ToolBase
{
    public override string Name => "get_device_history";
    public override string Description => "Previous sessions for this device serial on this machine.";

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
    {
        var device = await RequireDevice(input, ctx, ct);
        var entries = await HistoryStore.ReadAsync(ctx.AppDataDir, ct);
        var mine = entries.Where(e => e.Serial == device.Serial).ToList();
        return ToolResult.Success(new { entries = mine },
            mine.Count == 0 ? "No previous sessions for this device." : $"{mine.Count} previous session(s).");
    }
}

public sealed record HistoryEntry(string Serial, DateTimeOffset At, string Category, string Outcome);

public static class HistoryStore
{
    private static string PathFor(string dir) => Path.Combine(dir, "history.json");

    public static async Task<IReadOnlyList<HistoryEntry>> ReadAsync(string dir, CancellationToken ct = default)
    {
        var path = PathFor(dir);
        if (!File.Exists(path)) return Array.Empty<HistoryEntry>();
        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<List<HistoryEntry>>(json, JsonDefaults.Options) ?? new();
    }

    public static async Task AppendAsync(string dir, HistoryEntry entry, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        var existing = (await ReadAsync(dir, ct)).ToList();
        existing.Add(entry);
        await File.WriteAllTextAsync(PathFor(dir),
            JsonSerializer.Serialize(existing, JsonDefaults.Pretty), ct);
    }
}
