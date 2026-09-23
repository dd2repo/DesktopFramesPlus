using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Desktop_Frames;

public class NonActivatingWindow : Window
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MAXIMIZE  = 0xF030;
    private const int SC_RESTORE   = 0xF120;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE    = 3;
    private const int GWL_EXSTYLE      = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    // WM_NCHITTEST — edge/corner resize for transparent borderless windows
    private const int WM_NCHITTEST  = 0x0084;
    private const int HTCLIENT      = 1;
    private const int HTLEFT        = 10;
    private const int HTRIGHT       = 11;
    private const int HTTOP         = 12;
    private const int HTTOPLEFT     = 13;
    private const int HTTOPRIGHT    = 14;
    private const int HTBOTTOM      = 15;
    private const int HTBOTTOMLEFT  = 16;
    private const int HTBOTTOMRIGHT = 17;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    private bool _focusPreventionEnabled = true;

    // --- Idle Fade-Out Fields ---
    private System.Windows.Threading.DispatcherTimer _idleTimer;
    private bool _isIdleFaded = false;
    // ----------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwndSource = (HwndSource)PresentationSource.FromVisual(this);
        hwndSource.AddHook(WndProc);
        SetWindowLong(new WindowInteropHelper(this).Handle, GWL_EXSTYLE, GetWindowLong(new WindowInteropHelper(this).Handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
        SetupIdleTimer();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE  = 0x0232;

        if (msg == WM_ENTERSIZEMOVE) Framemanager.OnResizingStarted(this);
        else if (msg == WM_EXITSIZEMOVE) Framemanager.OnResizingEnded(this);

        // Edge/corner resize hit-testing for transparent borderless windows
        if (msg == WM_NCHITTEST && ResizeMode == ResizeMode.CanResizeWithGrip)
        {
            int lp = lParam.ToInt32();
            int cx = lp & 0xFFFF; if (cx >= 0x8000) cx -= 0x10000;
            int cy = (lp >> 16) & 0xFFFF; if (cy >= 0x8000) cy -= 0x10000;

            GetWindowRect(hwnd, out RECT r);
            const int G = 8; // grip width in pixels

            bool left   = cx < r.Left + G;
            bool right  = cx >= r.Right - G;
            bool top    = cy < r.Top + G;
            bool bottom = cy >= r.Bottom - G;

            if (bottom && right)  { handled = true; return new IntPtr(HTBOTTOMRIGHT); }
            if (bottom && left)   { handled = true; return new IntPtr(HTBOTTOMLEFT); }
            if (top    && right)  { handled = true; return new IntPtr(HTTOPRIGHT); }
            if (top    && left)   { handled = true; return new IntPtr(HTTOPLEFT); }
            if (right)            { handled = true; return new IntPtr(HTRIGHT); }
            if (left)             { handled = true; return new IntPtr(HTLEFT); }
            if (bottom)           { handled = true; return new IntPtr(HTBOTTOM); }
            if (top)              { handled = true; return new IntPtr(HTTOP); }
        }

        if (_focusPreventionEnabled && msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }

        if (msg == WM_SYSCOMMAND)
        {
            int command = wParam.ToInt32() & 0xFFF0;
            if (command == SC_MAXIMIZE || command == SC_RESTORE)
            {
                handled = true;
                return IntPtr.Zero;
            }
        }

        return IntPtr.Zero;
    }

    public void EnableFocusPrevention(bool enable)
    {
        _focusPreventionEnabled = enable;
        if (enable)
        {
            SetWindowLong(new WindowInteropHelper(this).Handle, GWL_EXSTYLE, GetWindowLong(new WindowInteropHelper(this).Handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
        }
        else
        {
            SetWindowLong(new WindowInteropHelper(this).Handle, GWL_EXSTYLE, GetWindowLong(new WindowInteropHelper(this).Handle, GWL_EXSTYLE) & ~WS_EX_NOACTIVATE);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_SHOWNOACTIVATE = 4;

    public void ShowWithoutActivation()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public void BeginKeyboardInteractiveEdit(UIElement targetElement)
    {
        EnableFocusPrevention(false);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        IntPtr foregroundHwnd = GetForegroundWindow();

        if (foregroundHwnd != hwnd && foregroundHwnd != IntPtr.Zero)
        {
            uint foregroundThread = GetWindowThreadProcessId(foregroundHwnd, out _);
            uint currentThread = GetCurrentThreadId();

            if (foregroundThread != currentThread)
            {
                AttachThreadInput(currentThread, foregroundThread, true);
                SetForegroundWindow(hwnd);
                AttachThreadInput(currentThread, foregroundThread, false);
            }
            else
            {
                SetForegroundWindow(hwnd);
            }
        }
        else
        {
            SetForegroundWindow(hwnd);
        }

        // Deferred focus to ensure the OS has actually switched foreground windows
        this.Dispatcher.BeginInvoke(new Action(() =>
        {
            targetElement.Focus();
            if (targetElement is System.Windows.Controls.TextBox tb) tb.SelectAll();
            else if (targetElement is System.Windows.Controls.ComboBox cb)
            {
                var innerTextBox = (System.Windows.Controls.TextBox)cb.Template.FindName("PART_EditableTextBox", cb);
                innerTextBox?.Focus();
                innerTextBox?.SelectAll();
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void EndKeyboardInteractiveEdit()
    {
        EnableFocusPrevention(true);
        System.Windows.Input.Keyboard.ClearFocus();
    }

    // =========================================================
    // IDLE FADE-OUT ENGINE
    // =========================================================

    public void SetupIdleTimer()
    {
        if (_idleTimer == null)
        {
            _idleTimer = new System.Windows.Threading.DispatcherTimer();
            _idleTimer.Tick += (s, ev) => ExecuteIdleFadeOut();

            this.MouseEnter += (s, ev) => ResetIdleTimer(true);
            this.MouseLeave += (s, ev) => ResetIdleTimer(false);
            this.MouseMove += (s, ev) => ResetIdleTimer(true);
        }

        RefreshIdleSettings();
    }

    public void RefreshIdleSettings()
    {
        if (_idleTimer == null) return;

        if (SettingsManager.FramesFadeOutFx)
        {
            _idleTimer.Interval = TimeSpan.FromSeconds(SettingsManager.FadeOutTime);
            _idleTimer.Start();
        }
        else
        {
            _idleTimer.Stop();
            RestoreOpacity();
        }
    }

    private void ResetIdleTimer(bool isMouseInside)
    {
        if (!SettingsManager.FramesFadeOutFx) return;

        _idleTimer.Stop();

        if (isMouseInside)
        {
            RestoreOpacity();
        }
        else
        {
            _idleTimer.Start();
        }
    }

    private void ExecuteIdleFadeOut()
    {
        _idleTimer.Stop();

        // --- BUG FIX: Prevent fading if the mouse is currently resting on the frame ---
        if (this.IsMouseOver)
        {
            _idleTimer.Start(); // Restart the countdown and check again later
            return;
        }

        if (!SettingsManager.FramesFadeOutFx || _isIdleFaded || this.Visibility != Visibility.Visible || this.Opacity == 0.0) return;

        _isIdleFaded = true;

        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = SettingsManager.FadeOutFxTargetAlpha,
            Duration = TimeSpan.FromMilliseconds(400)
        };

        this.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    public void TriggerWakeUpIdleReset()
    {
        _isIdleFaded = false; // Reset the state since the global manager is forcing opacity to 1.0

        if (SettingsManager.FramesFadeOutFx && _idleTimer != null)
        {
            _idleTimer.Stop();
            _idleTimer.Start(); // Restart the countdown automatically
        }
    }

    private void RestoreOpacity()
    {
        if (!_isIdleFaded)
        {
            if (SettingsManager.FramesFadeOutFx) _idleTimer.Start();
            return;
        }

        _isIdleFaded = false;

        var fadeIn = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(200)
        };

        this.BeginAnimation(UIElement.OpacityProperty, fadeIn);

        if (SettingsManager.FramesFadeOutFx) _idleTimer.Start();
    }

}