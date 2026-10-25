using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using PrtScr.App.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.App.Views;

/// <summary>贴图窗口：命令分发、穿透、覆盖层与导航提示。</summary>
public sealed unsafe partial class NativePinWindow
{
    private void SetOpacity(double value)
    {
        _opacity = value;
        _needsRedrawAfterZoom = true;
        RenderAndPresent(highQuality: true);
        PublishPinState();
    }

    /// <summary>管理器发来的贴图命令：只在这个贴图线程执行，避免跨线程碰窗口字段。</summary>
    private void HandlePinCommand(PinCommand command)
    {
        switch (command)
        {
            case PinCommand.Focus:
                if (_hidden)
                {
                    _hidden = false;
                    _ = PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
                }
                // 管理器打开时所有贴图已被临时降为 NOTOPMOST；这里只提到普通窗口顶端，
                // 不能重新宣布 TOPMOST，否则选中的贴图会盖住管理器。关闭管理器时统一恢复置顶。
                _ = PInvoke.SetWindowPos(_hwnd, default, 0, 0, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
                FlashPin(900);
                PublishPinState();
                break;

            case PinCommand.Hide:
                _hidden = true;
                _ = PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
                PublishPinState();
                break;

            case PinCommand.Show:
                _hidden = false;
                _ = PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
                _ = PInvoke.SetWindowPos(_hwnd, new HWND(-1), 0, 0, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
                PublishPinState();
                break;

            case PinCommand.ToggleLock:
                _locked = !_locked;
                if (_locked && _dragging)
                {
                    _dragging = false;
                    _ = PInvoke.ReleaseCapture();
                }
                PublishPinState();
                RefreshPinOverlays();
                break;

            case PinCommand.ToggleThrough:
                _clickThrough = !_clickThrough;
                ApplyClickThrough();
                PublishPinState();
                RefreshPinOverlays();
                break;

            case PinCommand.RestoreInteraction:
                _locked = false;
                _clickThrough = false;
                ApplyClickThrough();
                PublishPinState();
                RefreshPinOverlays();
                break;

            case PinCommand.Flash:
                FlashPin(1000);
                break;

            case PinCommand.Close:
                _ = PInvoke.DestroyWindow(_hwnd);
                break;
        }
    }

    private void ApplyClickThrough()
    {
        if (_hwnd.IsNull)
            return;
        var style = GetWindowLongPtr((nint)_hwnd.Value, GWL_EXSTYLE).ToInt64();
        style = _clickThrough ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        _ = SetWindowLongPtr((nint)_hwnd.Value, GWL_EXSTYLE, (nint)style);
        _ = PInvoke.SetWindowPos(_hwnd, default, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
    }

    private void RefreshPinOverlays()
    {
        _needsRedrawAfterZoom = true;
        RenderAndPresent(highQuality: true);
    }

    private void FlashPin(int milliseconds)
    {
        _flashUntil = Environment.TickCount64 + milliseconds;
        RefreshPinOverlays();
        _flashTimer?.Dispose();
        _flashTimer = new System.Threading.Timer(_ =>
        {
            if (!_hwnd.IsNull)
                _ = PInvoke.PostMessage(_hwnd, WM_APP_END_FLASH, default, default);
        }, null, milliseconds + 40, Timeout.Infinite);
    }

    private void ShowNavigationHint(int index, int count, string scope)
    {
        if (_winW < 140 * (_dpiScale <= 0 ? 1.0 : _dpiScale))
            return;
        _navHint = $"{index} / {count} · {scope}";
        _navHintUntil = Environment.TickCount64 + 1200;
        RefreshPinOverlays();
        _navHintTimer?.Dispose();
        _navHintTimer = new System.Threading.Timer(_ =>
        {
            if (!_hwnd.IsNull)
                _ = PInvoke.PostMessage(_hwnd, WM_APP_CLEAR_NAV_HINT, default, default);
        }, null, 1250, Timeout.Infinite);
    }

}
