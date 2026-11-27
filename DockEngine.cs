using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace TaskbarDock {
    public class DockEngine {
        private class TrayState {
            public int LastLeft = -9999;
            public int LastRight = -9999;
            public int LastTop = -9999;
            public int LastBottom = -9999;
            public int LastTrayTop = -9999;
            public int LastTrayNotifyLeft = -9999;
            public int LastTrayNotifyRight = -9999;
            public int LastMode = -1;
            public int LastRadius = -1;
            public int LastOpacity = -1;
            public int LastMarginBottom = -1;
            public bool LastHasButton = false;
            public bool IsCustomRgnApplied = false;

            public bool IsFullscreenHidden = false;
            public bool IsHoverRevealed = false;
            public long LastInDockZoneTick = 0;

            public bool HasCachedBounds = false;
            public int CachedLeft = -9999;
            public int CachedRight = -9999;
            public int CachedTop = -9999;
            public int CachedBottom = -9999;
            public int CachedTrayTop = -9999;
            public int CachedNotifyLeft = -1;
            public int CachedNotifyRight = -1;

            public void SaveCache() {
                if (IsCustomRgnApplied && LastRight > LastLeft && LastBottom > LastTop) {
                    HasCachedBounds = true;
                    CachedLeft = LastLeft;
                    CachedRight = LastRight;
                    CachedTop = LastTop;
                    CachedBottom = LastBottom;
                    CachedTrayTop = LastTrayTop;
                    CachedNotifyLeft = LastTrayNotifyLeft;
                    CachedNotifyRight = LastTrayNotifyRight;
                }
            }

            public void Reset() {
                LastLeft = -9999;
                LastRight = -9999;
                LastTop = -9999;
                LastBottom = -9999;
                LastTrayTop = -9999;
                LastTrayNotifyLeft = -9999;
                LastTrayNotifyRight = -9999;
                LastMode = -1;
                LastRadius = -1;
                LastOpacity = -1;
                LastMarginBottom = -1;
                LastHasButton = false;
                IsCustomRgnApplied = false;
            }
        }

        private TrayState _mainTrayState = new TrayState();
        private Dictionary<IntPtr, TrayState> _secondaryStates = new Dictionary<IntPtr, TrayState>();
        private Condition _trueCond = Condition.TrueCondition;

        private Thread _workerThread;
        private AutoResetEvent _wakeEvent = new AutoResetEvent(false);
        private volatile bool _running = false;
        private Config _config;
        private readonly object _lock = new object();

        private DockOverlayWindow _overlay;
        private Thread _overlayThread;
        private Action _onOpenSettings;

        private bool _isFullscreenHidden = false;
        private bool _isHoverRevealed = false;
        private bool _lastWindowFullscreen = false;
        private bool _isSearchOpen = false;
        private volatile bool _anySecondaryFullscreenOrHover = false;
        private IntPtr _lastLoggedSearchHwnd = IntPtr.Zero;
        private long _lastInDockZoneTick = 0;
        private long _lastWorkAreaEnforceTick = 0;
        private Dictionary<string, NativeMethods.RECT> _originalWorkAreas = new Dictionary<string, NativeMethods.RECT>();
        private bool _workAreaModified = false;

        public DockEngine(Action onOpenSettings = null) {
            _onOpenSettings = onOpenSettings;
            SaveOriginalWorkArea();
        }

        private int GetFallbackTrayHeight() {
            int fallbackTrayH = 48;
            try {
                IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
                if (tray != IntPtr.Zero) {
                    NativeMethods.RECT tr;
                    if (NativeMethods.GetWindowRect(tray, out tr) && tr.Height > 10 && tr.Height < 150) {
                        fallbackTrayH = tr.Height;
                    }
                }
            } catch { }
            return fallbackTrayH;
        }

        private void SaveOriginalWorkArea() {
            try {
                int fallbackTrayH = GetFallbackTrayHeight();
                foreach (var screen in Screen.AllScreens) {
                    var sb = screen.Bounds;
                    var wa = screen.WorkingArea;
                    bool isFullBounds = (wa.Left <= sb.Left && wa.Top <= sb.Top && wa.Right >= sb.Right && wa.Bottom >= sb.Bottom);
                    if (!isFullBounds) {
                        _originalWorkAreas[screen.DeviceName] = new NativeMethods.RECT(wa.Left, wa.Top, wa.Right, wa.Bottom);
                    } else if (!_originalWorkAreas.ContainsKey(screen.DeviceName)) {
                        _originalWorkAreas[screen.DeviceName] = new NativeMethods.RECT(sb.Left, sb.Top, sb.Right, sb.Bottom - fallbackTrayH);
                    }
                }
            } catch { }
        }

        public void Start(Config config) {
            lock (_lock) {
                Logger.Log("DockEngine.Start entering...");
                _config = config;
                if (_running) return;
                _running = true;

                ManualResetEvent overlayReady = new ManualResetEvent(false);
                _overlayThread = new Thread(() => {
                    try {
                        IntPtr hDesk = NativeMethods.OpenDesktop("Default", 0, false, 0x01FF);
                        if (hDesk != IntPtr.Zero) {
                            bool ok = NativeMethods.SetThreadDesktop(hDesk);
                            Logger.Log("OverlayThread SetThreadDesktop: " + ok);
                        }
                        _overlay = new DockOverlayWindow(_config, _onOpenSettings);
                        IntPtr h = _overlay.Handle;
                        Logger.Log("Overlay window created on OverlayThread: " + h.ToString("X"));
                        overlayReady.Set();
                        Application.Run(_overlay);
                    } catch (Exception ex) {
                        Logger.Log("OverlayThread error: " + ex);
                        overlayReady.Set();
                    }
                });
                _overlayThread.SetApartmentState(ApartmentState.STA);
                _overlayThread.IsBackground = true;
                _overlayThread.Name = "DockOverlayThread";
                _overlayThread.Start();
                overlayReady.WaitOne(2000);

                _workerThread = new Thread(WorkerLoop);
                _workerThread.IsBackground = true;
                _workerThread.Name = "DockEngineWorker";
                _workerThread.Start();
                Logger.Log("Worker thread started.");
            }
        }

        public void TriggerUpdate() {
            _wakeEvent.Set();
        }

        public void Stop() {
            lock (_lock) {
                if (!_running) return;
                _running = false;
                _wakeEvent.Set();
            }
            if (_overlay != null && !_overlay.IsDisposed) {
                try {
                    if (_overlay.InvokeRequired) {
                        _overlay.Invoke(new Action(() => {
                            _overlay.Close();
                            _overlay.Dispose();
                        }));
                    } else {
                        _overlay.Close();
                        _overlay.Dispose();
                    }
                } catch { }
            }
            if (_workerThread != null && _workerThread.IsAlive) {
                _workerThread.Join(1500);
            }
            RestoreAll();
        }

        private void WorkerLoop() {
            Logger.Log("WorkerLoop running...");
            try {
                IntPtr hDesk = NativeMethods.OpenDesktop("Default", 0, false, 0x01FF);
                if (hDesk != IntPtr.Zero) {
                    bool ok = NativeMethods.SetThreadDesktop(hDesk);
                    Logger.Log("WorkerLoop SetThreadDesktop: " + ok);
                }
            } catch (Exception ex) {
                Logger.Log("WorkerLoop desktop access exception: " + ex.Message);
            }

            while (_running) {
                try {
                    Config cfg = _config;
                    if (cfg != null) {
                        Update(cfg);
                    }
                } catch (Exception ex) {
                    Logger.Log("WorkerLoop update error: " + ex.Message);
                }

                int interval = 200;
                if (_config != null && _config.PollIntervalMs >= 50) {
                    interval = _config.PollIntervalMs;
                }
                if (_config != null && _config.Enabled &&
                    (_isSearchOpen || (_config.AutoHideInFullscreen && (_lastWindowFullscreen || _isFullscreenHidden || _isHoverRevealed || _anySecondaryFullscreenOrHover)))) {
                    interval = Math.Min(interval, 50);
                }
                _wakeEvent.WaitOne(interval);
            }

            RestoreAll();
        }

        public void Update(Config config) {
            if (!config.Enabled) {
                RestoreAll();
                return;
            }

            IntPtr primaryTray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (primaryTray == IntPtr.Zero) return;

            // Keep native Explorer auto-hide disabled so Windows never slides the dock off-screen on its own.
            EnsureSystemAutoHideDisabled(primaryTray);

            // 1. Float Over Windows (Work Area Management) - must run even when dock is auto-hidden!
            UpdateWorkArea(config.FloatOverWindows, primaryTray);

            // 2. Auto-Hide when foreground window is maximized/fullscreen, reveal on bottom screen edge hover
            bool isShellDockActive = false;
            bool isStartOrSearchOpen = false;
            IntPtr searchHwnd = IntPtr.Zero;
            bool isFullOrMax = config.AutoHideInFullscreen && IsForegroundFullscreenOrMaximized(primaryTray, out isShellDockActive, out isStartOrSearchOpen, out searchHwnd);
            if (!config.AutoHideInFullscreen) {
                DetectSearchForeground(out searchHwnd);
            }
            _isSearchOpen = (searchHwnd != IntPtr.Zero);
            if (_overlay != null && !_overlay.IsDisposed && _overlay.IsTrayBarOpen) {
                isShellDockActive = true;
            }

            if (isFullOrMax) {
                var pb = Screen.PrimaryScreen.Bounds;
                NativeMethods.RECT trayRect;
                int trayH = 48;
                int dockTop = pb.Bottom - trayH;
                if (NativeMethods.GetWindowRect(primaryTray, out trayRect) && trayRect.Height > 10 && trayRect.Height < 150) {
                    trayH = trayRect.Height;
                    if (trayRect.Top > pb.Top && trayRect.Top < pb.Bottom) {
                        dockTop = trayRect.Top;
                    } else {
                        dockTop = pb.Bottom - trayH;
                    }
                }

                long now = Environment.TickCount & 0x7FFFFFFF;
                NativeMethods.POINT pt;
                if (isStartOrSearchOpen) {
                    if (!_isHoverRevealed) Logger.Log("Revealing dock for Start Menu / Search.");
                    _isHoverRevealed = true;
                    _lastInDockZoneTick = now;
                } else if (NativeMethods.GetCursorPos(out pt)) {
                    bool inPrimaryX = (pt.X >= pb.Left && pt.X < pb.Right);
                    bool atBottomEdge = inPrimaryX && pt.Y >= pb.Bottom - 5 && pt.Y <= pb.Bottom;
                    bool inDockZone = inPrimaryX && pt.Y >= dockTop - 16 && pt.Y <= pb.Bottom;

                    if (atBottomEdge) {
                        if (!_isHoverRevealed) Logger.Log("Hover reveal triggered at bottom edge.");
                        _isHoverRevealed = true;
                        _lastInDockZoneTick = now;
                    } else if (isShellDockActive || (_isHoverRevealed && inDockZone)) {
                        _lastInDockZoneTick = now;
                    } else if (_isHoverRevealed) {
                        long elapsed = (now - _lastInDockZoneTick) & 0x7FFFFFFF;
                        if (elapsed >= 280) {
                            Logger.Log("Hover hide triggered after cursor left dock zone.");
                            _isHoverRevealed = false;
                        }
                    }
                } else if (isShellDockActive) {
                    _lastInDockZoneTick = now;
                }

                if (!_isHoverRevealed) {
                    if (!_isFullscreenHidden) {
                        Logger.Log("Hiding dock (foreground window is fullscreen/maximized).");
                        _mainTrayState.SaveCache();
                        AnimateDock(primaryTray, _mainTrayState, config, true, show: false);
                        HidePrimaryTrayVisuals(primaryTray);
                        HideOverlay();
                        _isFullscreenHidden = true;
                    }
                    UpdateSecondaryTaskbars(config);
                    return;
                } else if (_isFullscreenHidden) {
                    Logger.Log("Revealing dock immediately.");
                    RevealPrimaryDockFast(primaryTray, config);
                }
            } else {
                _isHoverRevealed = false;
                if (_isFullscreenHidden) {
                    Logger.Log("Restoring dock (no foreground fullscreen/maximized window).");
                    RevealPrimaryDockFast(primaryTray, config);
                }
            }

            // 3. Z-order management: keep dock above normal and fullscreen windows when visible
            EnsureTaskbarZOrder(primaryTray, config, forceTopMost: false);

            // 3b. Keep Windows Search flyout positioned cleanly above the dock (like Start Menu)
            if (searchHwnd != IntPtr.Zero) {
                AdjustSearchWindowAboveDock(searchHwnd, primaryTray, config);
            }

            // 4. Update Primary Taskbar & Glass Overlay
            UpdateSingleTaskbar(primaryTray, _mainTrayState, config, isPrimary: true);

            // 5. Update Secondary Taskbars
            UpdateSecondaryTaskbars(config);
        }

        private void RevealPrimaryDockFast(IntPtr primaryTray, Config config) {
            _isFullscreenHidden = false;
            _mainTrayState.Reset();
            NativeMethods.SetLayeredWindowAttributes(primaryTray, 0, 0, NativeMethods.LWA_ALPHA);
            EnsureTaskbarZOrder(primaryTray, config, forceTopMost: true);

            if (_mainTrayState.HasCachedBounds) {
                NativeMethods.RECT trayRect;
                if (NativeMethods.GetWindowRect(primaryTray, out trayRect) && trayRect.Width > 0 && trayRect.Height > 0) {
                    ApplyTaskbarVisuals(
                        primaryTray, _mainTrayState, config, true,
                        trayRect, _mainTrayState.CachedLeft, _mainTrayState.CachedRight,
                        _mainTrayState.CachedTop, _mainTrayState.CachedBottom,
                        _mainTrayState.CachedNotifyLeft, _mainTrayState.CachedNotifyRight,
                        0);
                    AnimateDock(primaryTray, _mainTrayState, config, true, show: true);
                    return;
                }
            }

            UpdateSingleTaskbar(primaryTray, _mainTrayState, config, isPrimary: true, initialAlpha: 0);
            AnimateDock(primaryTray, _mainTrayState, config, true, show: true);
        }

        private void HidePrimaryTrayVisuals(IntPtr primaryTray) {
            try {
                int exStyle = NativeMethods.GetWindowLong(primaryTray, NativeMethods.GWL_EXSTYLE);
                if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0) {
                    NativeMethods.SetWindowLong(primaryTray, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_LAYERED);
                }
                NativeMethods.SetLayeredWindowAttributes(primaryTray, 0, 0, NativeMethods.LWA_ALPHA);
                IntPtr emptyRgn = NativeMethods.CreateRectRgn(0, 0, 0, 0);
                NativeMethods.SetWindowRgn(primaryTray, emptyRgn, true);
            } catch { }
        }

        private bool IsShellOrDockWindow(IntPtr hwnd, string cls) {
            if (_overlay != null && !_overlay.IsDisposed) {
                if (hwnd == _overlay.Handle) return true;
                IntPtr barH = _overlay.TrayBarHandle;
                if (barH != IntPtr.Zero && hwnd == barH) return true;
                IntPtr flyH = _overlay.TrayFlyoutHandle;
                if (flyH != IntPtr.Zero && hwnd == flyH) return true;
            }
            return cls == "Shell_TrayWnd" ||
                   cls == "Shell_SecondaryTrayWnd" ||
                   cls == "NotifyIconOverflowWindow" ||
                   cls == "TopLevelWindowForOverflowXamlIsland" ||
                   cls == "Xaml_WindowedPopupClass" ||
                   cls == "#32768";
        }

        private static string GetProcessNameSafe(IntPtr hwnd) {
            try {
                uint pid;
                NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return "";
                using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) {
                    return p.ProcessName ?? "";
                }
            } catch {
                return "";
            }
        }

        private static bool IsSearchProcess(string procName) {
            if (string.IsNullOrEmpty(procName)) return false;
            return procName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
                   procName.Equals("SearchApp", StringComparison.OrdinalIgnoreCase) ||
                   procName.Equals("SearchUI", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStartOrSearchProcess(string procName) {
            if (string.IsNullOrEmpty(procName)) return false;
            return procName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                   IsSearchProcess(procName) ||
                   procName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                   procName.Equals("ShellHost", StringComparison.OrdinalIgnoreCase);
        }

        private void DetectSearchForeground(out IntPtr searchHwnd) {
            searchHwnd = IntPtr.Zero;
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero && IsSearchProcess(GetProcessNameSafe(fg))) {
                searchHwnd = fg;
            }
        }

        private void AdjustSearchWindowAboveDock(IntPtr searchHwnd, IntPtr primaryTray, Config config) {
            try {
                NativeMethods.RECT sr;
                if (!NativeMethods.GetWindowRect(searchHwnd, out sr)) return;

                var pb = Screen.PrimaryScreen.Bounds;
                NativeMethods.RECT trayRect;
                int dockTop = pb.Bottom - 48 + config.MarginTop;
                if (NativeMethods.GetWindowRect(primaryTray, out trayRect) && trayRect.Height > 10 && trayRect.Height < 150) {
                    int topMargin = (_mainTrayState.LastTop > -9000) ? _mainTrayState.LastTop : config.MarginTop;
                    dockTop = trayRect.Top + topMargin;
                }

                int targetMaxBottom = dockTop - 10;
                if (searchHwnd != _lastLoggedSearchHwnd) {
                    _lastLoggedSearchHwnd = searchHwnd;
                    Logger.Log(string.Format("Search window detected: rect=[{0},{1},{2},{3}] targetMaxBottom={4}",
                        sr.Left, sr.Top, sr.Right, sr.Bottom, targetMaxBottom));
                }

                if (sr.Bottom > targetMaxBottom) {
                    int shiftY = sr.Bottom - targetMaxBottom;
                    if (shiftY > 2 && shiftY < 220) {
                        int newTop = sr.Top - shiftY;
                        if (sr.Height < pb.Height - 20 && newTop < pb.Top + 8) {
                            newTop = pb.Top + 8;
                        }
                        NativeMethods.SetWindowPos(
                            searchHwnd, IntPtr.Zero,
                            sr.Left, newTop, 0, 0,
                            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                    }
                }
            } catch { }
        }

        private bool IsForegroundFullscreenOrMaximized(IntPtr primaryTray, out bool isShellDockActive, out bool isStartOrSearchOpen, out IntPtr searchHwnd) {
            isShellDockActive = false;
            isStartOrSearchOpen = false;
            searchHwnd = IntPtr.Zero;
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == NativeMethods.GetDesktopWindow() || fg == NativeMethods.GetShellWindow()) {
                _lastWindowFullscreen = false;
                return false;
            }

            if (_overlay != null && !_overlay.IsDisposed && fg == _overlay.Handle) {
                isShellDockActive = true;
                return _lastWindowFullscreen;
            }

            StringBuilder sb = new StringBuilder(64);
            NativeMethods.GetClassName(fg, sb, sb.Capacity);
            string cls = sb.ToString();

            if (cls == "Progman" || cls == "WorkerW") {
                _lastWindowFullscreen = false;
                return false;
            }

            if (IsShellOrDockWindow(fg, cls)) {
                isShellDockActive = true;
                if (_isFullscreenHidden && (fg == primaryTray || cls == "Xaml_WindowedPopupClass")) {
                    isStartOrSearchOpen = true;
                }
                return _lastWindowFullscreen;
            }

            string procName = GetProcessNameSafe(fg);
            if (IsStartOrSearchProcess(procName)) {
                isShellDockActive = true;
                isStartOrSearchOpen = true;
                if (IsSearchProcess(procName)) searchHwnd = fg;
                return _lastWindowFullscreen;
            }

            var pb = Screen.PrimaryScreen.Bounds;

            if (cls == "Windows.UI.Core.CoreWindow") {
                NativeMethods.RECT coreRect;
                if (NativeMethods.GetWindowRect(fg, out coreRect) &&
                    coreRect.Left <= pb.Left && coreRect.Top <= pb.Top &&
                    coreRect.Right >= pb.Right && coreRect.Bottom >= pb.Bottom - 2) {
                    _lastWindowFullscreen = true;
                    return true;
                }
                isShellDockActive = true;
                isStartOrSearchOpen = true;
                return _lastWindowFullscreen;
            }

            if (NativeMethods.IsIconic(fg)) {
                _lastWindowFullscreen = false;
                return false;
            }

            bool isFull = false;
            try {
                if (NativeMethods.IsZoomed(fg)) {
                    isFull = Screen.FromHandle(fg).Primary;
                } else {
                    NativeMethods.RECT r;
                    if (NativeMethods.GetWindowRect(fg, out r)) {
                        isFull = (r.Left <= pb.Left && r.Top <= pb.Top && r.Right >= pb.Right && r.Bottom >= pb.Bottom - 2);
                    }
                }
            } catch {
                isFull = false;
            }

            _lastWindowFullscreen = isFull;
            return isFull;
        }

        private void EnsureTaskbarZOrder(IntPtr primaryTray, Config config, bool forceTopMost) {
            int trayEx = NativeMethods.GetWindowLong(primaryTray, NativeMethods.GWL_EXSTYLE);
            bool isTrayTopMost = (trayEx & NativeMethods.WS_EX_TOPMOST) != 0;

            if (!config.FloatOverWindows) {
                if (isTrayTopMost) {
                    NativeMethods.SetWindowPos(primaryTray, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                }
                return;
            }

            bool needTrayTopMost = forceTopMost || !isTrayTopMost;
            if (!needTrayTopMost) {
                IntPtr fg = NativeMethods.GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != primaryTray) {
                    StringBuilder sb = new StringBuilder(64);
                    NativeMethods.GetClassName(fg, sb, sb.Capacity);
                    string cls = sb.ToString();
                    if (!IsShellOrDockWindow(fg, cls) && (cls != "Windows.UI.Core.CoreWindow" || IsStartOrSearchProcess(GetProcessNameSafe(fg)))) {
                        IntPtr prev = NativeMethods.GetWindow(primaryTray, NativeMethods.GW_HWNDPREV);
                        for (int i = 0; i < 25 && prev != IntPtr.Zero; i++) {
                            if (prev == fg) {
                                needTrayTopMost = true;
                                break;
                            }
                            prev = NativeMethods.GetWindow(prev, NativeMethods.GW_HWNDPREV);
                        }
                    }
                }
            }

            if (needTrayTopMost) {
                NativeMethods.SetWindowPos(primaryTray, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }

            if (_overlay != null && !_overlay.IsDisposed && _overlay.Visible) {
                bool needOverlayTopMost = needTrayTopMost;
                if (!needOverlayTopMost) {
                    IntPtr prev = NativeMethods.GetWindow(_overlay.Handle, NativeMethods.GW_HWNDPREV);
                    for (int i = 0; i < 10 && prev != IntPtr.Zero; i++) {
                        if (prev == primaryTray) {
                            needOverlayTopMost = true;
                            break;
                        }
                        prev = NativeMethods.GetWindow(prev, NativeMethods.GW_HWNDPREV);
                    }
                }
                if (needOverlayTopMost) {
                    try {
                        _overlay.BeginInvoke(new Action(() => {
                            if (_overlay != null && !_overlay.IsDisposed) _overlay.EnsureTopMost();
                        }));
                    } catch { }
                }
            }
        }

        private void AnimateDock(IntPtr trayHwnd, TrayState state, Config config, bool isPrimary, bool show) {
            if (!state.IsCustomRgnApplied) return;
            int rLeft = state.LastLeft;
            int rRight = state.LastRight;
            int rTop = state.LastTop;
            int rBottom = state.LastBottom;
            int rad = config.CornerRadius * 2;
            int visualMargin = Math.Max(12, config.PaddingHorizontal);

            int steps = show ? 8 : 6;
            int delayMs = 12;
            int minH = 6;
            int fullH = Math.Max(minH, rBottom - rTop);

            for (int i = 1; i <= steps; i++) {
                double p = (double)i / steps;
                double eased = show
                    ? 1.0 - Math.Pow(1.0 - p, 3.0) // ease-out cubic
                    : Math.Pow(p, 2.0);            // ease-in quad

                double factor = show ? eased : (1.0 - eased);
                int curH = minH + (int)Math.Round((fullH - minH) * factor);
                int curTop = rBottom - curH;
                byte curAlpha = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(config.TaskbarOpacity * factor)));

                IntPtr rgn = NativeMethods.CreateRoundRectRgn(rLeft, curTop, rRight, rBottom, rad, rad);
                if (isPrimary && config.ShowQuickSettingsButton) {
                    int btnSize = 26;
                    int bX = rRight - visualMargin - btnSize;
                    int bY = rTop + (rBottom - rTop - btnSize) / 2;
                    IntPtr btnHole = NativeMethods.CreateRoundRectRgn(bX + 2, bY + 2, bX + btnSize - 2, bY + btnSize - 2, btnSize - 4, btnSize - 4);
                    IntPtr diffRgn = NativeMethods.CreateRectRgn(0, 0, 0, 0);
                    NativeMethods.CombineRgn(diffRgn, rgn, btnHole, 4);
                    NativeMethods.DeleteObject(btnHole);
                    NativeMethods.DeleteObject(rgn);
                    rgn = diffRgn;
                }

                NativeMethods.SetLayeredWindowAttributes(trayHwnd, 0, curAlpha, NativeMethods.LWA_ALPHA);
                NativeMethods.SetWindowRgn(trayHwnd, rgn, true);

                if (isPrimary && _overlay != null && !_overlay.IsDisposed && (show || _overlay.Visible)) {
                    double btnOp = factor;
                    int btnOffset = (int)Math.Round(14.0 * (1.0 - factor));
                    try {
                        _overlay.BeginInvoke(new Action(() => {
                            if (_overlay != null && !_overlay.IsDisposed) {
                                _overlay.SetButtonOpacity(btnOp);
                                _overlay.SetVerticalOffset(btnOffset);
                            }
                        }));
                    } catch { }
                }
                Thread.Sleep(delayMs);
            }

            if (show) {
                NativeMethods.SetLayeredWindowAttributes(trayHwnd, 0, (byte)config.TaskbarOpacity, NativeMethods.LWA_ALPHA);
                state.Reset();
                UpdateSingleTaskbar(trayHwnd, state, config, isPrimary);
                if (isPrimary) {
                    EnsureTaskbarZOrder(trayHwnd, config, forceTopMost: false);
                    if (_overlay != null && !_overlay.IsDisposed) {
                        try {
                            _overlay.BeginInvoke(new Action(() => {
                                if (_overlay != null && !_overlay.IsDisposed) {
                                    _overlay.SetButtonOpacity(1.0);
                                    _overlay.SetVerticalOffset(0);
                                    _overlay.EnsureTopMost();
                                }
                            }));
                        } catch { }
                    }
                } else {
                    EnsureSecondaryZOrder(trayHwnd, config, forceTopMost: false);
                }
            }
        }

        private void EnsureSystemAutoHideDisabled(IntPtr primaryTray) {
            try {
                if (!NativeMethods.IsWindowVisible(primaryTray)) {
                    NativeMethods.ShowWindow(primaryTray, NativeMethods.SW_SHOW);
                }
                NativeMethods.APPBARDATA abd = new NativeMethods.APPBARDATA();
                abd.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.APPBARDATA));
                abd.hWnd = primaryTray;
                uint state = NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref abd);
                if ((state & NativeMethods.ABS_AUTOHIDE) != 0) {
                    Logger.Log("Disabling native Windows taskbar auto-hide (ABS_AUTOHIDE).");
                    SetSystemTaskbarAutoHide(false);
                    _mainTrayState.Reset();
                }
            } catch { }
        }

        private void SetSystemTaskbarAutoHide(bool autoHide) {
            try {
                IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
                if (tray == IntPtr.Zero) return;
                NativeMethods.APPBARDATA abd = new NativeMethods.APPBARDATA();
                abd.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.APPBARDATA));
                abd.hWnd = tray;
                abd.lParam = (IntPtr)(autoHide ? NativeMethods.ABS_AUTOHIDE : NativeMethods.ABS_ALWAYSONTOP);
                NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETSTATE, ref abd);
            } catch { }
        }

        private void UpdateWorkArea(bool floatOver, IntPtr primaryTray) {
            try {
                if (floatOver) {
                    long now = Environment.TickCount & 0x7FFFFFFF;
                    bool firstPass = !_workAreaModified;
                    long elapsed = (now - _lastWorkAreaEnforceTick) & 0x7FFFFFFF;
                    if (!firstPass && elapsed < 400) {
                        // Still check quickly if any screen work area is non-full only every 400ms
                        return;
                    }

                    int fallbackTrayH = GetFallbackTrayHeight();
                    bool anyUpdated = false;
                    foreach (var screen in Screen.AllScreens) {
                        var sb = screen.Bounds;
                        var wa = screen.WorkingArea;
                        bool isFullWork = (wa.Left <= sb.Left && wa.Top <= sb.Top && wa.Right >= sb.Right && wa.Bottom >= sb.Bottom);
                        if (!isFullWork) {
                            if (!_originalWorkAreas.ContainsKey(screen.DeviceName)) {
                                _originalWorkAreas[screen.DeviceName] = new NativeMethods.RECT(wa.Left, wa.Top, wa.Right, wa.Bottom);
                            }
                            NativeMethods.RECT area = new NativeMethods.RECT(sb.Left, sb.Top, sb.Right, sb.Bottom);
                            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETWORKAREA, 0, ref area, firstPass ? NativeMethods.SPIF_SENDCHANGE : 0);
                            anyUpdated = true;
                        } else if (!_originalWorkAreas.ContainsKey(screen.DeviceName)) {
                            _originalWorkAreas[screen.DeviceName] = new NativeMethods.RECT(sb.Left, sb.Top, sb.Right, sb.Bottom - fallbackTrayH);
                        }
                    }
                    _workAreaModified = true;
                    if (firstPass || anyUpdated) {
                        _lastWorkAreaEnforceTick = now;
                    }
                } else {
                    if (_workAreaModified) {
                        RestoreAllWorkAreas();
                    }
                }
            } catch { }
        }

        private void RestoreAllWorkAreas() {
            try {
                foreach (var kvp in _originalWorkAreas) {
                    NativeMethods.RECT orig = kvp.Value;
                    NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETWORKAREA, 0, ref orig, NativeMethods.SPIF_SENDCHANGE);
                }
                _workAreaModified = false;
            } catch { }
        }

        private void UpdateSingleTaskbar(IntPtr trayHwnd, TrayState state, Config config, bool isPrimary, int initialAlpha = -1) {
            NativeMethods.RECT trayRect;
            if (!NativeMethods.GetWindowRect(trayHwnd, out trayRect)) return;

            int trayW = trayRect.Width;
            int trayH = trayRect.Height;
            if (trayW <= 0 || trayH <= 0) return;

            AutomationElement trayElem = null;
            try {
                trayElem = AutomationElement.FromHandle(trayHwnd);
            } catch (Exception ex) {
                Logger.Log("ElementFromHandle error: " + ex);
                return;
            }
            if (trayElem == null) return;

            AutomationElementCollection children = null;
            try {
                children = trayElem.FindAll(TreeScope.Descendants, _trueCond);
            } catch (Exception ex) {
                Logger.Log("FindAll error: " + ex);
                return;
            }
            if (children == null || children.Count == 0) return;

            double minX = double.MaxValue;
            double maxX = double.MinValue;
            int itemCount = 0;

            for (int i = 0; i < children.Count; i++) {
                try {
                    AutomationElement ch = children[i];
                    string aid = ch.Current.AutomationId;
                    string cls = ch.Current.ClassName;
                    var rect = ch.Current.BoundingRectangle;

                    bool isTaskItem = (
                        aid == "StartButton" ||
                        aid == "SearchButton" ||
                        aid == "WidgetsButton" ||
                        aid == "TaskViewButton" ||
                        (cls != null && cls.IndexOf("TaskListButton", StringComparison.OrdinalIgnoreCase) >= 0)
                    );

                    if (isTaskItem && rect.Width > 0 && rect.Height > 0) {
                        minX = Math.Min(minX, rect.Left);
                        maxX = Math.Max(maxX, rect.Right);
                        itemCount++;
                    }
                } catch { }
            }

            if (itemCount == 0 || minX >= maxX) {
                if (state.IsCustomRgnApplied) {
                    NativeMethods.SetWindowRgn(trayHwnd, IntPtr.Zero, true);
                    HideOverlay();
                    state.Reset();
                }
                return;
            }

            // Symmetrical, balanced padding: Start button has ~10px internal transparent margin.
            // We ensure equal visual padding (e.g. 16px) on both left and right edges.
            int visualMargin = Math.Max(12, config.PaddingHorizontal);
            int padLeft = Math.Max(4, visualMargin - 10); // Visual margin to Start logo = padLeft + 10 = visualMargin

            int padRight;
            if (config.ShowQuickSettingsButton && isPrimary) {
                int btnSize = 24;
                int btnGap = 12; // Gap between last app icon and button
                padRight = btnGap + btnSize + visualMargin;
            } else {
                padRight = visualMargin;
            }

            int rLeft = (int)minX - trayRect.Left - padLeft;
            int rRight = (int)maxX - trayRect.Left + padRight;
            int rTop = config.MarginTop;
            int rBottom = trayH - config.MarginBottom;

            if (rLeft < 0) rLeft = 0;
            if (rRight > trayW) rRight = trayW;
            if (rTop >= rBottom) { rTop = 0; rBottom = trayH; }

            int notifyLeft = -1, notifyRight = -1;
            if (config.Mode == 1 && isPrimary) {
                IntPtr notifyWnd = NativeMethods.FindWindowEx(trayHwnd, IntPtr.Zero, "TrayNotifyWnd", null);
                if (notifyWnd != IntPtr.Zero && NativeMethods.IsWindowVisible(notifyWnd)) {
                    NativeMethods.RECT nRect;
                    if (NativeMethods.GetWindowRect(notifyWnd, out nRect)) {
                        notifyLeft = nRect.Left - trayRect.Left - 8;
                        notifyRight = nRect.Right - trayRect.Left - 8;
                        if (notifyLeft < 0) notifyLeft = 0;
                        if (notifyRight > trayW) notifyRight = trayW;
                    }
                }
            }

            int curExStyle = NativeMethods.GetWindowLong(trayHwnd, NativeMethods.GWL_EXSTYLE);
            bool hasLayered = (curExStyle & NativeMethods.WS_EX_LAYERED) != 0;

            // Check if bounds and properties are unchanged
            if (state.IsCustomRgnApplied &&
                hasLayered &&
                state.LastLeft == rLeft &&
                state.LastRight == rRight &&
                state.LastTop == rTop &&
                state.LastBottom == rBottom &&
                state.LastMode == config.Mode &&
                state.LastRadius == config.CornerRadius &&
                state.LastOpacity == config.TaskbarOpacity &&
                state.LastMarginBottom == config.MarginBottom &&
                state.LastHasButton == config.ShowQuickSettingsButton &&
                state.LastTrayNotifyLeft == notifyLeft &&
                state.LastTrayNotifyRight == notifyRight) {
                if (isPrimary && state.LastTrayTop != trayRect.Top) {
                    state.LastTrayTop = trayRect.Top;
                    UpdateOverlayPos(trayRect.Left + rLeft, trayRect.Top + rTop, trayRect.Left + rRight, trayRect.Top + rBottom, config);
                }
                return;
            }

            // 1. Apply Transparent Glass alpha to Taskbar
            ApplyTaskbarVisuals(trayHwnd, state, config, isPrimary, trayRect, rLeft, rRight, rTop, rBottom, notifyLeft, notifyRight, initialAlpha);
        }

        private void ApplyTaskbarVisuals(IntPtr trayHwnd, TrayState state, Config config, bool isPrimary,
            NativeMethods.RECT trayRect, int rLeft, int rRight, int rTop, int rBottom,
            int notifyLeft, int notifyRight, int initialAlpha) {
            int targetAlpha = (initialAlpha >= 0) ? initialAlpha : config.TaskbarOpacity;
            try {
                int exStyle = NativeMethods.GetWindowLong(trayHwnd, NativeMethods.GWL_EXSTYLE);
                if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0) {
                    NativeMethods.SetWindowLong(trayHwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_LAYERED);
                }
                NativeMethods.SetLayeredWindowAttributes(trayHwnd, 0, (byte)targetAlpha, NativeMethods.LWA_ALPHA);
            } catch { }

            // 2. Cut Taskbar Region
            int rad = config.CornerRadius * 2;
            IntPtr rgnApps = NativeMethods.CreateRoundRectRgn(rLeft, rTop, rRight, rBottom, rad, rad);
            IntPtr finalRgn = rgnApps;

            if (config.Mode == 1 && notifyLeft >= 0 && notifyRight > notifyLeft) {
                IntPtr rgnTray = NativeMethods.CreateRoundRectRgn(notifyLeft, rTop, notifyRight, rBottom, rad, rad);
                IntPtr combined = NativeMethods.CreateRectRgn(0, 0, 0, 0);
                NativeMethods.CombineRgn(combined, rgnApps, rgnTray, NativeMethods.RGN_OR);
                NativeMethods.DeleteObject(rgnApps);
                NativeMethods.DeleteObject(rgnTray);
                finalRgn = combined;
            }

            if (isPrimary && config.ShowQuickSettingsButton) {
                int visualMargin = Math.Max(12, config.PaddingHorizontal);
                int btnSize = 26;
                int bX = rRight - visualMargin - btnSize;
                int bY = rTop + (rBottom - rTop - btnSize) / 2;
                // Circular cutout inset by 2px so dock background covers the square form corners while button border stays anti-aliased
                IntPtr btnHole = NativeMethods.CreateRoundRectRgn(bX + 2, bY + 2, bX + btnSize - 2, bY + btnSize - 2, btnSize - 4, btnSize - 4);
                IntPtr diffRgn = NativeMethods.CreateRectRgn(0, 0, 0, 0);
                NativeMethods.CombineRgn(diffRgn, finalRgn, btnHole, 4); // RGN_DIFF = 4
                NativeMethods.DeleteObject(btnHole);
                NativeMethods.DeleteObject(finalRgn);
                finalRgn = diffRgn;
            }

            int setRes = NativeMethods.SetWindowRgn(trayHwnd, finalRgn, true);
            Logger.Log(string.Format("SetWindowRgn [{0}, {1}, top={2}, bot={3}] res={4}", rLeft, rRight, rTop, rBottom, setRes));

            // 3. Update Smooth Glass Overlay on primary monitor
            if (isPrimary) {
                double overlayOp = (initialAlpha == 0) ? 0.0 : 1.0;
                UpdateOverlayPos(trayRect.Left + rLeft, trayRect.Top + rTop, trayRect.Left + rRight, trayRect.Top + rBottom, config, overlayOp);
            }

            state.LastLeft = rLeft;
            state.LastRight = rRight;
            state.LastTop = rTop;
            state.LastBottom = rBottom;
            state.LastTrayTop = trayRect.Top;
            state.LastMode = config.Mode;
            state.LastRadius = config.CornerRadius;
            state.LastOpacity = config.TaskbarOpacity;
            state.LastMarginBottom = config.MarginBottom;
            state.LastHasButton = config.ShowQuickSettingsButton;
            state.LastTrayNotifyLeft = notifyLeft;
            state.LastTrayNotifyRight = notifyRight;
            state.IsCustomRgnApplied = true;
            state.SaveCache();
        }

        private void UpdateOverlayPos(int left, int top, int right, int bottom, Config config, double targetOpacity = 1.0) {
            Logger.Log(string.Format("UpdateOverlayPos called: [{0}, {1}, {2}, {3}] overlayNull={4}", left, top, right, bottom, (_overlay == null)));
            if (_overlay == null) return;
            try {
                if (_overlay.InvokeRequired) {
                    _overlay.BeginInvoke(new Action(() => {
                        try {
                            if (_overlay != null && !_overlay.IsDisposed) {
                                _overlay.UpdateOverlay(left, top, right, bottom, config, targetOpacity);
                            }
                        } catch (Exception ex) {
                            Logger.Log("UpdateOverlay error: " + ex);
                        }
                    }));
                } else {
                    _overlay.UpdateOverlay(left, top, right, bottom, config, targetOpacity);
                }
            } catch (Exception ex) {
                Logger.Log("UpdateOverlayPos error: " + ex);
            }
        }

        private void HideOverlay() {
            if (_overlay == null) return;
            try {
                if (_overlay.InvokeRequired) {
                    _overlay.BeginInvoke(new Action(() => {
                        if (_overlay != null && !_overlay.IsDisposed) _overlay.Hide();
                    }));
                } else {
                    _overlay.Hide();
                }
            } catch { }
        }

        private bool HasMaximizedOrFullscreenWindowOnScreen(Screen targetScreen) {
            bool found = false;
            string targetDevice = targetScreen.DeviceName;
            var sbBounds = targetScreen.Bounds;

            NativeMethods.EnumWindows((hwnd, lParam) => {
                if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return true;

                int cloaked = 0;
                if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) {
                    return true;
                }

                StringBuilder sb = new StringBuilder(64);
                NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
                string cls = sb.ToString();
                if (cls == "Progman" || cls == "WorkerW" || cls == "EdgeUiInputTopWndClass" || IsShellOrDockWindow(hwnd, cls)) {
                    return true;
                }

                if (IsStartOrSearchProcess(GetProcessNameSafe(hwnd))) {
                    return true;
                }

                NativeMethods.RECT r;
                if (!NativeMethods.GetWindowRect(hwnd, out r)) return true;
                if (r.Width < 120 || r.Height < 120) return true;

                try {
                    if (NativeMethods.IsZoomed(hwnd)) {
                        if (Screen.FromHandle(hwnd).DeviceName == targetDevice) {
                            found = true;
                            return false;
                        }
                    } else if (r.Left <= sbBounds.Left && r.Top <= sbBounds.Top &&
                               r.Right >= sbBounds.Right && r.Bottom >= sbBounds.Bottom - 2) {
                        if (Screen.FromHandle(hwnd).DeviceName == targetDevice) {
                            found = true;
                            return false;
                        }
                    }
                } catch { }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        private void RevealSecondaryDockFast(IntPtr secHwnd, TrayState s, Config config) {
            s.IsFullscreenHidden = false;
            s.Reset();
            NativeMethods.SetLayeredWindowAttributes(secHwnd, 0, 0, NativeMethods.LWA_ALPHA);
            EnsureSecondaryZOrder(secHwnd, config, forceTopMost: true);

            if (s.HasCachedBounds) {
                NativeMethods.RECT trayRect;
                if (NativeMethods.GetWindowRect(secHwnd, out trayRect) && trayRect.Width > 0 && trayRect.Height > 0) {
                    ApplyTaskbarVisuals(
                        secHwnd, s, config, false,
                        trayRect, s.CachedLeft, s.CachedRight,
                        s.CachedTop, s.CachedBottom,
                        s.CachedNotifyLeft, s.CachedNotifyRight,
                        0);
                    AnimateDock(secHwnd, s, config, false, show: true);
                    return;
                }
            }

            UpdateSingleTaskbar(secHwnd, s, config, isPrimary: false, initialAlpha: 0);
            AnimateDock(secHwnd, s, config, false, show: true);
        }

        private void UpdateSecondaryTaskbars(Config config) {
            List<IntPtr> secondaryList = new List<IntPtr>();
            NativeMethods.EnumWindows((hwnd, lParam) => {
                StringBuilder sb = new StringBuilder(64);
                NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
                if (sb.ToString() == "Shell_SecondaryTrayWnd") {
                    secondaryList.Add(hwnd);
                }
                return true;
            }, IntPtr.Zero);

            bool anySecActive = false;
            IntPtr fg = NativeMethods.GetForegroundWindow();
            string fgProc = (fg != IntPtr.Zero) ? GetProcessNameSafe(fg) : "";
            bool isStartOrSearch = IsStartOrSearchProcess(fgProc);

            foreach (IntPtr secHwnd in secondaryList) {
                TrayState s;
                if (!_secondaryStates.TryGetValue(secHwnd, out s)) {
                    s = new TrayState();
                    _secondaryStates[secHwnd] = s;
                }

                if (config.AutoHideInFullscreen) {
                    Screen secScreen = Screen.FromHandle(secHwnd);
                    bool secFullOrMax = HasMaximizedOrFullscreenWindowOnScreen(secScreen);
                    if (secFullOrMax) {
                        anySecActive = true;
                        var sb = secScreen.Bounds;
                        NativeMethods.RECT trayRect;
                        int trayH = 48;
                        int dockTop = sb.Bottom - trayH;
                        if (NativeMethods.GetWindowRect(secHwnd, out trayRect) && trayRect.Height > 10 && trayRect.Height < 150) {
                            trayH = trayRect.Height;
                            dockTop = (trayRect.Top > sb.Top && trayRect.Top < sb.Bottom) ? trayRect.Top : (sb.Bottom - trayH);
                        }

                        long now = Environment.TickCount & 0x7FFFFFFF;
                        bool startOnThisScreen = false;
                        if (isStartOrSearch && fg != IntPtr.Zero) {
                            try {
                                startOnThisScreen = (Screen.FromHandle(fg).DeviceName == secScreen.DeviceName);
                            } catch { }
                        }

                        NativeMethods.POINT pt;
                        if (startOnThisScreen || fg == secHwnd) {
                            s.IsHoverRevealed = true;
                            s.LastInDockZoneTick = now;
                        } else if (NativeMethods.GetCursorPos(out pt)) {
                            bool inSecX = (pt.X >= sb.Left && pt.X < sb.Right);
                            bool atBottomEdge = inSecX && pt.Y >= sb.Bottom - 5 && pt.Y <= sb.Bottom;
                            bool inDockZone = inSecX && pt.Y >= dockTop - 16 && pt.Y <= sb.Bottom;

                            if (atBottomEdge) {
                                s.IsHoverRevealed = true;
                                s.LastInDockZoneTick = now;
                            } else if (s.IsHoverRevealed && inDockZone) {
                                s.LastInDockZoneTick = now;
                            } else if (s.IsHoverRevealed) {
                                long elapsed = (now - s.LastInDockZoneTick) & 0x7FFFFFFF;
                                if (elapsed >= 280) {
                                    s.IsHoverRevealed = false;
                                }
                            }
                        }

                        if (!s.IsHoverRevealed) {
                            if (!s.IsFullscreenHidden) {
                                Logger.Log("Hiding secondary dock (fullscreen/maximized window on " + secScreen.DeviceName + ").");
                                s.SaveCache();
                                AnimateDock(secHwnd, s, config, false, show: false);
                                HidePrimaryTrayVisuals(secHwnd);
                                s.IsFullscreenHidden = true;
                            }
                            continue;
                        } else if (s.IsFullscreenHidden) {
                            Logger.Log("Revealing secondary dock on " + secScreen.DeviceName + ".");
                            RevealSecondaryDockFast(secHwnd, s, config);
                        }
                    } else {
                        s.IsHoverRevealed = false;
                        if (s.IsFullscreenHidden) {
                            Logger.Log("Restoring secondary dock on " + secScreen.DeviceName + ".");
                            RevealSecondaryDockFast(secHwnd, s, config);
                        }
                    }
                } else {
                    s.IsHoverRevealed = false;
                    if (s.IsFullscreenHidden) {
                        RevealSecondaryDockFast(secHwnd, s, config);
                    }
                }

                EnsureSecondaryZOrder(secHwnd, config, forceTopMost: false);
                UpdateSingleTaskbar(secHwnd, s, config, isPrimary: false);
            }

            _anySecondaryFullscreenOrHover = anySecActive;
        }

        private void EnsureSecondaryZOrder(IntPtr secHwnd, Config config, bool forceTopMost) {
            try {
                int trayEx = NativeMethods.GetWindowLong(secHwnd, NativeMethods.GWL_EXSTYLE);
                bool isTrayTopMost = (trayEx & NativeMethods.WS_EX_TOPMOST) != 0;

                if (!config.FloatOverWindows) {
                    if (isTrayTopMost) {
                        NativeMethods.SetWindowPos(secHwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                    }
                    return;
                }

                bool needTopMost = forceTopMost || !isTrayTopMost;
                if (!needTopMost) {
                    IntPtr fg = NativeMethods.GetForegroundWindow();
                    if (fg != IntPtr.Zero && fg != secHwnd) {
                        StringBuilder sb = new StringBuilder(64);
                        NativeMethods.GetClassName(fg, sb, sb.Capacity);
                        string cls = sb.ToString();
                        if (!IsShellOrDockWindow(fg, cls) && (cls != "Windows.UI.Core.CoreWindow" || IsStartOrSearchProcess(GetProcessNameSafe(fg)))) {
                            IntPtr prev = NativeMethods.GetWindow(secHwnd, NativeMethods.GW_HWNDPREV);
                            for (int i = 0; i < 25 && prev != IntPtr.Zero; i++) {
                                if (prev == fg) {
                                    needTopMost = true;
                                    break;
                                }
                                prev = NativeMethods.GetWindow(prev, NativeMethods.GW_HWNDPREV);
                            }
                        }
                    }
                }

                if (needTopMost) {
                    NativeMethods.SetWindowPos(secHwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                }
            } catch { }
        }

        public void RestoreAll() {
            IntPtr primaryTray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (primaryTray != IntPtr.Zero) {
                NativeMethods.SetWindowRgn(primaryTray, IntPtr.Zero, true);
                NativeMethods.SetLayeredWindowAttributes(primaryTray, 0, 255, NativeMethods.LWA_ALPHA);
                NativeMethods.ShowWindow(primaryTray, NativeMethods.SW_SHOW);
            }
            SetSystemTaskbarAutoHide(false);
            _mainTrayState.Reset();
            _isFullscreenHidden = false;
            _isHoverRevealed = false;
            _lastWindowFullscreen = false;

            HideOverlay();

            // Restore original work area
            if (_workAreaModified) {
                RestoreAllWorkAreas();
            }

            NativeMethods.EnumWindows((hwnd, lParam) => {
                StringBuilder sb = new StringBuilder(64);
                NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
                if (sb.ToString() == "Shell_SecondaryTrayWnd") {
                    NativeMethods.SetWindowRgn(hwnd, IntPtr.Zero, true);
                    NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 255, NativeMethods.LWA_ALPHA);
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
                }
                return true;
            }, IntPtr.Zero);

            foreach (var kvp in _secondaryStates) {
                kvp.Value.IsFullscreenHidden = false;
                kvp.Value.IsHoverRevealed = false;
                kvp.Value.Reset();
            }
            _anySecondaryFullscreenOrHover = false;
        }
    }
}
