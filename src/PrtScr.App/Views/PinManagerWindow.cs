using System.Collections.ObjectModel;
using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PrtScr.App.Services;
using WinRT.Interop;

namespace PrtScr.App.Views;

/// <summary>贴图管理器：集中查看所有贴图，恢复被隐藏/点击穿透的贴图。</summary>
public sealed partial class PinManagerWindow : Window
{
    private readonly ObservableCollection<PinCardView> _cards = new();
    private readonly PrtScr.App.Services.KeyInterceptor _keys;
    private bool _closed;
    private bool _reloadQueued;
    private int _reloadVersion;

    public PinManagerWindow()
    {
        InitializeComponent();
        Title = $"{AppInfo.Name} 屏幕贴图管理";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        PinGrid.ItemsSource = _cards;

        var area = DisplayArea.GetFromWindowId(
            AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(1080, Math.Max(820, area.Width - 120));
        var height = Math.Min(760, Math.Max(520, area.Height - 120));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2,
            width,
            height));

        _keys = new PrtScr.App.Services.KeyInterceptor(WindowNative.GetWindowHandle(this));
        _keys.KeyDown += vk =>
        {
            if (vk == 0x1B && !_closed)
                Close();
        };

        PinService.PinsChanged += OnPinsChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            PinService.PinsChanged -= OnPinsChanged;
            _keys.Dispose();
            PinGrid.ItemsSource = null;
            _cards.Clear();
        };
        ((FrameworkElement)Content).Loaded += async (_, _) =>
        {
            PinGrid.Focus(FocusState.Programmatic);
            await ReloadAsync();
        };
        Activated += (_, e) =>
        {
            if (!_closed && e.WindowActivationState != WindowActivationState.Deactivated)
                PinGrid.Focus(FocusState.Programmatic);
        };
    }

    private void OnPinsChanged()
    {
        if (_closed || _reloadQueued)
            return;
        _reloadQueued = true;
        DispatcherQueue.TryEnqueue(async () =>
        {
            _reloadQueued = false;
            if (!_closed)
                await ReloadAsync();
        });
    }

    private async Task ReloadAsync()
    {
        var version = ++_reloadVersion;
        var snapshot = PinService.SnapshotPins();
        var cards = new List<PinCardView>(snapshot.Count);
        foreach (var record in snapshot)
        {
            var card = await PinCardView.CreateAsync(record);
            if (version != _reloadVersion || _closed)
                return;
            cards.Add(card);
        }

        _cards.Clear();
        foreach (var card in cards)
            _cards.Add(card);

        EmptyPanel.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var hidden = snapshot.Count(p => p.Hidden);
        var states = snapshot.Count(p => p.Locked || p.ClickThrough);
        SubtitleText.Text = cards.Count == 0
            ? "所有贴图都在这里；被隐藏或点击穿透的贴图可以从这里恢复。"
            : $"共 {cards.Count} 张贴图" +
              (hidden > 0 ? $" · {hidden} 张已隐藏" : "") +
              (states > 0 ? $" · {states} 张锁定/穿透" : "") +
              " · 单击卡片置顶";
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void PinGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PinCardView card)
            return;
        PinService.SendCommand(card.Item.Hwnd, PinCommand.Focus);
    }

    private static PinCardView? ViewFrom(object sender)
        => (sender as FrameworkElement)?.DataContext as PinCardView;

    private nint Hwnd => WindowNative.GetWindowHandle(this);

    private void CopyCard_Click(object sender, RoutedEventArgs e)
    {
        var view = ViewFrom(sender);
        if (view is null)
            return;
        PinClipboardHelper.Copy(view.Item.PngBytes, DispatcherQueue);
        SubtitleText.Text = $"已复制 {view.Item.PixelWidth} × {view.Item.PixelHeight} 到剪贴板";
    }

    private void SaveCard_Click(object sender, RoutedEventArgs e)
    {
        var view = ViewFrom(sender);
        if (view is null)
            return;
        _ = FileSaveService.SavePngAsync(Hwnd, view.Item.PngBytes, FileSaveService.SuggestedName("pin"));
    }

    private void OcrCard_Click(object sender, RoutedEventArgs e)
    {
        var view = ViewFrom(sender);
        if (view is null)
            return;
        _ = OcrUiService.RecognizeAndShowAsync(view.Item.PngBytes, Hwnd);
    }

    private void CloseCard_Click(object sender, RoutedEventArgs e)
    {
        var view = ViewFrom(sender);
        if (view is not null)
            PinService.SendCommand(view.Item.Hwnd, PinCommand.Close);
    }

    private void RestoreCard_Click(object sender, RoutedEventArgs e)
    {
        var view = ViewFrom(sender);
        if (view is null)
            return;
        PinService.SendCommand(view.Item.Hwnd, PinCommand.RestoreInteraction);
        PinService.SendCommand(view.Item.Hwnd, PinCommand.Show);
        PinService.SendCommand(view.Item.Hwnd, PinCommand.Focus);
    }

    private void HideAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var pin in PinService.SnapshotPins())
            PinService.SendCommand(pin.Hwnd, PinCommand.Hide);
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var pin in PinService.SnapshotPins())
            PinService.SendCommand(pin.Hwnd, PinCommand.Show);
    }

    private void CloseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var pin in PinService.SnapshotPins())
            PinService.SendCommand(pin.Hwnd, PinCommand.Close);
    }
}
