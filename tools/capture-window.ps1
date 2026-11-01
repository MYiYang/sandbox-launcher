param(
    [Parameter(Mandatory = $true)][int]$Id,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$WaitMs = 800,
    # 只抓标题里包含这段文字的窗口（模态对话框通常比主窗口小，靠面积挑选会挑错）
    [string]$Title
)

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

[StructLayout(LayoutKind.Sequential)]
public struct CAPRECT { public int Left; public int Top; public int Right; public int Bottom; }

public static class WindowCapture
{
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out CAPRECT rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);

    static IntPtr FindLargest(uint pid, string titleFilter)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((hWnd, lParam) =>
        {
            uint windowPid;
            GetWindowThreadProcessId(hWnd, out windowPid);
            if (windowPid != pid || !IsWindowVisible(hWnd)) return true;

            if (!string.IsNullOrEmpty(titleFilter))
            {
                var text = new StringBuilder(512);
                GetWindowTextW(hWnd, text, text.Capacity);
                if (text.ToString().IndexOf(titleFilter, StringComparison.OrdinalIgnoreCase) < 0) return true;
            }

            CAPRECT rect;
            if (!GetWindowRect(hWnd, out rect)) return true;
            long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            if (area > bestArea)
            {
                bestArea = area;
                best = hWnd;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public static string Capture(uint pid, string path, int waitMs, string titleFilter)
    {
        SetProcessDPIAware();
        System.Threading.Thread.Sleep(waitMs);
        var handle = FindLargest(pid, titleFilter);
        if (handle == IntPtr.Zero)
        {
            return string.IsNullOrEmpty(titleFilter)
                ? "no visible window for pid " + pid
                : "no visible window for pid " + pid + " matching title '" + titleFilter + "'";
        }

        CAPRECT rect;
        if (!GetWindowRect(handle, out rect)) return "GetWindowRect failed";
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return "bad size " + width + "x" + height;

        var title = new StringBuilder(512);
        GetWindowTextW(handle, title, title.Capacity);

        using (var bitmap = new Bitmap(width, height))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            var ok = PrintWindow(handle, hdc, 2);
            graphics.ReleaseHdc(hdc);
            if (!ok)
            {
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height));
            }

            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);
            bitmap.Save(path, ImageFormat.Png);
        }

        return "saved " + path + " (" + width + "x" + height + ") title=" + title;
    }
}
'@

$result = [WindowCapture]::Capture([uint32]$Id, $Out, $WaitMs, $Title)
Write-Output $result
