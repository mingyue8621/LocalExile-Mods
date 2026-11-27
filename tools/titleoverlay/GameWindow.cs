using System.Diagnostics;

namespace TitleOverlay;

internal static class GameWindow
{
    /// <summary>返回前台窗口所属进程名（不含 .exe），取不到返回空串。</summary>
    public static string GetForegroundProcessName()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return "";
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
        {
            return "";
        }

        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>POE 游戏窗口的类名（客户端本体、Game.exe、Game.patched.exe 都是这个）。</summary>
    public const string PoeWindowClass = "POEWindowClass";

    /// <summary>
    /// 找游戏窗口。先用窗口类名 POEWindowClass 找（客户端叫什么 exe 都不影响），
    /// 找不到再退回按进程名匹配。返回 IntPtr.Zero 表示没有。
    /// </summary>
    public static IntPtr FindGameWindow(IEnumerable<string> processNames)
    {
        // ① 前台窗口本身就是 POE，最快也最常见。
        var foreground = NativeMethods.GetForegroundWindow();
        if (IsUsable(foreground) && GetWindowClass(foreground) == PoeWindowClass)
        {
            return foreground;
        }

        // ② 按类名枚举：不论进程名是 Game.exe 还是 Game.patched.exe。
        IntPtr best = IntPtr.Zero;
        int bestArea = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!IsUsable(hwnd) || GetWindowClass(hwnd) != PoeWindowClass)
            {
                return true;
            }

            var rect = GetClientScreenRect(hwnd);
            int area = rect.Width * rect.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        if (bestArea >= 320 * 200)
        {
            return best;
        }

        // ③ 兜底：按进程名。
        return FindByProcessName(processNames);
    }

    private static IntPtr FindByProcessName(IEnumerable<string> processNames)
    {
        var names = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0)
        {
            return IntPtr.Zero;
        }

        foreach (var name in names)
        {
            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (var p in procs)
            {
                try
                {
                    var hwnd = p.MainWindowHandle;
                    if (!IsUsable(hwnd))
                    {
                        // 全屏 / 无边框窗口的 MainWindowHandle 常是 0，退回到自己枚举顶层窗口。
                        hwnd = FindWindowOfProcess(p.Id);
                    }

                    if (IsUsable(hwnd))
                    {
                        return hwnd;
                    }
                }
                catch
                {
                    // 进程可能已退出。
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>取窗口类名，失败返回空串。</summary>
    public static string GetWindowClass(IntPtr hwnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        return NativeMethods.GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    /// <summary>指定窗口是否持有前台焦点（考虑覆盖层自己不抢焦点）。</summary>
    public static bool IsWindowForeground(IntPtr hwnd)
    {
        var fore = NativeMethods.GetForegroundWindow();
        if (fore == IntPtr.Zero || hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (fore == hwnd)
        {
            return true;
        }

        NativeMethods.GetWindowThreadProcessId(fore, out var forePid);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var gamePid);
        return forePid != 0 && forePid == gamePid;
    }

    /// <summary>枚举该进程的顶层窗口，取最大的那个可见窗口。</summary>
    internal static IntPtr FindWindowOfProcess(int pid)
    {
        IntPtr best = IntPtr.Zero;
        int bestArea = 0;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != (uint)pid || !IsUsable(hwnd))
            {
                return true;
            }

            var rect = GetClientScreenRect(hwnd);
            int area = rect.Width * rect.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return bestArea >= 320 * 200 ? best : IntPtr.Zero;
    }

    private static bool IsUsable(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero
               && NativeMethods.IsWindowVisible(hwnd)
               && !NativeMethods.IsIconic(hwnd);
    }

    /// <summary>窗口客户区在屏幕上的矩形（物理像素）。</summary>
    public static Rectangle GetClientScreenRect(IntPtr hwnd)
    {
        if (!NativeMethods.GetClientRect(hwnd, out var rc))
        {
            return Rectangle.Empty;
        }

        var origin = new NativeMethods.POINT(0, 0);
        if (!NativeMethods.ClientToScreen(hwnd, ref origin))
        {
            return Rectangle.Empty;
        }

        return new Rectangle(origin.X, origin.Y, rc.Width, rc.Height);
    }
}
