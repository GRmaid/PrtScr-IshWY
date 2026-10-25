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

/// <summary>截图历史窗口：复制、贴图、标注、另存为与删除动作。</summary>
public sealed partial class HistoryWindow
{

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


    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CaptureHistoryService.Instance.StorageDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{CaptureHistoryService.Instance.StorageDirectory}\"",
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
}
