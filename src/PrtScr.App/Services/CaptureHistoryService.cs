using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrtScr.App.Services;

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

    /// <summary>默认单例使用程序目录 data/history；测试可传入临时目录。</summary>
    public CaptureHistoryService(string? storageDirectory = null)
    {
        StorageDirectory = storageDirectory ?? Path.Combine(AppContext.BaseDirectory, "data", "history");
    }

    public static CaptureHistoryService Instance { get; } = new();

    public string StorageDirectory { get; }

    /// <summary>历史文件新增/修剪后触发；可能来自后台线程，订阅方自行切回 UI 线程。</summary>
    public event Action? HistoryChanged;

    private void NotifyHistoryChanged() => HistoryChanged?.Invoke();

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

            // 配额只在写入新截图时对本分类生效；其他分类即使因调低配额而超限，
            // 也等用户显式点「立即清理」再删，避免改设置后悄悄损失旧图。
            // 分类键从刚写入的文件读回，与 PruneLocked 的归类保持一致（元数据写失败时归入早期类）。
            lock (_sync)
            {
                var saved = ToItem(new FileInfo(path));
                PruneLocked(PerCategoryLimit, saved.CategoryKey);
            }

            App.Log($"History: saved {name} ({pngBytes.Length / 1024}KB, {mode}, {processName ?? "-"})");
            NotifyHistoryChanged();
            return path;
        }
        catch (Exception ex)
        {
            App.Log($"History: 保存失败 {ex.Message}");
            return null;
        }
    }

    /// <summary>用户显式点「立即清理」时按配额修剪，返回实际删除的张数。</summary>
    public int PruneNow(int? perCategory = null)
    {
        int deleted;
        lock (_sync)
            deleted = PruneLocked(perCategory ?? PerCategoryLimit);
        NotifyHistoryChanged();
        return deleted;
    }

    /// <summary>
    /// 「立即清理」预演：只统计按配额会删除的张数，不删除任何文件。
    /// 与 <see cref="PruneLocked"/> 共用同一套排序与分类，避免预演和实际删除结果不一致。
    /// </summary>
    public PrunePlan GetPrunePlan(int? perCategory = null)
    {
        var limit = Math.Clamp(perCategory ?? PerCategoryLimit, 1, 200);
        var groups = new List<PruneGroup>();
        lock (_sync)
        {
            try
            {
                foreach (var group in EnumerateHistoryLocked()
                             .GroupBy(x => x.Item.CategoryKey, StringComparer.OrdinalIgnoreCase))
                {
                    var items = group.ToList();
                    var deleteCount = Math.Max(0, items.Count - limit);
                    if (deleteCount == 0)
                        continue;

                    groups.Add(new PruneGroup(
                        group.Key, CategoryTitle(items[0].Item), deleteCount, items.Count - deleteCount));
                }
            }
            catch (Exception ex)
            {
                App.Log($"History: 清理预演失败 {ex.Message}");
                return PrunePlan.Empty;
            }
        }

        groups.Sort((a, b) => a.DeleteCount != b.DeleteCount
            ? b.DeleteCount.CompareTo(a.DeleteCount)
            : string.Compare(a.Title, b.Title, StringComparison.CurrentCulture));
        return new PrunePlan(groups, groups.Sum(g => g.DeleteCount));
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

    public bool Delete(HistoryItem item) => DeleteMany(new[] { item }) > 0;

    /// <summary>
    /// 批量删除并只触发一次 HistoryChanged。历史窗清空当前分类、批量删除多选时使用，
    /// 避免逐个删除时设置页和已打开的历史窗反复重载。
    /// </summary>
    public int DeleteMany(IEnumerable<HistoryItem> items)
    {
        var deleted = 0;
        lock (_sync)
        {
            foreach (var item in items)
            {
                try
                {
                    if (!IsInsideStorage(item.FilePath) || !File.Exists(item.FilePath))
                        continue;

                    File.Delete(item.FilePath);
                    TryDeleteMetadata(item.FilePath);
                    App.Log($"History: deleted {item.FileName}");
                    deleted++;
                }
                catch (Exception ex)
                {
                    App.Log($"History: 删除失败 {item.FileName} {ex.Message}");
                }
            }
        }

        if (deleted > 0)
            NotifyHistoryChanged();
        return deleted;
    }

    public int Clear()
    {
        var cleared = 0;
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
                cleared = files.Length;
            }
            catch (Exception ex)
            {
                App.Log($"History: 清空失败 {ex.Message}");
            }
        }

        NotifyHistoryChanged();
        return cleared;
    }

    /// <param name="onlyCategoryKey">只修剪这一个分类；null 表示按配额清理全部分类（显式清理）。</param>
    private int PruneLocked(int limit, string? onlyCategoryKey = null)
    {
        if (limit < 1)
            return 0;

        var deleted = 0;
        // 每个分类（窗口截图按进程，常规/早期按模式）各自保留 limit 张；
        // 窗口进程分类多时总文件数会大于 limit，这是有意为之。
        foreach (var group in EnumerateHistoryLocked()
                     .GroupBy(x => x.Item.CategoryKey, StringComparer.OrdinalIgnoreCase))
        {
            if (onlyCategoryKey is not null &&
                !string.Equals(group.Key, onlyCategoryKey, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var (file, _) in group.Skip(limit))
            {
                try
                {
                    file.Delete();
                    TryDeleteMetadata(file.FullName);
                    App.Log($"History: pruned {file.Name}");
                    deleted++;
                }
                catch (Exception ex)
                {
                    App.Log($"History: 修剪失败 {file.Name} {ex.Message}");
                }
            }
        }

        return deleted;
    }

    /// <summary>按时间倒序枚举历史文件及其分类信息；调用方必须已持有 <see cref="_sync"/> 锁。</summary>
    private IEnumerable<(FileInfo File, HistoryItem Item)> EnumerateHistoryLocked()
    {
        var dir = new DirectoryInfo(StorageDirectory);
        if (!dir.Exists)
            return Array.Empty<(FileInfo, HistoryItem)>();

        return dir.GetFiles("*.png")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => (File: f, Item: ToItem(f)));
    }

    private static string CategoryTitle(HistoryItem item) => item.Mode switch
    {
        HistoryCaptureMode.Window => $"窗口 · {item.DisplayProcess}",
        HistoryCaptureMode.Normal => "常规截图",
        HistoryCaptureMode.PinCrop => "贴图裁剪",
        _ => "早期未分类",
    };

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

    private bool IsInsideStorage(string path)
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

/// <summary>「立即清理」预演中的一个分类：按配额将删除多少张、保留多少张。</summary>
public sealed record PruneGroup(string CategoryKey, string Title, int DeleteCount, int KeepCount);

/// <summary>「立即清理」的预演结果；<see cref="TotalCount"/> 为 0 表示当前没有超出配额的历史。</summary>
public sealed record PrunePlan(IReadOnlyList<PruneGroup> Groups, int TotalCount)
{
    public static PrunePlan Empty { get; } = new(Array.Empty<PruneGroup>(), 0);

    public bool IsEmpty => TotalCount == 0;
}
