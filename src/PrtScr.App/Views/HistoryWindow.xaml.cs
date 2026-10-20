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

/// <summary>截图历史窗口：左侧按截图模式/进程分类，右侧缩略图网格，支持多选批量另存为。</summary>
public sealed partial class HistoryWindow : Window
{
    private const string AllCategoryKey = "all";
    private const string WindowModeKey = "mode:Window";
    private const string NormalModeKey = "mode:Normal";
    private const string LegacyModeKey = "mode:Legacy";
    private const int DisplayCap = 120;

    private readonly ObservableCollection<HistoryItemView> _items = new();
    private readonly ObservableCollection<HistoryCategoryView> _categories = new();
    private readonly PrtScr.App.Services.KeyInterceptor _keys;
    private List<HistoryItem> _allItems = new();
    private string _selectedCategoryKey = AllCategoryKey;
    private int _loadVersion;
    private bool _suppressCategoryChange;
    private bool _closed;

    public HistoryWindow()
    {
        InitializeComponent();
        Title = $"{AppInfo.Name} 截图历史";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

        HistoryGrid.ItemsSource = _items;
        CategoryList.ItemsSource = _categories;

        var area = DisplayArea.GetFromWindowId(
            AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(1120, Math.Max(860, area.Width - 80));
        var height = Math.Min(780, Math.Max(560, area.Height - 80));
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
            HistoryGrid.ItemsSource = null;
            CategoryList.ItemsSource = null;
            _items.Clear();
            _categories.Clear();
            _allItems.Clear();
        };
        ((FrameworkElement)Content).Loaded += async (_, _) =>
        {
            // 让键盘焦点落在 XAML 树内，根 Grid 的 PreviewKeyDown 才能稳定收到 Esc
            // （WinUI 3 的顶层 HWND 收不到 WM_KEYDOWN，见 docs/HANDOFF.md §5）。
            HistoryGrid.Focus(FocusState.Programmatic);
            await ReloadAsync();
        };

        Activated += (_, e) =>
        {
            if (!_closed && e.WindowActivationState != WindowActivationState.Deactivated)
                HistoryGrid.Focus(FocusState.Programmatic);
        };
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape)
            return;

        e.Handled = true;
        Close();
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
        if (_closed)
            return;

        var version = ++_loadVersion;
        ShowLoading("加载中…");
        try
        {
            var items = CaptureHistoryService.Instance.GetItems();
            if (_closed || version != _loadVersion)
                return;

            _allItems = items.ToList();
            RebuildCategories();
            await LoadVisibleAsync();
        }
        catch (Exception ex)
        {
            App.Log($"History: 窗口加载失败 {ex}");
            HideLoading();
            StatusText.Text = "加载历史失败，详情见日志";
        }
    }

    private void RebuildCategories()
    {
        var windowItems = _allItems
            .Where(i => i.Mode == HistoryCaptureMode.Window)
            .ToList();

        var rebuilt = new List<HistoryCategoryView>
        {
            new(AllCategoryKey, "全部", _allItems.Count),
            new(WindowModeKey, "窗口截图", windowItems.Count)
        };

        foreach (var group in windowItems
                     .GroupBy(i => i.DisplayProcess)
                     .OrderByDescending(g => g.Max(i => i.CreatedAt))
                     .ThenBy(g => g.Key, StringComparer.CurrentCulture))
        {
            rebuilt.Add(new HistoryCategoryView(
                $"window:{group.Key}", group.Key, group.Count(), isChild: true));
        }

        rebuilt.Add(new HistoryCategoryView(
            NormalModeKey, "常规截图",
            _allItems.Count(i => i.Mode == HistoryCaptureMode.Normal)));
        rebuilt.Add(new HistoryCategoryView(
            LegacyModeKey, "早期未分类",
            _allItems.Count(i => i.Mode == HistoryCaptureMode.Legacy)));

        var selected = rebuilt.FirstOrDefault(c => c.Key == _selectedCategoryKey)
            ?? rebuilt[0];
        _selectedCategoryKey = selected.Key;

        _suppressCategoryChange = true;
        try
        {
            _categories.Clear();
            foreach (var category in rebuilt)
                _categories.Add(category);
            CategoryList.SelectedItem = selected;
        }
        finally
        {
            _suppressCategoryChange = false;
        }
    }

    private async Task LoadVisibleAsync()
    {
        var version = ++_loadVersion;
        ShowLoading("加载缩略图…");

        try
        {
            var views = await BuildVisibleViewsAsync(version);
            if (views is null || _closed || version != _loadVersion)
                return;

            _items.Clear();
            foreach (var view in views)
                _items.Add(view);

            UpdateSummary();
        }
        catch (Exception ex)
        {
            App.Log($"History: 分类加载失败 {ex}");
            StatusText.Text = "加载历史失败，详情见日志";
        }
        finally
        {
            if (version == _loadVersion)
                HideLoading();
        }
    }

    private async Task<List<HistoryItemView>?> BuildVisibleViewsAsync(int version)
    {
        var filtered = FilterItems(_selectedCategoryKey);
        var views = new List<HistoryItemView>();
        foreach (var item in filtered.Take(DisplayCap))
        {
            if (_closed || version != _loadVersion)
                return null;

            var thumbnail = await LoadThumbnailAsync(item.FilePath);
            views.Add(new HistoryItemView(item, thumbnail));
        }

        return views;
    }

    private IEnumerable<HistoryItem> FilterItems(string categoryKey)
    {
        return categoryKey switch
        {
            AllCategoryKey => _allItems,
            WindowModeKey => _allItems.Where(i => i.Mode == HistoryCaptureMode.Window),
            NormalModeKey => _allItems.Where(i => i.Mode == HistoryCaptureMode.Normal),
            LegacyModeKey => _allItems.Where(i => i.Mode == HistoryCaptureMode.Legacy),
            _ when categoryKey.StartsWith("window:", StringComparison.Ordinal) =>
                _allItems.Where(i => i.CategoryKey == categoryKey),
            _ => _allItems
        };
    }

    private void UpdateSummary()
    {
        var totalBytes = _allItems.Sum(i => i.SizeBytes);
        var filteredCount = FilterItems(_selectedCategoryKey).Count();
        var shown = _items.Count;

        SubtitleText.Text = $"按截图模式 / 进程分类，每类最多 {CaptureHistoryService.PerCategoryLimit} 张；"
            + $"仅保存在本机 data\\history · 共 {_allItems.Count} 张 / {FormatSize(totalBytes)}";

        EmptyText.Text = _allItems.Count == 0
            ? "还没有截图历史。按 F1 截一张图，或检查“保存截图历史”是否已打开。"
            : "当前分类还没有截图。";
        EmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (shown == 0)
            StatusText.Text = string.Empty;
        else if (filteredCount > shown)
            StatusText.Text = $"当前分类共 {filteredCount} 张，先显示最新 {shown} 张";
        else
            StatusText.Text = $"当前分类 {shown} 张 · 可多选后批量另存为";
    }

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCategoryChange || _closed)
            return;

        if (CategoryList.SelectedItem is not HistoryCategoryView category ||
            category.Key == _selectedCategoryKey)
            return;

        _selectedCategoryKey = category.Key;
        _ = LoadVisibleAsync();
    }

    private void ShowLoading(string status)
    {
        LoadingRing.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
        EmptyText.Visibility = Visibility.Collapsed;
        StatusText.Text = status;
    }

    private void HideLoading()
    {
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
    }

    private static async Task<BitmapImage?> LoadThumbnailAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage { DecodePixelWidth = 240 };
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
        if (e.ClickedItem is not HistoryItemView view)
            return;

        var selected = HistoryGrid.SelectedItems.Count;
        StatusText.Text = selected > 1
            ? $"已选择 {selected} 张 · 双击复制第一张"
            : $"{view.Caption} · {view.Detail}";
    }

    private async void HistoryGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        await CopySelectedAsync();
    }

    private HistoryItemView? RequireSelection()
    {
        var view = HistoryGrid.SelectedItems.OfType<HistoryItemView>().FirstOrDefault();
        if (view is not null)
            return view;

        StatusText.Text = "先在列表里选一张截图";
        return null;
    }

    private List<HistoryItemView> SelectedViews() =>
        HistoryGrid.SelectedItems.OfType<HistoryItemView>().ToList();

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
        var selected = SelectedViews().Count;
        StatusText.Text = ok
            ? (selected > 1 ? $"已复制第一张（共选中 {selected} 张）" : "已复制到剪贴板")
            : "复制失败（剪贴板被占用）";
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

    private async void Annotate_Click(object sender, RoutedEventArgs e)
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

        var created = PinService.PastePngAndEdit(bytes);
        StatusText.Text = created ? "已贴到桌面并进入标注" : "贴图失败";
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
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

        StatusText.Text = "选择保存位置…";
        var path = await FileSaveService.SavePngAsync(
            WindowNative.GetWindowHandle(this), bytes,
            FileSaveService.SuggestedName("history"));
        StatusText.Text = path is null ? "已取消另存为" : $"已保存：{path}";
    }

    private async void SaveMany_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedViews();
        if (selected.Count == 0)
        {
            StatusText.Text = "先选中一张或多张截图（Ctrl / Shift 多选）";
            return;
        }

        var payload = new List<(byte[] Bytes, string SuggestedName)>();
        foreach (var view in selected)
        {
            var bytes = await CaptureHistoryService.Instance.ReadAsync(view.Item);
            if (bytes is null)
                continue;

            payload.Add((bytes,
                $"{view.Item.Source}-{view.Item.CreatedAt:yyyyMMdd-HHmmss}"));
        }

        if (payload.Count == 0)
        {
            StatusText.Text = "读取文件失败（可能已被删除）";
            return;
        }

        StatusText.Text = $"选择保存文件夹（共 {payload.Count} 张）…";
        var (saved, folder) = await FileSaveService.SaveManyAsync(
            WindowNative.GetWindowHandle(this), payload);
        StatusText.Text = folder is null
            ? "已取消批量另存为"
            : $"已批量保存 {saved} 张到：{folder}";
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedViews();
        if (selected.Count == 0)
        {
            StatusText.Text = "先在列表里选中要删除的截图";
            return;
        }

        var deleted = 0;
        foreach (var view in selected)
        {
            if (CaptureHistoryService.Instance.Delete(view.Item))
                deleted++;
        }

        await ReloadAsync();
        StatusText.Text = $"已删除 {deleted} 张";
        HistoryChanged?.Invoke();
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        var count = CaptureHistoryService.Instance.Clear();
        await ReloadAsync();
        StatusText.Text = $"已清空 {count} 张历史";
        HistoryChanged?.Invoke();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        HistoryGrid.SelectAll();
        StatusText.Text = $"已选择 {HistoryGrid.SelectedItems.Count} 张";
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

/// <summary>左侧分类模型：全部 / 窗口截图（按进程展开）/ 常规 / 早期未分类。</summary>
public sealed class HistoryCategoryView
{
    public HistoryCategoryView(string key, string title, int count, bool isChild = false)
    {
        Key = key;
        Title = title;
        Count = count;
        IsChild = isChild;
    }

    public string Key { get; }
    public string Title { get; }
    public int Count { get; }
    public bool IsChild { get; }
    public string CountText => Count.ToString();
    public Thickness IndentMargin => IsChild ? new Thickness(16, 0, 0, 0) : new Thickness(0);
}
