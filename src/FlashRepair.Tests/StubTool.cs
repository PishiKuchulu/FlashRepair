using System.Text.Json;
using FlashRepair.Core;

namespace FlashRepair.Tests;

/// <summary>Configurable-risk stub used to prove SafetyGate behaviour without
/// any real destructive tool existing in Phase 1.</summary>
public sealed class StubTool(RiskLevel risk) : ITool
{
    public string Name => "stub_flash";
    public RiskLevel Risk { get; } = risk;
    public string Description => "stub";
    public string InputSchemaJson => "{}";
    public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct = default) =>
        Task.FromResult(ToolResult.Success(new { }, "stub ran"));
}
