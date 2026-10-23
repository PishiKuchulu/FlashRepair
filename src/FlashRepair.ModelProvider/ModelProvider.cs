using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FlashRepair.Core;

namespace FlashRepair.ModelProvider;

/// <summary>User-configured model connection (spec section 5). The app ships
/// with no vendor default and no hardcoded model list.</summary>
public sealed record ModelConfig(string BaseUrl, string ApiKey, string Model)
{
    public double Temperature { get; init; } = 0.1;
    public int MaxStepsPerSession { get; init; } = 24;
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

public interface IKeyStore
{
    void Save(string key);
    string? Load();
}

/// <summary>Windows DPAPI via direct CryptProtectData P/Invoke — no NuGet
/// dependency, key never touches plaintext config or logs.</summary>
public sealed class DpapiKeyStore(string filePath) : IKeyStore
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob inData, string? description,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr promptStruct, uint flags, out DataBlob outData);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob inData, IntPtr description,
        IntPtr optionalEntropy, IntPtr reserved, IntPtr promptStruct, uint flags, out DataBlob outData);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    public void Save(string key)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI key store requires Windows.");
        var plain = Encoding.UTF8.GetBytes(key);
        var inBlob = new DataBlob { CbData = plain.Length, PbData = Marshal.AllocHGlobal(plain.Length) };
        Marshal.Copy(plain, 0, inBlob.PbData, plain.Length);
        try
        {
            if (!CryptProtectData(ref inBlob, "FlashRepairAgent", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var outBlob))
                throw new InvalidOperationException("CryptProtectData failed.");
            try
            {
                var cipher = new byte[outBlob.CbData];
                Marshal.Copy(outBlob.PbData, cipher, 0, outBlob.CbData);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
                File.WriteAllText(filePath, Convert.ToBase64String(cipher));
            }
            finally { LocalFree(outBlob.PbData); }
        }
        finally { Marshal.FreeHGlobal(inBlob.PbData); }
    }

    public string? Load()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath)) return null;
        var cipher = Convert.FromBase64String(File.ReadAllText(filePath));
        var inBlob = new DataBlob { CbData = cipher.Length, PbData = Marshal.AllocHGlobal(cipher.Length) };
        Marshal.Copy(cipher, 0, inBlob.PbData, cipher.Length);
        try
        {
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var outBlob))
                return null;
            try
            {
                var plain = new byte[outBlob.CbData];
                Marshal.Copy(outBlob.PbData, plain, 0, outBlob.CbData);
                return Encoding.UTF8.GetString(plain);
            }
            finally { LocalFree(outBlob.PbData); }
        }
        finally { Marshal.FreeHGlobal(inBlob.PbData); }
    }
}

/// <summary>Dev/test fallback for non-Windows environments. Clearly marked:
/// stores a plain marker file and warns. Production Windows builds use DPAPI.</summary>
public sealed class DevFileKeyStore(string filePath) : IKeyStore
{
    public void Save(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        File.WriteAllText(filePath, "DEV-ONLY-NOT-SECURE:" + key);
    }

    public string? Load()
    {
        if (!File.Exists(filePath)) return null;
        var text = File.ReadAllText(filePath);
        const string prefix = "DEV-ONLY-NOT-SECURE:";
        return text.StartsWith(prefix) ? text[prefix.Length..] : null;
    }
}

public sealed record ModelToolCall(string ToolName, JsonElement Arguments);
public sealed record ModelReply(string? Text, IReadOnlyList<ModelToolCall> ToolCalls);

/// <summary>OpenAI-compatible chat client with two modes:
/// native tool-calling when the model supports it, and a structured-JSON
/// fallback ({"tool":"name","arguments":{...}}) parsed and validated locally.</summary>
public sealed class OpenAiCompatibleClient(ModelConfig config, HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient();

    private string Endpoint(string path) => config.BaseUrl.TrimEnd('/') + path;

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint("/models"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
            using var res = await _http.SendAsync(req, ct);
            return res.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }

    public async Task<ModelReply> ChatAsync(
        IReadOnlyList<object> messages, IReadOnlyList<ITool> tools, CancellationToken ct = default)
    {
        var toolDefs = tools.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = JsonSerializer.Deserialize<JsonElement>(t.InputSchemaJson)
            }
        }).ToList();

        var payload = new
        {
            model = config.Model,
            temperature = config.Temperature,
            messages,
            tools = toolDefs.Count > 0 ? toolDefs : null,
            tool_choice = toolDefs.Count > 0 ? "auto" : null
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint("/chat/completions"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        req.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonDefaults.Options), Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Model endpoint returned {(int)res.StatusCode}: {Truncate(body)}");

        using var doc = JsonDocument.Parse(body);
        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        var text = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() : null;

        var calls = new List<ModelToolCall>();
        if (message.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in tc.EnumerateArray())
            {
                var fn = call.GetProperty("function");
                var argsText = fn.GetProperty("arguments").GetString() ?? "{}";
                using var argsDoc = JsonDocument.Parse(argsText);
                calls.Add(new ModelToolCall(fn.GetProperty("name").GetString()!, argsDoc.RootElement.Clone()));
            }
        }
        else if (text is not null && TryParseStructuredCall(text, out var fallback) && fallback is not null)
        {
            calls.Add(fallback);
        }
        return new ModelReply(text, calls);
    }

    /// <summary>Structured fallback: the model answers with
    /// {"tool":"&lt;name&gt;","arguments":{...}} when native tool-calling is absent.</summary>
    public static bool TryParseStructuredCall(string text, out ModelToolCall? call)
    {
        call = null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return false;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("tool", out var toolEl) || toolEl.ValueKind != JsonValueKind.String)
                return false;
            var args = doc.RootElement.TryGetProperty("arguments", out var a)
                ? a.Clone()
                : JsonSerializer.SerializeToElement(new { });
            call = new ModelToolCall(toolEl.GetString()!, args);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
