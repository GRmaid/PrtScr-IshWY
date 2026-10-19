using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PrtScr.Ocr;

/// <summary>
/// Umi-OCR HTTP API 客户端（https://github.com/hiroi-sora/Umi-OCR）。
/// 可选高精度引擎：本机未运行 Umi-OCR 时探测失败，调用方直接跳过，不影响软件其他功能。
/// 服务端启动方式：Umi-OCR.exe --server --hide --port 1224（默认端口 1224）。
/// 接口细节以 Umi-OCR 运行后的 http://127.0.0.1:1224/docs 为准，M5 实装时再对齐字段。
/// </summary>
public sealed class UmiOcrClient : IOcrEngine
{
    public string Id => "umi-ocr";

    public string DisplayName => "Umi-OCR（PaddleOCR 高精度，可选）";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public int Port { get; }

    public UmiOcrClient(int port = 1224) => Port = port;

    private string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>v2 用 /api/ocr，旧版用 /api/ocr/data，都试一遍。</summary>
    private async Task<HttpResponseMessage?> PostToAnyEndpointAsync(string payload, CancellationToken cancellationToken)
    {
        foreach (var path in new[] { "/api/ocr", "/api/ocr/data" })
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(BaseUrl + path, content, cancellationToken);
                if (response.IsSuccessStatusCode)
                    return response;
                response.Dispose();
            }
            catch
            {
                // 换下一个端点
            }
        }
        return null;
    }

    public sealed record ProbeResult(bool Online, int Port, string? Version = null, string? Error = null);

    /// <summary>不同版本 Umi-OCR 暴露的端点不同，逐个探测；只要有一个 200 即视为在线。</summary>
    public async Task<ProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var paths = new[] { "/api/ocr/get_options", "/api/ocr/get_version", "/" };
        string? lastError = null;

        foreach (var path in paths)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));

                using var response = await _http.GetAsync(BaseUrl + path, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    lastError = $"{path} → HTTP {(int)response.StatusCode}";
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                var version = body.Trim();
                if (version.Length > 120)
                    version = version[..120];
                return new ProbeResult(true, Port, version, null);
            }
            catch (Exception ex)
            {
                lastError = $"{path} → {ex.GetType().Name}: {ex.Message}";
            }
        }

        return new ProbeResult(false, Port, null, lastError ?? "无响应");
    }

    public async Task<bool> IsAvailableAsync() => (await ProbeAsync()).Online;

    public async Task<OcrResult> RecognizeAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                base64 = Convert.ToBase64String(imageBytes),
                options = new Dictionary<string, string> { ["data.format"] = "text" },
            });

            using var response = await PostToAnyEndpointAsync(payload, cancellationToken);
            if (response is null)
                return new OcrResult(false, "", Error: "Umi-OCR 无响应（HTTP 服务可能未开启）", Engine: Id);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
                ? codeEl.GetInt32()
                : -1;
            if (code != 100)
            {
                var message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
                return new OcrResult(false, "", Error: $"Umi-OCR 返回 code={code} {message}", Engine: Id);
            }

            var text = new StringBuilder();
            if (root.TryGetProperty("data", out var data))
            {
                if (data.ValueKind == JsonValueKind.String)
                {
                    text.Append(data.GetString());
                }
                else if (data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                            text.AppendLine(item.GetString());
                        else if (item.TryGetProperty("text", out var line))
                            text.AppendLine(line.GetString());
                    }
                }
            }

            return new OcrResult(true, text.ToString().TrimEnd(), Engine: Id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new OcrResult(false, "", Error: ex.Message, Engine: Id);
        }
    }
}
