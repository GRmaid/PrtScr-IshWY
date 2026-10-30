using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PrtScr.App.Views;

/// <summary>
/// D1：编辑态关闭贴图前先确认，避免未应用的标注被静默丢弃。
/// 三个关闭入口（WM_CLOSE / 贴图菜单 / PinService 命令）统一走这里；
/// 退出编辑但保留窗口的路径不受影响。
/// </summary>
public sealed unsafe partial class NativePinWindow
{
    /// <summary>关闭贴图；编辑中且还有未应用标注时先弹确认。返回是否真的关闭。</summary>
    private bool _closeConfirming;

    private bool TryClosePinWindow(string source)
    {
        // 确认框自身会泵消息；用户连点关闭时不叠第二个对话框。
        if (_closeConfirming)
            return false;

        var pending = _editMode && HasPendingAnnotations
            ? (_document?.Count ?? 0) + (_draft is null ? 0 : 1)
            : 0;
        if (pending == 0)
        {
            PinSnapGuide.Hide();
            App.Log($"Pin: close source={source} edit={_editMode} pending=0");
            _ = PInvoke.DestroyWindow(_hwnd);
            return true;
        }

        var message = $"这张贴图正在标注，还有 {pending} 条标注尚未应用。\n\n放弃这些标注并关闭贴图吗？";
        var discard = false;
        _closeConfirming = true;
        try
        {
            fixed (char* body = message)
            fixed (char* caption = "关闭贴图")
            {
                var result = PInvoke.MessageBox(
                    _hwnd,
                    new PCWSTR(body),
                    new PCWSTR(caption),
                    MESSAGEBOX_STYLE.MB_YESNO | MESSAGEBOX_STYLE.MB_ICONWARNING |
                    MESSAGEBOX_STYLE.MB_DEFBUTTON2 | MESSAGEBOX_STYLE.MB_SETFOREGROUND);
                discard = result == MESSAGEBOX_RESULT.IDYES;
            }
        }
        finally
        {
            _closeConfirming = false;
        }

        if (!discard)
        {
            // Esc、点「否」、点右上角关闭都走这里：留在编辑态，标注不动。
            App.Log($"Pin: close canceled source={source} pending={pending}");
            ShowHint("已取消关闭，标注仍在编辑中", 1800);
            _ = PInvoke.SetFocus(_hwnd);
            return false;
        }

        App.Log($"Pin: close discarded source={source} pending={pending}");
        PinSnapGuide.Hide();
        _ = PInvoke.DestroyWindow(_hwnd);
        return true;
    }
}
