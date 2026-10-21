using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrtScr.App.Services;

/// <summary>截图历史的分组模式：窗口截图按进程再分组，常规截图单独一组，早期无元数据文件归入未分类。</summary>
public enum HistoryCaptureMode
{
    Window,
    Normal,
    Legacy,
    PinCrop,
}

/// <summary>一条本机截图历史。</summary>
public sealed record HistoryItem(
    string FilePath,
    DateTime CreatedAt,
    long SizeBytes,
    int Width,
    int Height,
    HistoryCaptureMode Mode,
    string ProcessName,
    string WindowTitle)
{
    public string FileName => Path.GetFileName(FilePath);

    public string DisplayProcess => string.IsNullOrWhiteSpace(ProcessName) ? "未知进程" : ProcessName;

    public string Source => Mode switch
    {
        HistoryCaptureMode.Window => $"窗口 · {DisplayProcess}",
        HistoryCaptureMode.Normal => "常规截图",
        HistoryCaptureMode.PinCrop => "贴图裁剪",
        _ => "早期截图",
    };

    public string CategoryKey => Mode == HistoryCaptureMode.Window
        ? $"window:{DisplayProcess}"
        : $"mode:{Mode}";
}

/// <summary>
/// 本机截图历史：PNG 与同名 <c>.png.json</c> 元数据都写在程序目录 data/history。
/// 窗口截图按“进程”分组保留，常规/早期各一组；每个分类默认 30 张，可由设置调整。
/// 不联网、不上传；关闭保存开关后不再写入，已有文件仍可手动清理。
/// </summary>
public sealed class CaptureHistoryService
{
    private const int MetadataVersion = 1;
    private const string FullTag = "full";
    private const string ShotTag = "shot";

    private static readonly JsonSerializerOptions MetadataJson = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _sync = new();

    public static CaptureHistoryService Instance { get; } = new();

    public static string StorageDirectory => Path.Combine(
        AppContext.BaseDirectory, "data", "history");

    public static int PerCategoryLimit =>
        Math.Clamp(App.Config.Current.HistoryPerCategory, 1, 200);

    /// <summary>返回按时间倒序排列的历史；目录不存在时为空。UI 负责显示上限。</summary>
    public IReadOnlyList<HistoryItem> GetItems()
    {
        lock (_sync)
        {
            try
            {
                if (!Directory.Exists(StorageDirectory))
                    return Array.Empty<HistoryItem>();

                return new DirectoryInfo(StorageDirectory)
                    .GetFiles("*.png")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                    .Select(ToItem)
                    .ToArray();
            }
            catch (Exception ex)
            {
                App.Log($"History: 枚举失败 {ex.Message}");
                return Array.Empty<HistoryItem>();
            }
        }
    }

    /// <summary>把一张刚截好的 PNG 写入历史；失败不影响截图/剪贴板主流程。</summary>
    public async Task<string?> AddAsync(
        byte[] pngBytes,
        HistoryCaptureMode mode,
        string? processName = null,
        string? windowTitle = null)
    {
        if (!App.Config.Current.SaveHistory)
            return null;

        if (pngBytes.Length == 0)
            return null;

        try
        {
            Directory.CreateDirectory(StorageDirectory);
            var now = DateTime.Now;
            var tag = mode switch
            {
                HistoryCaptureMode.Window => "win",
                HistoryCaptureMode.Normal => "normal",
                HistoryCaptureMode.PinCrop => "pin",
                _ => "legacy",
            };
            var name = $"{now:yyyyMMdd-HHmmss-fff}-{tag}-{Guid.NewGuid().ToString("N")[..6]}.png";
            var path = Path.Combine(StorageDirectory, name);

            await File.WriteAllBytesAsync(path, pngBytes);

            // 元数据失败时 PNG 仍然保留，读取时按早期截图归类，不阻断主流程。
            try
            {
                await WriteMetadataAsync(path, mode, processName, windowTitle, now);
            }
            catch (Exception ex)
            {
                App.Log($"History: 元数据写入失败 {name} {ex.Message}");
            }

            lock (_sync)
                PruneLocked(PerCategoryLimit);

            App.Log($"History: saved {name} ({pngBytes.Length / 1024}KB, {mode}, {processName ?? "-"})");
            return path;
        }
        catch (Exception ex)
        {
            App.Log($"History: 保存失败 {ex.Message}");
            return null;
        }
    }

    /// <summary>设置改变后立即按新配额修剪，避免旧分类继续超限。</summary>
    public void PruneNow(int? perCategory = null)
    {
        lock (_sync)
            PruneLocked(perCategory ?? PerCategoryLimit);
    }

    public async Task<byte[]?> ReadAsync(HistoryItem item)
    {
        try
        {
            return await File.ReadAllBytesAsync(item.FilePath);
        }
        catch (Exception ex)
        {
            App.Log($"History: 读取失败 {item.FileName} {ex.Message}");
            return null;
        }
    }

    public bool Delete(HistoryItem item)
    {
        lock (_sync)
        {
            try
            {
                if (!IsInsideStorage(item.FilePath) || !File.Exists(item.FilePath))
                    return false;

                File.Delete(item.FilePath);
                TryDeleteMetadata(item.FilePath);
                App.Log($"History: deleted {item.FileName}");
                return true;
            }
            catch (Exception ex)
            {
                App.Log($"History: 删除失败 {item.FileName} {ex.Message}");
                return false;
            }
        }
    }

    public int Clear()
    {
        lock (_sync)
        {
            try
            {
                if (!Directory.Exists(StorageDirectory))
                    return 0;

                var files = new DirectoryInfo(StorageDirectory).GetFiles("*.png");
                foreach (var file in files)
                {
                    file.Delete();
                    TryDeleteMetadata(file.FullName);
                }

                // 清理可能残留的孤儿 sidecar
                foreach (var sidecar in new DirectoryInfo(StorageDirectory).GetFiles("*.png.json"))
                {
                    try { sidecar.Delete(); } catch { }
                }

                App.Log($"History: cleared {files.Length} files");
                return files.Length;
            }
            catch (Exception ex)
            {
                App.Log($"History: 清空失败 {ex.Message}");
                return 0;
            }
        }
    }

    private static void PruneLocked(int limit)
    {
        if (limit < 1)
            return;

        var dir = new DirectoryInfo(StorageDirectory);
        if (!dir.Exists)
            return;

        var files = dir.GetFiles("*.png")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .ToArray();

        // 每个分类（窗口截图按进程，常规/早期按模式）各自保留 limit 张；
        // 窗口进程分类多时总文件数会大于 limit，这是有意为之。
        var groups = files.GroupBy(f => ToItem(f).CategoryKey, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            foreach (var file in group.Skip(limit))
            {
                try
                {
                    file.Delete();
                    TryDeleteMetadata(file.FullName);
                    App.Log($"History: pruned {file.Name}");
                }
                catch (Exception ex)
                {
                    App.Log($"History: 修剪失败 {file.Name} {ex.Message}");
                }
            }
        }
    }

    private static async Task WriteMetadataAsync(
        string pngPath, HistoryCaptureMode mode, string? processName, string? windowTitle, DateTime capturedAt)
    {
        var metadata = new HistoryMetadata
        {
            Version = MetadataVersion,
            Mode = mode.ToString(),
            ProcessName = NormalizeProcessName(processName),
            WindowTitle = windowTitle?.Trim() ?? "",
            CapturedAt = capturedAt,
        };
        var path = MetadataPath(pngPath);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(metadata, MetadataJson));
        File.Move(temp, path, overwrite: true);
    }

    private static HistoryItem ToItem(FileInfo file)
    {
        var (width, height) = TryReadPngSize(file.FullName);
        var metadata = TryReadMetadata(file.FullName);

        var mode = HistoryCaptureMode.Legacy;
        var processName = "";
        var windowTitle = "";
        if (metadata is not null)
        {
            mode = Enum.TryParse<HistoryCaptureMode>(metadata.Mode, ignoreCase: true, out var parsed)
                && parsed != HistoryCaptureMode.Legacy
                ? parsed
                : HistoryCaptureMode.Legacy;
            processName = NormalizeProcessName(metadata.ProcessName);
            windowTitle = metadata.WindowTitle?.Trim() ?? "";
        }

        return new HistoryItem(
            file.FullName,
            file.LastWriteTime,
            file.Length,
            width,
            height,
            mode,
            processName,
            windowTitle);
    }

    private static HistoryMetadata? TryReadMetadata(string pngPath)
    {
        try
        {
            var path = MetadataPath(pngPath);
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<HistoryMetadata>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            App.Log($"History: 元数据读取失败 {Path.GetFileName(pngPath)} {ex.Message}");
            return null;
        }
    }

    private static string MetadataPath(string pngPath) => pngPath + ".json";

    private static bool TryDeleteMetadata(string pngPath)
    {
        try
        {
            var path = MetadataPath(pngPath);
            if (File.Exists(path))
                File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"History: 元数据删除失败 {Path.GetFileName(pngPath)} {ex.Message}");
            return false;
        }
    }

    private static string NormalizeProcessName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var name = value.Trim();
        return name.Length > 64 ? name[..64] : name;
    }

    private static (int Width, int Height) TryReadPngSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) != header.Length)
                return (0, 0);

            // PNG signature + IHDR length/type + width/height (big-endian)
            if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47)
                return (0, 0);

            var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return (width, height);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static bool IsInsideStorage(string path)
    {
        try
        {
            var root = Path.GetFullPath(StorageDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private sealed class HistoryMetadata
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("mode")]
        public string Mode { get; set; } = "";

        [JsonPropertyName("processName")]
        public string? ProcessName { get; set; }

        [JsonPropertyName("windowTitle")]
        public string? WindowTitle { get; set; }

        [JsonPropertyName("capturedAt")]
        public DateTime CapturedAt { get; set; }
    }
}
