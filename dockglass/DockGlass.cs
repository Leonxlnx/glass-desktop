// Tells the PRO Threads wallpaper where the dock and tray are, so it can draw
// Liquid Glass behind them (the taskbar itself is transparent), and rewrites
// geometry.js in the wallpaper's folder only when something changed.
//
// The taskbar is asked for its layout (UI Automation, served by Explorer) only
// when an app window opens or closes, plus every 3 seconds as a fallback; the
// cheap window-state check behind "mode" runs four times a second.
//
// mode: visible  the desktop shows; the wallpaper animates as usual
//       covered  a maximized window hides the desktop; only the taskbar strip
//                shows, so the wallpaper sleeps and redraws only when the dock changes
//       hidden   a fullscreen app hides the taskbar; nothing is drawn
//
// It also reloads the wallpaper when its browser crashes (see WatchLively).
//
// Build (C# 5 compiler that ships with Windows): build.cmd
// Usage: DockGlass.exe [geometry.js path]
//   Default path: the Lively (Microsoft Store) library folder "prothreads.live".

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

static class DockGlass {
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    delegate void TimerProc(IntPtr h, uint msg, IntPtr id, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, TimerProc proc);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();

    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, DWMWA_CLOAKED = 14;
    const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    const uint GW_OWNER = 4, GA_PARENT = 1;
    const int TickMs = 250, FallbackMs = 3000;
    static readonly string[] Shell = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    static IntPtr siteHwnd;
    static AutomationElement site;
    static CacheRequest cache;

    static string ClassOf(IntPtr h) { var s = new StringBuilder(64); GetClassName(h, s, 64); return s.ToString(); }
    static bool IsShell(IntPtr h) { return Array.IndexOf(Shell, ClassOf(h)) >= 0; }
    static bool Covers(RECT a, RECT b) { return a.L <= b.L && a.T <= b.T && a.R >= b.R && a.B >= b.B; }

    static string Mode(MONITORINFO mon) {
        IntPtr fg = GetForegroundWindow();
        RECT r = new RECT();
        if (fg != IntPtr.Zero && !IsShell(fg) && GetWindowRect(fg, out r) && Covers(r, mon.Monitor)) return "hidden";
        bool covered = false;
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            int cloaked;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;
            if (IsShell(h)) return true;
            RECT w = new RECT();
            if (!GetWindowRect(h, out w)) return true;
            bool onPrimary = w.L < mon.Monitor.R && w.R > mon.Monitor.L && w.T < mon.Monitor.B && w.B > mon.Monitor.T;
            if (onPrimary && (IsZoomed(h) || Covers(w, mon.Work))) { covered = true; return false; }
            return true;
        }, IntPtr.Zero);
        return covered ? "covered" : "visible";
    }

    static string Num(double v) { return Math.Round(v, 2).ToString(CultureInfo.InvariantCulture); }
    static string Box(double x, double y, double w, double h) { return "[" + Num(x) + "," + Num(y) + "," + Num(w) + "," + Num(h) + "]"; }

    static bool PrimaryMonitor(out MONITORINFO mon, out IntPtr tray) {
        mon = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) };
        tray = FindWindow("Shell_TrayWnd", null);
        return tray != IntPtr.Zero && GetMonitorInfo(MonitorFromWindow(tray, 1), ref mon);
    }

    // "screen", "dpi", "dock" and "tray" JSON fields from the taskbar's layout,
    // or null when the taskbar isn't there (Explorer restarting).
    static string Layout() {
        MONITORINFO mon;
        IntPtr tray;
        if (!PrimaryMonitor(out mon, out tray)) return null;
        IntPtr bridge = FindWindowEx(tray, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null);
        IntPtr input = bridge == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(bridge, IntPtr.Zero, "Windows.UI.Input.InputSite.WindowClass", null);
        if (input == IntPtr.Zero) return null;
        if (input != siteHwnd) { site = AutomationElement.FromHandle(input); siteHwnd = input; }
        double dpi = GetDpiForWindow(tray) / 96.0;

        AutomationElementCollection kids;
        using (cache.Activate()) kids = site.FindAll(TreeScope.Children, Condition.TrueCondition);

        Rect frame = Rect.Empty;
        double trayL = double.MaxValue, trayR = double.MinValue;
        foreach (AutomationElement k in kids) {
            Rect b = (Rect)k.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty);
            if (b.IsEmpty || double.IsInfinity(b.Width) || b.Width <= 0) continue;
            string id = (string)k.GetCachedPropertyValue(AutomationElement.AutomationIdProperty);
            string cls = (string)k.GetCachedPropertyValue(AutomationElement.ClassNameProperty);
            if (id == "TaskbarFrame") frame = b;
            else if (cls != null && cls.StartsWith("SystemTray.")) { trayL = Math.Min(trayL, b.Left); trayR = Math.Max(trayR, b.Right); }
        }
        if (frame.IsEmpty) return null;

        // Offsets from the Styler theme (windhawk/windows-11-taskbar-styler.yaml): the dock's
        // background sits 1px inside the frame, the tray pill has 8px padding.
        double ox = mon.Monitor.L, oy = mon.Monitor.T, h = frame.Height;
        var sb = new StringBuilder();
        sb.Append("\"screen\":[").Append(mon.Monitor.R - mon.Monitor.L).Append(',').Append(mon.Monitor.B - mon.Monitor.T).Append(']');
        sb.Append(",\"dpi\":").Append(Num(dpi));
        sb.Append(",\"dock\":").Append(Box(frame.Left + dpi - ox, frame.Top - oy, frame.Width - 2 * dpi, h));
        if (trayR > trayL) sb.Append(",\"tray\":").Append(Box(trayL - 8 * dpi - ox, frame.Top - oy, trayR - trayL + 16 * dpi, h));
        return sb.ToString();
    }

    static string outPath, layout, written;
    static long rev;
    static int nextLayout; // Environment.TickCount when the taskbar layout is next read
    static readonly System.Collections.Generic.HashSet<IntPtr> appWindows = new System.Collections.Generic.HashSet<IntPtr>();
    static WinEventProc onWinEvent;
    static TimerProc onTick;

    // A window that gets a taskbar button: top-level, unowned, not a tool window.
    static bool IsAppWindow(IntPtr h) {
        return GetAncestor(h, GA_PARENT) == GetDesktopWindow() && GetWindow(h, GW_OWNER) == IntPtr.Zero
            && (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0;
    }

    // Apps opening or closing change the dock's width; read the layout once the
    // taskbar has added or removed the button, and again after its animation.
    static void WinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
        bool relevant;
        if (ev == EVENT_OBJECT_SHOW) relevant = IsAppWindow(hwnd) && appWindows.Add(hwnd);
        else relevant = appWindows.Remove(hwnd);
        if (!relevant) return;
        nextLayout = Environment.TickCount + 300;
        pendingSettle = true;
    }

    static bool pendingSettle;

    // Lively leaves a crashed wallpaper browser on its sad-face page and only
    // logs "CoreWebView2 process failed" (seen when the GPU resets on waking from
    // sleep), so the helper follows Lively's log and sets the wallpaper again.
    const string LivelyData = @"Packages\12030rocksdanister.LivelyWallpaper_97hta09mmv6hy\LocalCache\Local\Lively Wallpaper";
    const int LogCheckMs = 3000, ReloadDelayMs = 1000, ReloadGapMs = 30000;
    static string logDir, logFile, wallpaper;
    static long logPos;
    static int nextLogCheck, reloadAt, lastReload;
    static bool reloadPending, everReloaded;

    // Notes the wallpaper Lively last set; true when its browser failed.
    static bool FollowLog(string text) {
        bool failed = false;
        foreach (string line in text.Split('\n')) {
            int set = line.IndexOf("Setting wallpaper: ", StringComparison.Ordinal);
            int bar = line.LastIndexOf(" | ", StringComparison.Ordinal);
            if (set >= 0 && bar > set) wallpaper = Path.GetDirectoryName(line.Substring(bar + 3).Trim());
            if (line.IndexOf("CoreWebView2 process failed", StringComparison.Ordinal) >= 0) failed = true;
        }
        return failed;
    }

    static void WatchLively(int now) {
        if (reloadPending && now - reloadAt >= 0) {
            reloadPending = false;
            everReloaded = true;
            lastReload = now;
            Reload(wallpaper);
        }
        if (now - nextLogCheck < 0) return;
        nextLogCheck = now + LogCheckMs;

        // Lively starts a new log file each time it launches.
        string newest = null;
        DateTime newestTime = DateTime.MinValue;
        foreach (string f in Directory.GetFiles(logDir, "*.txt")) {
            DateTime t = File.GetLastWriteTimeUtc(f);
            if (t > newestTime) { newest = f; newestTime = t; }
        }
        if (newest == null) return;
        bool catchUp = logFile == null; // failures from before the helper started are old news
        if (newest != logFile) { logFile = newest; logPos = 0; }

        byte[] buf;
        using (var s = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            if (s.Length < logPos) logPos = 0;
            if (s.Length == logPos) return;
            s.Position = logPos;
            buf = new byte[s.Length - logPos];
            int n = 0, got;
            while (n < buf.Length && (got = s.Read(buf, n, buf.Length - n)) > 0) n += got;
            Array.Resize(ref buf, n);
        }
        int end = Array.LastIndexOf(buf, (byte)'\n') + 1; // a line still being written waits for the next check
        if (end == 0) return;
        logPos += end;
        if (FollowLog(Encoding.UTF8.GetString(buf, 0, end)) && !catchUp && !reloadPending
            && (!everReloaded || now - lastReload >= ReloadGapMs)) {
            reloadPending = true;
            reloadAt = now + ReloadDelayMs;
        }
    }

    // Lively's CLI has to run inside its app package to see the same files.
    static void Reload(string folder) {
        string script = "$p = Get-AppxPackage 12030rocksdanister.LivelyWallpaper; "
            + "Invoke-CommandInDesktopPackage -PackageFamilyName $p.PackageFamilyName -AppId App "
            + "-Command (Join-Path $p.InstallLocation 'Build\\Lively.exe') "
            + "-Args 'setwp --file \"" + folder.Replace("'", "''") + "\"'";
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        using (System.Diagnostics.Process.Start(psi)) { }
    }

    static void Tick(IntPtr h, uint msg, IntPtr id, uint time) {
        try {
            WatchLively(Environment.TickCount);
        } catch {
            // Log busy or Lively not installed; try again at the next check.
        }
        try {
            int now = Environment.TickCount;
            if (now - nextLayout >= 0) {
                layout = Layout();
                if (pendingSettle) { pendingSettle = false; nextLayout = now + 900; }
                else nextLayout = now + FallbackMs;
            }
            MONITORINFO mon;
            IntPtr tray;
            if (layout == null || !PrimaryMonitor(out mon, out tray)) return;
            string snap = "\"mode\":\"" + Mode(mon) + "\"," + layout;
            if (snap == written) return;
            rev++;
            string tmp = outPath + ".tmp";
            File.WriteAllText(tmp, "__glassUpdate({\"rev\":" + rev + "," + snap + "});");
            if (File.Exists(outPath)) File.Replace(tmp, outPath, null); else File.Move(tmp, outPath);
            written = snap;
        } catch {
            // Explorer restarted or the file was busy; read everything again next tick.
            siteHwnd = IntPtr.Zero;
            site = null;
            layout = null;
            written = null;
            nextLayout = Environment.TickCount;
        }
    }

    [MTAThread]
    static void Main(string[] args) {
        bool created;
        using (var mutex = new Mutex(true, @"Local\DockGlassHelper", out created)) {
            if (!created) return;
            SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: physical pixels everywhere
            cache = new CacheRequest { TreeFilter = Automation.RawViewCondition };
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.AutomationIdProperty);
            cache.Add(AutomationElement.ClassNameProperty);

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            outPath = args.Length > 0 ? args[0] : Path.Combine(local, LivelyData, @"Library\wallpapers\prothreads.live\geometry.js");
            rev = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond % 1000000;
            nextLayout = Environment.TickCount;

            // The path Lively's CLI sees from inside its package, until the log names one.
            logDir = Path.Combine(local, LivelyData, "logs");
            wallpaper = Path.Combine(local, @"Lively Wallpaper\Library\wallpapers", Path.GetFileName(Path.GetDirectoryName(outPath)));
            nextLogCheck = Environment.TickCount;

            EnumWindows((h, l) => { if (IsWindowVisible(h) && IsAppWindow(h)) appWindows.Add(h); return true; }, IntPtr.Zero);
            onWinEvent = WinEvent;
            onTick = Tick;
            SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, IntPtr.Zero, onWinEvent, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            SetTimer(IntPtr.Zero, IntPtr.Zero, TickMs, onTick);
            MSG m;
            while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) {
                TranslateMessage(ref m);
                DispatchMessage(ref m);
            }
        }
    }
}
