using System.IO;

namespace PrtScr.App.Services;

/// <summary>一条本机截图历史。</summary>
public sealed record HistoryItem(
    string FilePath,
    DateTime CreatedAt,
    long SizeBytes,
    int Width,
    int Height,
    string Source)
{
    public string FileName => Path.GetFileName(FilePath);
}

/// <summary>
/// 本机截图历史：只把 PNG 写在程序目录 data/history，最多保留 30 张。
/// 不联网、不上传；关闭保存开关后不再写入，已有文件仍可手动清理。
/// </summary>
public sealed class CaptureHistoryService
{
    private const int MaxItems = 30;
    private const string FullTag = "full";
    private const string ShotTag = "shot";

    private readonly object _sync = new();

    public static CaptureHistoryService Instance { get; } = new();

    public static string StorageDirectory => Path.Combine(
        AppContext.BaseDirectory, "data", "history");

    /// <summary>返回按时间倒序排列的历史；目录不存在时为空。</summary>
    public IReadOnlyList<HistoryItem> GetItems()
    {
        lock (_sync)
        {
            try
            {
                if (!Directory.Exists(StorageDirectory))
                    return Array.Empty<HistoryItem>();

                var files = new DirectoryInfo(StorageDirectory).GetFiles("*.png");
                return files
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                    .Take(MaxItems)
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
    public async Task<string?> AddAsync(byte[] pngBytes, bool fullScreen)
    {
        if (!App.Config.Current.SaveHistory)
            return null;

        if (pngBytes.Length == 0)
            return null;

        try
        {
            Directory.CreateDirectory(StorageDirectory);
            var tag = fullScreen ? FullTag : ShotTag;
            var name = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{tag}-{Guid.NewGuid().ToString("N")[..6]}.png";
            var path = Path.Combine(StorageDirectory, name);

            await using (var stream = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true))
            {
                await stream.WriteAsync(pngBytes);
            }

            lock (_sync)
                PruneLocked();

            App.Log($"History: saved {name} ({pngBytes.Length / 1024}KB)");
            return path;
        }
        catch (Exception ex)
        {
            App.Log($"History: 保存失败 {ex.Message}");
            return null;
        }
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
                    file.Delete();

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

    private static void PruneLocked()
    {
        var dir = new DirectoryInfo(StorageDirectory);
        if (!dir.Exists)
            return;

        var files = dir.GetFiles("*.png")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var file in files.Skip(MaxItems))
        {
            try
            {
                file.Delete();
                App.Log($"History: pruned {file.Name}");
            }
            catch (Exception ex)
            {
                App.Log($"History: 修剪失败 {file.Name} {ex.Message}");
            }
        }
    }

    private static HistoryItem ToItem(FileInfo file)
    {
        var (width, height) = TryReadPngSize(file.FullName);
        var source = file.Name.Contains($"-{FullTag}-", StringComparison.Ordinal) ? "全屏" : "截图";
        return new HistoryItem(
            file.FullName,
            file.LastWriteTime,
            file.Length,
            width,
            height,
            source);
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
}
