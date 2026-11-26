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
            public int LastTrayNotifyLeft = -9999;
            public int LastTrayNotifyRight = -9999;
            public int LastMode = -1;
            public int LastRadius = -1;
            public int LastOpacity = -1;
            public int LastMarginBottom = -1;
            public bool LastHasButton = false;
            public bool IsCustomRgnApplied = false;

            public void Reset() {
                LastLeft = -9999;
                LastRight = -9999;
                LastTop = -9999;
                LastBottom = -9999;
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
        private NativeMethods.RECT _originalWorkArea;
        private bool _workAreaModified = false;

        public DockEngine(Action onOpenSettings = null) {
            _onOpenSettings = onOpenSettings;
            SaveOriginalWorkArea();
        }

        private void SaveOriginalWorkArea() {
            try {
                NativeMethods.RECT curWork = new NativeMethods.RECT();
                if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref curWork, 0)) {
                    _originalWorkArea = curWork;
                } else {
                    int sw = Screen.PrimaryScreen.Bounds.Width;
                    int sh = Screen.PrimaryScreen.Bounds.Height;
                    _originalWorkArea = new NativeMethods.RECT(0, 0, sw, sh - 48);
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

            // 1. Float Over Windows (Work Area Management) - must run even when dock is auto-hidden!
            UpdateWorkArea(config.FloatOverWindows, config);

            // 2. Auto-Hide when foreground window is maximized/fullscreen, reveal on bottom screen edge hover
            if (config.AutoHideInFullscreen && IsForegroundFullscreenOrMaximized()) {
                NativeMethods.POINT pt;
                int sh = Screen.PrimaryScreen.Bounds.Height;
                if (NativeMethods.GetCursorPos(out pt)) {
                    if (pt.Y >= sh - 3) {
                        _isHoverRevealed = true;
                    } else if (pt.Y < sh - 54) {
                        _isHoverRevealed = false;
                    }
                }

                if (!_isHoverRevealed) {
                    if (!_isFullscreenHidden) {
                        AnimateDock(primaryTray, config, show: false);
                        NativeMethods.ShowWindow(primaryTray, NativeMethods.SW_HIDE);
                        HideOverlay();
                        _isFullscreenHidden = true;
                    }
                    return;
                } else if (_isFullscreenHidden) {
                    _isFullscreenHidden = false;
                    _mainTrayState.Reset();
                    NativeMethods.SetLayeredWindowAttributes(primaryTray, 0, 0, NativeMethods.LWA_ALPHA);
                    NativeMethods.ShowWindow(primaryTray, NativeMethods.SW_SHOW);
                    NativeMethods.SetWindowPos(primaryTray, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                    UpdateSingleTaskbar(primaryTray, _mainTrayState, config, isPrimary: true);
                    AnimateDock(primaryTray, config, show: true);
                }
            } else {
                _isHoverRevealed = false;
                if (_isFullscreenHidden) {
                    _isFullscreenHidden = false;
                    _mainTrayState.Reset();
                    NativeMethods.SetLayeredWindowAttributes(primaryTray, 0, 0, NativeMethods.LWA_ALPHA);
                    NativeMethods.ShowWindow(primaryTray, NativeMethods.SW_SHOW);
                    NativeMethods.SetWindowPos(primaryTray, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                    UpdateSingleTaskbar(primaryTray, _mainTrayState, config, isPrimary: true);
                    AnimateDock(primaryTray, config, show: true);
                }
            }

            // 3. Z-order management: only call SetWindowPos when WS_EX_TOPMOST state actually differs (avoids cursor flicker)
            int trayEx = NativeMethods.GetWindowLong(primaryTray, NativeMethods.GWL_EXSTYLE);
            bool isTrayTopMost = (trayEx & NativeMethods.WS_EX_TOPMOST) != 0;
            if (config.FloatOverWindows && !isTrayTopMost) {
                NativeMethods.SetWindowPos(primaryTray, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            } else if (!config.FloatOverWindows && isTrayTopMost) {
                NativeMethods.SetWindowPos(primaryTray, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }

            // 4. Update Primary Taskbar & Glass Overlay
            UpdateSingleTaskbar(primaryTray, _mainTrayState, config, isPrimary: true);

            // 5. Update Secondary Taskbars
            UpdateSecondaryTaskbars(config);
        }

        private bool IsForegroundFullscreenOrMaximized() {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            if (fg == NativeMethods.GetDesktopWindow() || fg == NativeMethods.GetShellWindow()) return false;
            if (_overlay != null && fg == _overlay.Handle) return false;

            StringBuilder sb = new StringBuilder(64);
            NativeMethods.GetClassName(fg, sb, sb.Capacity);
            string cls = sb.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "NotifyIconOverflowWindow" || cls == "Windows.UI.Core.CoreWindow") return false;

            if (NativeMethods.IsZoomed(fg)) return true;

            NativeMethods.RECT r;
            if (!NativeMethods.GetWindowRect(fg, out r)) return false;

            int sw = Screen.PrimaryScreen.Bounds.Width;
            int sh = Screen.PrimaryScreen.Bounds.Height;

            // Covers full screen (borderless maximized or fullscreen)
            return (r.Left <= 0 && r.Top <= 0 && r.Right >= sw && r.Bottom >= sh - 2);
        }

        private void AnimateDock(IntPtr primaryTray, Config config, bool show) {
            if (!_mainTrayState.IsCustomRgnApplied) return;
            int rLeft = _mainTrayState.LastLeft;
            int rRight = _mainTrayState.LastRight;
            int rTop = _mainTrayState.LastTop;
            int rBottom = _mainTrayState.LastBottom;
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
                if (config.ShowQuickSettingsButton) {
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

                NativeMethods.SetLayeredWindowAttributes(primaryTray, 0, curAlpha, NativeMethods.LWA_ALPHA);
                NativeMethods.SetWindowRgn(primaryTray, rgn, true);

                if (_overlay != null && !_overlay.IsDisposed && _overlay.Visible) {
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
                NativeMethods.SetLayeredWindowAttributes(primaryTray, 0, (byte)config.TaskbarOpacity, NativeMethods.LWA_ALPHA);
                _mainTrayState.Reset();
                UpdateSingleTaskbar(primaryTray, _mainTrayState, config, isPrimary: true);
                if (_overlay != null && !_overlay.IsDisposed) {
                    try {
                        _overlay.BeginInvoke(new Action(() => {
                            if (_overlay != null && !_overlay.IsDisposed) {
                                _overlay.SetButtonOpacity(1.0);
                                _overlay.SetVerticalOffset(0);
                            }
                        }));
                    } catch { }
                }
            }
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

        private void UpdateWorkArea(bool floatOver, Config config) {
            try {
                int sw = Screen.PrimaryScreen.Bounds.Width;
                int sh = Screen.PrimaryScreen.Bounds.Height;
                if (floatOver) {
                    if (!_workAreaModified) {
                        SetSystemTaskbarAutoHide(true);
                        NativeMethods.RECT area = new NativeMethods.RECT(0, 0, sw, sh);
                        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETWORKAREA, 0, ref area, 2);
                        _workAreaModified = true;
                    }
                } else {
                    if (_workAreaModified) {
                        SetSystemTaskbarAutoHide(false);
                        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETWORKAREA, 0, ref _originalWorkArea, 2);
                        _workAreaModified = false;
                    }
                }
            } catch { }
        }

        private void UpdateSingleTaskbar(IntPtr trayHwnd, TrayState state, Config config, bool isPrimary) {
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

            // Check if bounds and properties are unchanged
            if (state.IsCustomRgnApplied &&
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
                return;
            }

            // 1. Apply Transparent Glass alpha to Taskbar
            try {
                int exStyle = NativeMethods.GetWindowLong(trayHwnd, NativeMethods.GWL_EXSTYLE);
                if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0) {
                    NativeMethods.SetWindowLong(trayHwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_LAYERED);
                }
                NativeMethods.SetLayeredWindowAttributes(trayHwnd, 0, (byte)config.TaskbarOpacity, NativeMethods.LWA_ALPHA);
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
                UpdateOverlayPos(trayRect.Left + rLeft, trayRect.Top + rTop, trayRect.Left + rRight, trayRect.Top + rBottom, config);
            }

            state.LastLeft = rLeft;
            state.LastRight = rRight;
            state.LastTop = rTop;
            state.LastBottom = rBottom;
            state.LastMode = config.Mode;
            state.LastRadius = config.CornerRadius;
            state.LastOpacity = config.TaskbarOpacity;
            state.LastMarginBottom = config.MarginBottom;
            state.LastHasButton = config.ShowQuickSettingsButton;
            state.LastTrayNotifyLeft = notifyLeft;
            state.LastTrayNotifyRight = notifyRight;
            state.IsCustomRgnApplied = true;
        }

        private void UpdateOverlayPos(int left, int top, int right, int bottom, Config config) {
            Logger.Log(string.Format("UpdateOverlayPos called: [{0}, {1}, {2}, {3}] overlayNull={4}", left, top, right, bottom, (_overlay == null)));
            if (_overlay == null) return;
            try {
                if (_overlay.InvokeRequired) {
                    _overlay.BeginInvoke(new Action(() => {
                        try {
                            if (_overlay != null && !_overlay.IsDisposed) {
                                _overlay.UpdateOverlay(left, top, right, bottom, config);
                            }
                        } catch (Exception ex) {
                            Logger.Log("UpdateOverlay error: " + ex);
                        }
                    }));
                } else {
                    _overlay.UpdateOverlay(left, top, right, bottom, config);
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

            foreach (IntPtr secHwnd in secondaryList) {
                TrayState s;
                if (!_secondaryStates.TryGetValue(secHwnd, out s)) {
                    s = new TrayState();
                    _secondaryStates[secHwnd] = s;
                }
                UpdateSingleTaskbar(secHwnd, s, config, isPrimary: false);
            }
        }

        public void RestoreAll() {
            IntPtr primaryTray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (primaryTray != IntPtr.Zero) {
                NativeMethods.SetWindowRgn(primaryTray, IntPtr.Zero, true);
                NativeMethods.SetLayeredWindowAttributes(primaryTray, 0, 255, NativeMethods.LWA_ALPHA);
                NativeMethods.ShowWindow(primaryTray, NativeMethods.SW_SHOW);
            }
            _mainTrayState.Reset();

            HideOverlay();

            // Restore original work area
            if (_workAreaModified) {
                try {
                    NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETWORKAREA, 0, ref _originalWorkArea, 0);
                    _workAreaModified = false;
                } catch { }
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
                kvp.Value.Reset();
            }
        }
    }
}
