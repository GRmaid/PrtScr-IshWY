using System.Runtime.InteropServices;

namespace PrtScr.App.Services;

/// <summary>
/// 全局 Esc 仲裁器：贴图（含标注模式）与 OCR 结果窗都可能自行处理 Esc；
/// 统一由这里决定“这一次按下到底关谁”，避免多个窗口同时响应一起消失。
///
/// 规则：
/// 1. 有贴图处于标注模式 → 只退出标注，不关任何窗口；
/// 2. 存在 OCR 结果窗时：若 OCR 与任一贴图矩形相交，关最上层（Z 序）的那个；
///    若没有任何相交，固定先关 OCR；
/// 3. 没有 OCR 时：关光标所在的最上层贴图。
/// 同一次按键 400ms 内只派发一次，防止贴图轮询与 OCR 热键各关一遍。
/// </summary>
internal static class EscRouter
{
    public const uint ExitEditMessage = 0x8000 + 13;

    private const int GW_HWNDNEXT = 2;

    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();
    private static long _lastDispatchTick;

    private sealed class Entry
    {
        public nint Hwnd;
        public bool IsOcr;
        public Func<bool>? IsEditMode;
        public Func<bool>? IsMenuOpen;
        public Action? ExitEdit;
        public Action Close = static () => { };
        public int Rank;
        public int Left, Top, Right, Bottom;
    }

    public static void RegisterPin(nint hwnd, Func<bool> isEditMode, Func<bool> isMenuOpen, Action exitEdit, Action close)
    {
        lock (Gate)
        {
            Entries.RemoveAll(e => e.Hwnd == hwnd);
            Entries.Add(new Entry
            {
                Hwnd = hwnd,
                IsOcr = false,
                IsEditMode = isEditMode,
                IsMenuOpen = isMenuOpen,
                ExitEdit = exitEdit,
                Close = close,
            });
        }
    }

    public static void RegisterOcr(nint hwnd, Action close)
    {
        lock (Gate)
        {
            Entries.RemoveAll(e => e.Hwnd == hwnd);
            Entries.Add(new Entry { Hwnd = hwnd, IsOcr = true, Close = close });
        }
    }

    public static void Unregister(nint hwnd)
    {
        if (hwnd == 0)
            return;
        lock (Gate)
            Entries.RemoveAll(e => e.Hwnd == hwnd);
    }

    /// <summary>每次检测到新的 Esc 按下时调用；内部负责去重与选择目标。</summary>
    public static void Dispatch()
    {
        lock (Gate)
        {
            var now = Environment.TickCount64;
            if (now - _lastDispatchTick < 400)
                return;
            _lastDispatchTick = now;

            var visible = new List<Entry>();
            foreach (var entry in Entries)
            {
                if (!IsWindowVisible(entry.Hwnd) || !GetWindowRect(entry.Hwnd, out var rect))
                    continue;
                entry.Left = rect.Left;
                entry.Top = rect.Top;
                entry.Right = rect.Right;
                entry.Bottom = rect.Bottom;
                visible.Add(entry);
            }

            if (visible.Count == 0)
                return;

            AssignRanks(visible);

            // 贴图右键菜单是模态的：Esc 先关菜单，不参与窗口关闭仲裁
            if (visible.Any(e => !e.IsOcr && e.IsMenuOpen?.Invoke() == true))
                return;

            // 1) 标注模式优先：只退出标注，不关贴图/OCR
            var edit = visible
                .Where(e => !e.IsOcr && e.IsEditMode?.Invoke() == true)
                .OrderBy(e => e.Rank)
                .FirstOrDefault();
            if (edit is not null)
            {
                App.Log($"EscRouter: target=pin-edit 0x{edit.Hwnd:X}");
                edit.ExitEdit?.Invoke();
                return;
            }

            var ocr = visible.Where(e => e.IsOcr).OrderBy(e => e.Rank).ToList();
            var pins = visible.Where(e => !e.IsOcr).ToList();

            // 2) 有 OCR：相交时关最上层，不相交时固定先关 OCR
            if (ocr.Count > 0)
            {
                var overlapping = new List<Entry>();
                foreach (var o in ocr)
                {
                    foreach (var p in pins)
                    {
                        if (Intersects(o, p))
                        {
                            overlapping.Add(o);
                            overlapping.Add(p);
                        }
                    }
                }

                var target = overlapping.Count > 0
                    ? overlapping.Distinct().OrderBy(e => e.Rank).First()
                    : ocr[0];

                App.Log($"EscRouter: target={(target.IsOcr ? "ocr" : "pin")} 0x{target.Hwnd:X} overlap={overlapping.Count > 0}");
                target.Close();
                return;
            }

            // 3) 没有 OCR：关光标所在的最上层贴图
            if (!GetCursorPos(out var pt))
                return;
            var underCursor = pins
                .Where(p => pt.X >= p.Left && pt.X < p.Right && pt.Y >= p.Top && pt.Y < p.Bottom)
                .OrderBy(p => p.Rank)
                .FirstOrDefault();
            if (underCursor is null)
                return;

            App.Log($"EscRouter: target=pin 0x{underCursor.Hwnd:X}");
            underCursor.Close();
        }
    }

    private static void AssignRanks(List<Entry> visible)
    {
        foreach (var entry in visible)
            entry.Rank = int.MaxValue;

        var hwnd = GetTopWindow(0);
        var rank = 0;
        while (hwnd != 0 && rank < 4096)
        {
            foreach (var entry in visible)
            {
                if (entry.Hwnd == hwnd)
                    entry.Rank = rank;
            }
            hwnd = GetWindow(hwnd, GW_HWNDNEXT);
            rank++;
        }
    }

    private static bool Intersects(Entry a, Entry b)
    {
        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right > left && bottom > top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern nint GetTopWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
