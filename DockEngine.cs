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
        private long _lastInDockZoneTick = 0;
        private long _lastWorkAreaEnforceTick = 0;
        private NativeMethods.RECT _originalWorkArea;
        private bool _workAreaModified = false;

        public DockEngine(Action onOpenSettings = null) {
            _onOpenSettings = onOpenSettings;
            SaveOriginalWorkArea();
        }

        private void SaveOriginalWorkArea() {
            try {
                var pb = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                int fallbackTrayH = 48;
                IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
                if (tray != IntPtr.Zero) {
                    NativeMethods.RECT tr;
                    if (NativeMethods.GetWindowRect(tray, out tr) && tr.Height > 10 && tr.Height < 150) {
                        fallbackTrayH = tr.Height;
                    }
                }

                NativeMethods.RECT curWork = new NativeMethods.RECT();
                if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref curWork, 0)) {
                    bool isFullBounds = (curWork.Left <= pb.Left && curWork.Top <= pb.Top && curWork.Right >= pb.Right && curWork.Bottom >= pb.Bottom);
                    if (!isFullBounds) {
                        _originalWorkArea = curWork;
                        return;
                    }
                }
                _originalWorkArea = new NativeMethods.RECT(pb.Left, pb.Top, pb.Right, pb.Bottom - fallbackTrayH);
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
                if (_config != null && _config.Enabled && _config.AutoHideInFullscreen &&
                    (_lastWindowFullscreen || _isFullscreenHidden || _isHoverRevealed)) {
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
            bool isFullOrMax = config.AutoHideInFullscreen && IsForegroundFullscreenOrMaximized(out isShellDockActive);
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
                if (NativeMethods.GetCursorPos(out pt)) {
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
                        AnimateDock(primaryTray, config, show: false);
                        HidePrimaryTrayVisuals(primaryTray);
                        HideOverlay();
                        _isFullscreenHidden = true;
                    }
                    return;
                } else if (_isFullscreenHidden) {
                    Logger.Log("Revealing dock on bottom-edge hover.");
                    _isFullscreenHidden = false;
                    _mainTrayState.Reset();
                    NativeMethods.SetLayeredWindowAttributes(primaryTray, 0, 0, NativeMethods.LWA_ALPHA);
                    EnsureTaskbarZOrder(primaryTray, config, forceTopMost: true);
                    UpdateSingleTaskbar(primaryTray, _mainTrayState, config, isPrimary: true, initialAlpha: 0);
                    AnimateDock(primaryTray, config, show: true);
                }
            } else {
                _isHoverRevealed = false;
                if (_isFullscreenHidden) {
                    Logger.Log("Restoring dock (no foreground fullscreen/maximized window).");
                    _isFullscreenHidden = false;
                    _mainTrayState.Reset();
                    NativeMethods.SetLayeredWindowAttributes(primaryTray, 0, 0, NativeMethods.LWA_ALPHA);
                    EnsureTaskbarZOrder(primaryTray, config, forceTopMost: true);
                    UpdateSingleTaskbar(primaryTray, _mainTrayState, config, isPrimary: true, initialAlpha: 0);
                    AnimateDock(primaryTray, config, show: true);
                }
            }

            // 3. Z-order management: keep dock above normal and fullscreen windows when visible
            EnsureTaskbarZOrder(primaryTray, config, forceTopMost: false);

            // 4. Update Primary Taskbar & Glass Overlay
            UpdateSingleTaskbar(primaryTray, _mainTrayState, config, isPrimary: true);

            // 5. Update Secondary Taskbars
            UpdateSecondaryTaskbars(config);
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

        private bool IsForegroundFullscreenOrMaximized(out bool isShellDockActive) {
            isShellDockActive = false;
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
                    if (!IsShellOrDockWindow(fg, cls) && cls != "Windows.UI.Core.CoreWindow") {
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

                if (_overlay != null && !_overlay.IsDisposed && (show || _overlay.Visible)) {
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
                EnsureTaskbarZOrder(primaryTray, config, forceTopMost: false);
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
                var pb = Screen.PrimaryScreen.Bounds;
                NativeMethods.RECT curWork = new NativeMethods.RECT();
                bool gotWork = NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref curWork, 0);

                if (floatOver) {
                    bool isFullWork = gotWork &&
                        curWork.Left <= pb.Left && curWork.Top <= pb.Top &&
                        curWork.Right >= pb.Right && curWork.Bottom >= pb.Bottom;

                    if (!_workAreaModified) {
                        SaveOriginalWorkArea();
                        if (!isFullWork) {
                            NativeMethods.RECT area = new NativeMethods.RECT(pb.Left, pb.Top, pb.Right, pb.Bottom);
                            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETWORKAREA, 0, ref area, NativeMethods.SPIF_SENDCHANGE);
                        }
                        _workAreaModified = true;
                        _lastWorkAreaEnforceTick = Environment.TickCount & 0x7FFFFFFF;
                    } else if (!isFullWork) {
                        long now = Environment.TickCount & 0x7FFFFFFF;
                        long elapsed = (now - _lastWorkAreaEnforceTick) & 0x7FFFFFFF;
                        if (elapsed >= 400) {
                            NativeMethods.RECT area = new NativeMethods.RECT(pb.Left, pb.Top, pb.Right, pb.Bottom);
                            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETWORKAREA, 0, ref area, 0);
                            _lastWorkAreaEnforceTick = now;
                        }
                    }
                } else {
                    if (_workAreaModified) {
                        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETWORKAREA, 0, ref _originalWorkArea, NativeMethods.SPIF_SENDCHANGE);
                        _workAreaModified = false;
                    }
                }
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
            SetSystemTaskbarAutoHide(false);
            _mainTrayState.Reset();
            _isFullscreenHidden = false;
            _isHoverRevealed = false;
            _lastWindowFullscreen = false;

            HideOverlay();

            // Restore original work area
            if (_workAreaModified) {
                try {
                    NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETWORKAREA, 0, ref _originalWorkArea, NativeMethods.SPIF_SENDCHANGE);
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
