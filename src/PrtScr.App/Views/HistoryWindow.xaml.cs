using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PrtScr.App.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI;
using WinRT.Interop;

namespace PrtScr.App.Views;

/// <summary>截图历史窗口：左侧按截图模式/进程分类，右侧缩略图网格，支持多选批量另存为。</summary>
public sealed partial class HistoryWindow : Window
{
    private const string AllCategoryKey = "all";
    private const string WindowModeKey = "mode:Window";
    private const string NormalModeKey = "mode:Normal";
    private const string PinCropModeKey = "mode:PinCrop";
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
    private bool _selectionMode;
    private bool _suppressSelectionSync;
    private bool _suppressExternalRefresh;
    private bool _destructiveBusy;
    private long _lastEscapeTick;

    public HistoryWindow()
    {
        InitializeComponent();
        WindowAppearance.ApplyTitleBar(this, (FrameworkElement)Content);
        ClearBottomBarToolTips();
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(RootGrid_PointerPressed), true);
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
            // 删除确认框打开时 Esc 交给对话框取消，不能顺带把历史窗关掉。
            if (vk == 0x1B && !_destructiveBusy && !HandleEscape())
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

    /// <summary>
    /// Esc 的两级语义：先退出多选/清空选择，再按一次才关窗。
    /// KeyInterceptor 与 XAML PreviewKeyDown 可能同时收到同一次按键，用时间窗去重。
    /// </summary>
    /// <summary>SelectedItems 在 Single 模式下只读；清空必须按当前模式分开处理。</summary>
    /// <summary>多选模式下点窗口空白处退出选择；点卡片、分类项、按钮或滚动条时交给控件处理。</summary>
    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
                return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    /// <summary>历史被删除/清空后通知外部刷新统计。</summary>
    public event Action? HistoryChanged;

    /// <summary>窗口已经打开时刷新列表。</summary>
    public async Task RefreshAsync()
    {
        // 本窗口自己删除/清空时会主动重载；服务事件触发的这次刷新直接跳过，避免重复加载缩略图。
        if (!_closed && !_suppressExternalRefresh)
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
            PinCropModeKey, "贴图裁剪",
            _allItems.Count(i => i.Mode == HistoryCaptureMode.PinCrop)));
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

            _suppressSelectionSync = true;
            try
            {
                ClearGridSelection();
            }
            finally
            {
                _suppressSelectionSync = false;
            }

            _items.Clear();
            foreach (var view in views)
                _items.Add(view);

            UpdateSummary();
            UpdateSelectionVisuals();
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
            PinCropModeKey => _allItems.Where(i => i.Mode == HistoryCaptureMode.PinCrop),
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

        SubtitleText.Text = $"每类最多 {CaptureHistoryService.PerCategoryLimit} 张 · 仅存本机";

        EmptyText.Text = _allItems.Count == 0
            ? "还没有截图历史。按截图热键截一张图，或检查“保存截图历史”是否已打开。"
            : "当前分类还没有截图。";
        EmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;

        var title = CategoryTitle(_selectedCategoryKey);
        var scope = CategoryScopeNote(_selectedCategoryKey);
        var total = $"全库共 {_allItems.Count} 张 / {FormatSize(totalBytes)}";
        ScopeText.Text = filteredCount == 0
            ? $"「{title}」{scope}暂无截图（{total}）。"
            : filteredCount > shown
                ? $"「{title}」{scope}共 {filteredCount} 张，列表只显示最新 {shown} 张；「清空分类」会删除全部 {filteredCount} 张。{total}。"
                : $"「{title}」{scope}共 {filteredCount} 张，已全部显示。{total}。";

        // 顶部统计已并入 ScopeText；右上只留给“已删除”等一次性操作反馈。
        StatusText.Text = string.Empty;
    }

    private string CategoryTitle(string categoryKey) =>
        _categories.FirstOrDefault(c => c.Key == categoryKey)?.Title ?? "当前分类";

    /// <summary>「全部」和「窗口截图」是汇总分类，清空时会波及下面的子分类，要在文案里说清楚。</summary>
    private static string CategoryScopeNote(string categoryKey) => categoryKey switch
    {
        AllCategoryKey => "（所有分类）",
        WindowModeKey => "（含所有进程）",
        _ => "",
    };

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCategoryChange || _closed)
            return;

        if (CategoryList.SelectedItem is not HistoryCategoryView category ||
            category.Key == _selectedCategoryKey)
            return;

        _selectedCategoryKey = category.Key;
        ExitSelectionMode();
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

    /// <summary>选中项按截图时间从新到旧排序；复制/标注取第一张，删除/另存为/贴图使用全部。</summary>
    /// <summary>
    /// 把选中的历史截图作为真实 PNG 文件拖到资源管理器 / 微信 / 文档等外部程序。
    /// 拖已选的卡片 = 整批；拖未选中的卡片 = 只拖这一张，避免误带上一批勾选。
    /// 复制语义：历史原图与剪贴板都不变。
    /// </summary>
    /// <summary>全选的两级语义：未全选则全选；已全选时再点一次退出多选。</summary>
    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{bytes / 1024.0:0} KB";
}

/// <summary>历史网格的显示模型（x:Bind 需要公开属性）。</summary>
public sealed class HistoryItemView : INotifyPropertyChanged
{
    private static readonly Brush TransparentBrush = new SolidColorBrush(Colors.Transparent);
    private static Brush? _defaultBorderBrush;
    private static Brush? _selectedBorderBrush;
    private static Brush? _selectionTintBrush;

    private bool _isSelected;

    public HistoryItemView(HistoryItem item, BitmapImage? thumbnail)
    {
        Item = item;
        Thumbnail = thumbnail;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public HistoryItem Item { get; }
    public BitmapImage? Thumbnail { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CardBorderThickness));
            OnPropertyChanged(nameof(CardBorderBrush));
            OnPropertyChanged(nameof(SelectionTintBrush));
            OnPropertyChanged(nameof(SelectionBadgeBrush));
            OnPropertyChanged(nameof(SelectionVisibility));
        }
    }

    public Thickness CardBorderThickness => _isSelected ? new Thickness(2.5) : new Thickness(1);

    public Brush CardBorderBrush => _isSelected ? SelectedBorderBrush : DefaultBorderBrush;

    public Brush SelectionTintBrush => _isSelected ? SelectionTint : TransparentBrush;

    public Brush SelectionBadgeBrush => SelectedBorderBrush;

    public Visibility SelectionVisibility =>
        _isSelected ? Visibility.Visible : Visibility.Collapsed;

    public string Caption => $"{Item.Source} · {Item.CreatedAt:MM-dd HH:mm:ss}";

    public string Detail => Item.Width > 0
        ? $"{Item.Width}×{Item.Height} · {FormatSize(Item.SizeBytes)}"
        : FormatSize(Item.SizeBytes);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Brush DefaultBorderBrush =>
        _defaultBorderBrush ??= ResourceBrush("CardStrokeColorDefaultBrush", Color.FromArgb(255, 128, 128, 128));

    private static Brush SelectedBorderBrush =>
        _selectedBorderBrush ??= ResourceBrush("AccentFillColorDefaultBrush", Color.FromArgb(255, 0, 120, 212));

    private static Brush SelectionTint
    {
        get
        {
            if (_selectionTintBrush is not null)
                return _selectionTintBrush;

            var color = (SelectedBorderBrush as SolidColorBrush)?.Color
                ?? Color.FromArgb(255, 0, 120, 212);
            _selectionTintBrush = new SolidColorBrush(Color.FromArgb(38, color.R, color.G, color.B));
            return _selectionTintBrush;
        }
    }

    private static Brush ResourceBrush(string key, Color fallback)
    {
        try
        {
            if (Application.Current.Resources[key] is Brush brush)
                return brush;
        }
        catch
        {
        }
        return new SolidColorBrush(fallback);
    }

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
