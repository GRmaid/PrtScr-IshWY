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

/// <summary>截图历史窗口：选择、右键、拖出与键盘导航。</summary>
public sealed partial class HistoryWindow
{

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


    private List<HistoryItemView> SelectedViews() =>
        HistoryGrid.SelectedItems.OfType<HistoryItemView>()
            .OrderByDescending(v => v.Item.CreatedAt)
            .ToList();


    private void HistoryGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var dragged = e.Items.OfType<HistoryItemView>().FirstOrDefault();
        if (dragged is null)
        {
            e.Cancel = true;
            return;
        }

        var selected = SelectedViews();
        var batch = selected.Contains(dragged) ? selected : new List<HistoryItemView> { dragged };

        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.Properties.Title = batch.Count > 1
            ? $"IshWY 截图 · {batch.Count} 张"
            : dragged.Item.FilePath is { } path ? Path.GetFileName(path) : "IshWY 截图";

        // DragItemsStarting 没有 GetDeferral，不能在这里 await 文件；
        // 用 SetDataProvider 延迟到目标程序真正取数据时再解析 StorageFile。
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var files = new List<StorageFile>();
                foreach (var view in batch)
                {
                    try
                    {
                        files.Add(await StorageFile.GetFileFromPathAsync(view.Item.FilePath));
                    }
                    catch (Exception ex)
                    {
                        App.Log($"History: 拖出时找不到 {Path.GetFileName(view.Item.FilePath)} {ex.Message}");
                    }
                }

                if (files.Count == 0)
                {
                    App.Log("History: 拖出失败，所有截图文件都不存在");
                    return;
                }

                request.SetData(files);
                App.Log($"History: 拖出 {files.Count} 张截图");
            }
            catch (Exception ex)
            {
                App.Log($"History: 拖出异常 {ex}");
            }
            finally
            {
                deferral.Complete();
            }
        });

        StatusText.Text = batch.Count > 1
            ? $"正在拖出 {batch.Count} 张截图（复制到目标程序）"
            : "正在拖出截图（复制到目标程序）";
    }


    private void HistoryGrid_DragItemsCompleted(object sender, DragItemsCompletedEventArgs e)
    {
        var count = e.Items.OfType<HistoryItemView>().Count();
        StatusText.Text = e.DropResult == DataPackageOperation.None
            ? "已取消拖出"
            : e.DropResult == DataPackageOperation.Copy
                ? (count > 1 ? $"已拖出 {count} 张截图（复制）" : "已拖出截图（复制）")
                : "已拖出截图";
    }


    private void SelectAll_Click(object sender, RoutedEventArgs e) => ToggleSelectAll();


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
}
