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

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            if (!HandleEscape())
                Close();
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            e.Handled = true;
            _ = DeleteSelectedAsync();
            return;
        }

        if (e.Key == Windows.System.VirtualKey.A && IsControlDown())
        {
            e.Handled = true;
            ToggleSelectAll();
        }
    }

    /// <summary>
    /// Esc 的两级语义：先退出多选/清空选择，再按一次才关窗。
    /// KeyInterceptor 与 XAML PreviewKeyDown 可能同时收到同一次按键，用时间窗去重。
    /// </summary>
    private bool HandleEscape()
    {
        var now = Environment.TickCount64;
        if (now - _lastEscapeTick < 250)
            return true;
        _lastEscapeTick = now;

        if (_selectionMode || HistoryGrid.SelectedItems.Count > 0)
        {
            ExitSelectionMode();
            StatusText.Text = "已退出多选";
            return true;
        }

        return false;
    }

    private static bool IsControlDown()
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    }

    private void EnterSelectionMode(HistoryItemView? item)
    {
        _selectionMode = true;
        HistoryGrid.SelectionMode = ListViewSelectionMode.Multiple;
        if (item is not null && !HistoryGrid.SelectedItems.Contains(item))
            HistoryGrid.SelectedItems.Add(item);
        UpdateSelectionVisuals();
    }

    private void ExitSelectionMode()
    {
        _selectionMode = false;
        _suppressSelectionSync = true;
        try
        {
            // SelectedItems 在 Single 模式下是只读集合，先按当前模式清空再切模式，否则会抛 COMException。
            ClearGridSelection();
            HistoryGrid.SelectionMode = ListViewSelectionMode.Single;
        }
        finally
        {
            _suppressSelectionSync = false;
        }
        UpdateSelectionVisuals();
    }

    /// <summary>SelectedItems 在 Single 模式下只读；清空必须按当前模式分开处理。</summary>
    private void ClearGridSelection()
    {
        if (HistoryGrid.SelectionMode == ListViewSelectionMode.Single)
            HistoryGrid.SelectedItem = null;
        else
            HistoryGrid.SelectedItems.Clear();
    }

    private void HistoryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionSync || _closed)
            return;
        UpdateSelectionVisuals();
    }

    /// <summary>多选模式下点窗口空白处退出选择；点卡片、分类项、按钮或滚动条时交给控件处理。</summary>
    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_selectionMode || _closed)
            return;

        var source = e.OriginalSource as DependencyObject;
        if (source is null)
            return;

        if (FindAncestor<GridViewItem>(source) is not null ||
            FindAncestor<ListViewItem>(source) is not null ||
            FindAncestor<ButtonBase>(source) is not null ||
            FindAncestor<ScrollBar>(source) is not null)
            return;

        ExitSelectionMode();
        StatusText.Text = "已退出多选";
    }

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

    private void UpdateSelectionVisuals()
    {
        var selected = HistoryGrid.SelectedItems.OfType<HistoryItemView>().ToHashSet();
        foreach (var view in _items)
            view.IsSelected = selected.Contains(view);

        if (SelectAllButton is not null)
        {
            var allSelected = _selectionMode && _items.Count > 0 && selected.Count == _items.Count;
            SelectAllButton.Content = allSelected ? "取消全选" : "全选";
        }

        if (_selectionMode)
        {
            StatusText.Text = selected.Count > 0
                ? $"已选 {selected.Count} 张 · Esc 退出选择"
                : "多选模式 · 单击勾选 · Esc 退出";
            return;
        }

        if (selected.Count == 1)
        {
            var view = selected.First();
            StatusText.Text = $"{view.Caption} · {view.Detail}";
        }
    }

    private void HistoryGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var view = FindItemView(e.OriginalSource as DependencyObject);
        if (view is null)
            return;

        e.Handled = true;
        EnterSelectionMode(view);
    }

    private static HistoryItemView? FindItemView(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: HistoryItemView view })
                return view;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void DeleteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = DeleteSelectedAsync();
    }

    private void SelectAllAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleSelectAll();
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

        SubtitleText.Text = $"按截图模式 / 进程分类，每类最多 {CaptureHistoryService.PerCategoryLimit} 张；"
            + $"仅保存在本机 data\\history · 共 {_allItems.Count} 张 / {FormatSize(totalBytes)}";

        EmptyText.Text = _allItems.Count == 0
            ? "还没有截图历史。按截图热键截一张图，或检查“保存截图历史”是否已打开。"
            : "当前分类还没有截图。";
        EmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;

        var title = CategoryTitle(_selectedCategoryKey);
        var scope = CategoryScopeNote(_selectedCategoryKey);
        ScopeText.Text = filteredCount == 0
            ? $"「{title}」{scope}暂无截图。"
            : filteredCount > shown
                ? $"「{title}」{scope}共 {filteredCount} 张，列表只显示最新 {shown} 张；「清空分类」会删除全部 {filteredCount} 张。"
                : $"「{title}」{scope}共 {filteredCount} 张，已全部显示。";

        if (shown == 0)
            StatusText.Text = string.Empty;
        else if (filteredCount > shown)
            StatusText.Text = $"显示最新 {shown} / {filteredCount} 张";
        else
            StatusText.Text = $"当前分类 {shown} 张 · 单张另存为，多选批量另存为";
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

    private void HistoryGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not HistoryItemView view)
            return;

        if (_selectionMode)
        {
            // Multiple 模式下 GridView 自己负责“点一次选中、再点取消”，
            // SelectionChanged 会刷新卡片边框与计数。
            UpdateSelectionVisuals();
            return;
        }

        StatusText.Text = $"{view.Caption} · {view.Detail}";
    }

    private async void HistoryGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var view = FindItemView(e.OriginalSource as DependencyObject)
            ?? HistoryGrid.SelectedItems.OfType<HistoryItemView>().FirstOrDefault();
        if (view is null)
            return;

        // 双击 = 直接贴图；多选状态下先退出多选，避免残留勾选。
        if (_selectionMode)
            ExitSelectionMode();

        _suppressSelectionSync = true;
        try
        {
            HistoryGrid.SelectedItem = view;
        }
        finally
        {
            _suppressSelectionSync = false;
        }
        UpdateSelectionVisuals();

        await PinViewAsync(view);
    }

    /// <summary>选中项按截图时间从新到旧排序；复制/标注取第一张，删除/另存为/贴图使用全部。</summary>
    private List<HistoryItemView> SelectedViews() =>
        HistoryGrid.SelectedItems.OfType<HistoryItemView>()
            .OrderByDescending(v => v.Item.CreatedAt)
            .ToList();

    private async void Copy_Click(object sender, RoutedEventArgs e) => await CopySelectedAsync();

    private async Task CopySelectedAsync()
    {
        var selected = SelectedViews();
        if (selected.Count == 0)
        {
            StatusText.Text = "先在列表里选一张截图";
            return;
        }

        var view = selected[0];
        var bytes = await CaptureHistoryService.Instance.ReadAsync(view.Item);
        if (bytes is null)
        {
            StatusText.Text = "读取文件失败（可能已被删除）";
            return;
        }

        var ok = await ClipboardService.SetImageAsync(bytes);
        StatusText.Text = ok
            ? (selected.Count > 1 ? $"已复制最新一张（共选中 {selected.Count} 张）" : "已复制到剪贴板")
            : "复制失败（剪贴板被占用）";
    }

    private async void Pin_Click(object sender, RoutedEventArgs e) => await PinSelectedAsync();

    private async Task PinSelectedAsync()
    {
        var selected = SelectedViews();
        if (selected.Count == 0)
        {
            StatusText.Text = "先在列表里选中要贴图的截图";
            return;
        }

        if (selected.Count == 1)
        {
            await PinViewAsync(selected[0]);
            return;
        }

        var created = 0;
        var failed = 0;
        foreach (var view in selected)
        {
            var bytes = await CaptureHistoryService.Instance.ReadAsync(view.Item);
            if (bytes is null)
            {
                failed++;
                continue;
            }

            // 多张同时贴出时按 24px 阶梯错开，避免全部叠在指针同一点。
            if (PinService.PastePngOffset(bytes, created * 24, created * 24))
                created++;
            else
                failed++;
        }

        StatusText.Text = failed == 0
            ? $"已贴出 {created} 张"
            : $"已贴出 {created} 张，{failed} 张失败";
    }

    private async Task PinViewAsync(HistoryItemView view)
    {
        var bytes = await CaptureHistoryService.Instance.ReadAsync(view.Item);
        if (bytes is null)
        {
            StatusText.Text = "读取文件失败（可能已被删除）";
            return;
        }

        var files = FilterItems(_selectedCategoryKey).Select(i => i.FilePath).ToList();
        var index = files.IndexOf(view.Item.FilePath);
        var scope = _categories.FirstOrDefault(c => c.Key == _selectedCategoryKey)?.Title ?? "全部";
        var created = files.Count > 1 && index >= 0
            ? PinService.PastePngWithNavigation(bytes, files, index, scope)
            : PinService.PastePng(bytes);
        StatusText.Text = created ? $"已贴到桌面 · 左/右半边切换「{scope}」" : "贴图失败";
    }

    private async void Annotate_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedViews();
        if (selected.Count == 0)
        {
            StatusText.Text = "先在列表里选一张截图";
            return;
        }

        var view = selected[0];
        var bytes = await CaptureHistoryService.Instance.ReadAsync(view.Item);
        if (bytes is null)
        {
            StatusText.Text = "读取文件失败（可能已被删除）";
            return;
        }

        var created = PinService.PastePngAndEdit(bytes);
        StatusText.Text = created
            ? (selected.Count > 1
                ? $"已贴出最新一张并进入标注（共选中 {selected.Count} 张）"
                : "已贴到桌面并进入标注")
            : "贴图失败";
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedViews();
        if (selected.Count == 0)
        {
            StatusText.Text = "先选中一张或多张截图（Ctrl / Shift 多选）";
            return;
        }

        if (selected.Count > 1)
        {
            await SaveManyAsync(selected);
            return;
        }

        var bytes = await CaptureHistoryService.Instance.ReadAsync(selected[0].Item);
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

    private async Task SaveManyAsync(List<HistoryItemView> selected)
    {
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

    private void ClearBottomBarToolTips()
    {
        // 底栏只用左侧静态提示，不再使用悬浮 tooltip；显式清空，避免旧模板或复用路径残留。
        foreach (var child in BottomBar.Children)
        {
            if (child is not StackPanel panel)
                continue;

            foreach (var item in panel.Children)
            {
                if (item is Button button)
                    ToolTipService.SetToolTip(button, null);
            }
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    private async Task DeleteSelectedAsync()
    {
        // 按钮、Delete 键与 KeyboardAccelerator 可能连续触发；确认框只开一个。
        if (_destructiveBusy)
            return;

        var selected = SelectedViews();
        if (selected.Count == 0)
        {
            StatusText.Text = "先在列表里选中要删除的截图";
            return;
        }

        // 先固定要删的条目：确认期间列表刷新或新截图进来，都不会改变删除范围。
        var targets = selected.Select(v => v.Item).ToList();
        var size = FormatSize(targets.Sum(i => i.SizeBytes));
        var message = targets.Count == 1
            ? $"将永久删除这张截图（{selected[0].Caption}，{size}）。\n\n删除后无法恢复。"
            : $"将永久删除选中的 {targets.Count} 张截图（共 {size}），未选中的截图不受影响。\n\n删除后无法恢复。";

        _destructiveBusy = true;
        try
        {
            var confirmed = await ConfirmDialog.ShowAsync(
                RootGrid, "删除截图", message, targets.Count == 1 ? "删除" : $"删除 {targets.Count} 张");
            if (_closed)
                return;
            if (!confirmed)
            {
                StatusText.Text = "已取消删除";
                return;
            }

            await DeleteTargetsAsync(targets, deleted => deleted == targets.Count
                ? $"已删除 {deleted} 张"
                : $"已删除 {deleted} 张，另有 {targets.Count - deleted} 张已不存在或删除失败（详见日志）");
        }
        finally
        {
            _destructiveBusy = false;
        }
    }

    private async void Clear_Click(object sender, RoutedEventArgs e) => await ClearCategoryAsync();

    private async Task ClearCategoryAsync()
    {
        if (_destructiveBusy)
            return;

        // 范围按点击这一刻的分类快照确定，而不是只按列表里可见的最多 DisplayCap 张。
        var categoryKey = _selectedCategoryKey;
        var categoryTitle = CategoryTitle(categoryKey);
        var targets = FilterItems(categoryKey).ToList();
        if (targets.Count == 0)
        {
            StatusText.Text = $"「{categoryTitle}」已经是空的";
            return;
        }

        var hidden = Math.Max(0, targets.Count - DisplayCap);
        var message =
            $"将永久删除「{categoryTitle}」{CategoryScopeNote(categoryKey)}的全部 {targets.Count} 张截图（共 {FormatSize(targets.Sum(i => i.SizeBytes))}）。"
            + (hidden > 0
                ? $"\n\n其中 {hidden} 张较早的截图没有显示在列表里（列表最多显示最新 {DisplayCap} 张），也会一起删除。"
                : "")
            + "\n\n删除后无法恢复；确认期间新产生的截图不会被删除。";

        _destructiveBusy = true;
        try
        {
            var confirmed = await ConfirmDialog.ShowAsync(
                RootGrid, "清空分类", message, $"删除 {targets.Count} 张");
            if (_closed)
                return;
            if (!confirmed)
            {
                StatusText.Text = "已取消清空";
                return;
            }

            await DeleteTargetsAsync(targets, deleted => deleted == targets.Count
                ? $"已清空「{categoryTitle}」{deleted} 张"
                : $"已删除「{categoryTitle}」{deleted} 张，另有 {targets.Count - deleted} 张已不存在或删除失败（详见日志）");
        }
        finally
        {
            _destructiveBusy = false;
        }
    }

    private async Task DeleteTargetsAsync(List<HistoryItem> targets, Func<int, string> describe)
    {
        _suppressExternalRefresh = true;
        try
        {
            var deleted = CaptureHistoryService.Instance.DeleteMany(targets);
            await ReloadAsync();
            StatusText.Text = describe(deleted);
        }
        finally
        {
            _suppressExternalRefresh = false;
        }
        HistoryChanged?.Invoke();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => ToggleSelectAll();

    /// <summary>全选的两级语义：未全选则全选；已全选时再点一次退出多选。</summary>
    private void ToggleSelectAll()
    {
        if (_selectionMode && _items.Count > 0 && HistoryGrid.SelectedItems.Count == _items.Count)
        {
            ExitSelectionMode();
            StatusText.Text = "已退出多选";
            return;
        }

        EnterSelectionMode(null);
        HistoryGrid.SelectAll();
        UpdateSelectionVisuals();
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
