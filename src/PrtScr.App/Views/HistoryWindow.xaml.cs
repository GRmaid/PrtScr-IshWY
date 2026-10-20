using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PrtScr.App.Services;
using Windows.Storage;
using WinRT.Interop;

namespace PrtScr.App.Views;

/// <summary>截图历史窗口：缩略图网格 + 复制/贴图/删除/清空。</summary>
public sealed partial class HistoryWindow : Window
{
    private readonly ObservableCollection<HistoryItemView> _items = new();
    private readonly PrtScr.App.Services.KeyInterceptor _keys;
    private bool _loading;
    private bool _closed;

    public HistoryWindow()
    {
        InitializeComponent();
        Title = $"{AppInfo.Name} 截图历史";

        HistoryGrid.ItemsSource = _items;

        var area = DisplayArea.GetFromWindowId(
            AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(1040, Math.Max(720, area.Width - 80));
        var height = Math.Min(760, Math.Max(540, area.Height - 80));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2,
            width,
            height));

        _keys = new PrtScr.App.Services.KeyInterceptor(WindowNative.GetWindowHandle(this));
        _keys.KeyDown += vk =>
        {
            if (vk == 0x1B)
                Close();
        };

        Closed += (_, _) =>
        {
            _closed = true;
            _keys.Dispose();
        };
        ((FrameworkElement)Content).Loaded += async (_, _) => await ReloadAsync();
    }

    /// <summary>历史被删除/清空后通知外部刷新统计。</summary>
    public event Action? HistoryChanged;

    /// <summary>窗口已经打开时刷新列表。</summary>
    public async Task RefreshAsync()
    {
        if (!_closed)
            await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_loading || _closed)
            return;

        _loading = true;
        LoadingRing.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
        EmptyText.Visibility = Visibility.Collapsed;
        StatusText.Text = "加载中…";

        try
        {
            var items = CaptureHistoryService.Instance.GetItems();
            var views = new List<HistoryItemView>(items.Count);
            foreach (var item in items)
            {
                var thumbnail = await LoadThumbnailAsync(item.FilePath);
                views.Add(new HistoryItemView(item, thumbnail));
            }

            _items.Clear();
            foreach (var view in views)
                _items.Add(view);

            var totalBytes = views.Sum(v => v.Item.SizeBytes);
            SubtitleText.Text = $"最近 30 张，仅保存在本机 data\\history · 当前 {_items.Count} 张 / {FormatSize(totalBytes)}";
            EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = _items.Count == 0 ? "" : "选中一张后可以复制、贴图或删除（双击直接复制）";
        }
        catch (Exception ex)
        {
            App.Log($"History: 窗口加载失败 {ex}");
            StatusText.Text = "加载历史失败，详情见日志";
        }
        finally
        {
            _loading = false;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private static async Task<BitmapImage?> LoadThumbnailAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage { DecodePixelWidth = 360 };
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception ex)
        {
            App.Log($"History: 缩略图失败 {Path.GetFileName(path)} {ex.Message}");
            return null;
        }
    }

    private void HistoryGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryItemView view)
        {
            HistoryGrid.SelectedItem = view;
            StatusText.Text = $"{view.Caption} · {view.Detail}";
        }
    }

    private async void HistoryGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        await CopySelectedAsync();
    }

    private HistoryItemView? RequireSelection()
    {
        if (HistoryGrid.SelectedItem is HistoryItemView view)
            return view;

        StatusText.Text = "先在列表里选一张截图";
        return null;
    }

    private async void Copy_Click(object sender, RoutedEventArgs e) => await CopySelectedAsync();

    private async Task CopySelectedAsync()
    {
        var view = RequireSelection();
        if (view is null)
            return;

        var bytes = await CaptureHistoryService.Instance.ReadAsync(view.Item);
        if (bytes is null)
        {
            StatusText.Text = "读取文件失败（可能已被删除）";
            return;
        }

        var ok = await ClipboardService.SetImageAsync(bytes);
        StatusText.Text = ok ? "已复制到剪贴板" : "复制失败（剪贴板被占用）";
    }

    private async void Pin_Click(object sender, RoutedEventArgs e)
    {
        var view = RequireSelection();
        if (view is null)
            return;

        var bytes = await CaptureHistoryService.Instance.ReadAsync(view.Item);
        if (bytes is null)
        {
            StatusText.Text = "读取文件失败（可能已被删除）";
            return;
        }

        var created = PinService.PastePng(bytes);
        StatusText.Text = created ? "已贴到桌面" : "贴图失败";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var view = RequireSelection();
        if (view is null)
            return;

        var index = _items.IndexOf(view);
        if (!CaptureHistoryService.Instance.Delete(view.Item))
        {
            StatusText.Text = "删除失败（文件可能已被删除）";
            return;
        }

        _items.Remove(view);
        if (_items.Count == 0)
        {
            HistoryGrid.SelectedItem = null;
            EmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            HistoryGrid.SelectedItem = _items[Math.Min(index, _items.Count - 1)];
        }

        StatusText.Text = "已删除 1 张";
        HistoryChanged?.Invoke();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var count = CaptureHistoryService.Instance.Clear();
        _items.Clear();
        HistoryGrid.SelectedItem = null;
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = $"已清空 {count} 张历史";
        HistoryChanged?.Invoke();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CaptureHistoryService.StorageDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{CaptureHistoryService.StorageDirectory}\"",
                UseShellExecute = true
            });
            StatusText.Text = "已在资源管理器中打开 data\\history";
        }
        catch (Exception ex)
        {
            App.Log($"History: 打开目录失败 {ex}");
            StatusText.Text = "打开文件夹失败，详情见日志";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{bytes / 1024.0:0} KB";
}

/// <summary>历史网格的显示模型（x:Bind 需要公开属性）。</summary>
public sealed class HistoryItemView
{
    public HistoryItemView(HistoryItem item, BitmapImage? thumbnail)
    {
        Item = item;
        Thumbnail = thumbnail;
    }

    public HistoryItem Item { get; }
    public BitmapImage? Thumbnail { get; }

    public string Caption => $"{Item.Source} · {Item.CreatedAt:MM-dd HH:mm:ss}";

    public string Detail => Item.Width > 0
        ? $"{Item.Width}×{Item.Height} · {FormatSize(Item.SizeBytes)}"
        : FormatSize(Item.SizeBytes);

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{bytes / 1024.0:0} KB";
}
