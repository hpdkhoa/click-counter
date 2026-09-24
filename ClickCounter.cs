// ClickCounter: global click counter with a click-through, always-on-top overlay.
// Builds with the C# compiler that ships with Windows (.NET Framework 4.x), no XAML.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace ClickCounter
{
    // ------------------------------------------------------------------ Trigger
    public sealed class Trigger
    {
        public readonly bool IsMouse;
        public readonly int Code; // mouse: 1=left 2=right 3=middle 4=X1 5=X2, key: virtual key code

        public Trigger(bool isMouse, int code) { IsMouse = isMouse; Code = code; }

        public static readonly Trigger LeftClick = new Trigger(true, 1);
        public static readonly Trigger Mouse4 = new Trigger(true, 4);

        public bool Same(Trigger other)
        {
            return other != null && other.IsMouse == IsMouse && other.Code == Code;
        }

        public string Display()
        {
            if (IsMouse)
            {
                switch (Code)
                {
                    case 1: return "Left click (M1)";
                    case 2: return "Right click (M2)";
                    case 3: return "Middle click (M3)";
                    case 4: return "Mouse 4 (M4 / X1, back)";
                    case 5: return "Mouse 5 (M5 / X2, forward)";
                }
                return "Mouse " + Code;
            }
            Key k = KeyInterop.KeyFromVirtualKey(Code);
            return "Key " + (k == Key.None ? "0x" + Code.ToString("X") : k.ToString());
        }

        public string Serialize()
        {
            return (IsMouse ? "M" : "K") + Code.ToString(CultureInfo.InvariantCulture);
        }

        public static Trigger Parse(string s, Trigger fallback)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 2) return fallback;
            int code;
            if (!int.TryParse(s.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out code)) return fallback;
            if (s[0] == 'M') return new Trigger(true, code);
            if (s[0] == 'K') return new Trigger(false, code);
            return fallback;
        }
    }

    // ----------------------------------------------------------------- Settings
    public sealed class Settings
    {
        public Trigger CountTrigger = Trigger.LeftClick;
        public Trigger StartTrigger = Trigger.Mouse4;
        public int Max = 5;
        public int Alert = 4;
        public double FontSize = 96;
        public string NormalColor = "#FFFFFF";
        public string AlertColor = "#FF3B30";
        public bool CountBeforeStart = false;
        public bool ShowOverlay = true;

        static string PathOf()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClickCounter");
            return Path.Combine(dir, "settings.txt");
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                string p = PathOf();
                if (!File.Exists(p)) return s;
                foreach (string raw in File.ReadAllLines(p))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    int i; double d; bool b;
                    switch (key)
                    {
                        case "count": s.CountTrigger = Trigger.Parse(val, s.CountTrigger); break;
                        case "start": s.StartTrigger = Trigger.Parse(val, s.StartTrigger); break;
                        case "max": if (int.TryParse(val, out i) && i > 0) s.Max = i; break;
                        case "alert": if (int.TryParse(val, out i) && i >= 0) s.Alert = i; break;
                        case "fontsize": if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 8) s.FontSize = d; break;
                        case "normalcolor": s.NormalColor = val; break;
                        case "alertcolor": s.AlertColor = val; break;
                        case "countbeforestart": if (bool.TryParse(val, out b)) s.CountBeforeStart = b; break;
                        case "showoverlay": if (bool.TryParse(val, out b)) s.ShowOverlay = b; break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                string p = PathOf();
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                string[] lines = new string[]
                {
                    "count=" + CountTrigger.Serialize(),
                    "start=" + StartTrigger.Serialize(),
                    "max=" + Max.ToString(CultureInfo.InvariantCulture),
                    "alert=" + Alert.ToString(CultureInfo.InvariantCulture),
                    "fontsize=" + FontSize.ToString(CultureInfo.InvariantCulture),
                    "normalcolor=" + NormalColor,
                    "alertcolor=" + AlertColor,
                    "countbeforestart=" + CountBeforeStart.ToString(),
                    "showoverlay=" + ShowOverlay.ToString(),
                };
                File.WriteAllLines(p, lines);
            }
            catch { }
        }
    }

    // ---------------------------------------------------------- Global hooks
    public static class InputHook
    {
        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        const int WH_KEYBOARD_LL = 13;
        const int WH_MOUSE_LL = 14;
        const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207, WM_XBUTTONDOWN = 0x020B;

        static HookProc mouseProc;   // kept alive so the GC never collects the delegates
        static HookProc keyProc;
        static IntPtr mouseHook = IntPtr.Zero;
        static IntPtr keyHook = IntPtr.Zero;
        static readonly HashSet<int> keysDown = new HashSet<int>();

        // trigger, screen x, screen y (x,y are -1 for keyboard input)
        public static event Action<Trigger, int, int> InputDown;

        public static void Install()
        {
            if (mouseHook != IntPtr.Zero) return;
            mouseProc = MouseCallback;
            keyProc = KeyCallback;
            IntPtr mod = GetModuleHandle(null);
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, mod, 0);
            keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyProc, mod, 0);
        }

        public static void Uninstall()
        {
            if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
            if (keyHook != IntPtr.Zero) { UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
        }

        static void Raise(Trigger t, int x, int y)
        {
            Action<Trigger, int, int> h = InputDown;
            if (h == null) return;
            try { h(t, x, y); } catch { }
        }

        static IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                int code = 0;
                switch (msg)
                {
                    case WM_LBUTTONDOWN: code = 1; break;
                    case WM_RBUTTONDOWN: code = 2; break;
                    case WM_MBUTTONDOWN: code = 3; break;
                    case WM_XBUTTONDOWN:
                        {
                            int mouseData = Marshal.ReadInt32(lParam, 8);
                            int xb = (mouseData >> 16) & 0xFFFF;
                            code = xb == 1 ? 4 : (xb == 2 ? 5 : 0);
                            break;
                        }
                }
                if (code != 0)
                {
                    int x = Marshal.ReadInt32(lParam, 0);
                    int y = Marshal.ReadInt32(lParam, 4);
                    Raise(new Trigger(true, code), x, y);
                }
            }
            return CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        static IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                int vk = Marshal.ReadInt32(lParam, 0);
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    // Ignore keyboard auto-repeat while a key is held down.
                    if (keysDown.Add(vk)) Raise(new Trigger(false, vk), -1, -1);
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    keysDown.Remove(vk);
                }
            }
            return CallNextHookEx(keyHook, nCode, wParam, lParam);
        }
    }

    // ------------------------------------------------------------ Overlay
    public sealed class OverlayWindow : Window
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        const int GWL_EXSTYLE = -20;
        const long WS_EX_TRANSPARENT = 0x00000020L;
        const long WS_EX_TOOLWINDOW = 0x00000080L;
        const long WS_EX_NOACTIVATE = 0x08000000L;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

        readonly TextBlock text = new TextBlock();
        readonly DispatcherTimer topmostTimer = new DispatcherTimer();
        IntPtr hwnd = IntPtr.Zero;

        public OverlayWindow()
        {
            Title = "ClickCounter overlay";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            IsHitTestVisible = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;

            text.FontFamily = new FontFamily("Segoe UI");
            text.FontWeight = FontWeights.Bold;
            text.FontSize = 96;
            text.Foreground = Brushes.White;
            text.Margin = new Thickness(24, 8, 24, 8);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;
            DropShadowEffect shadow = new DropShadowEffect();
            shadow.Color = Colors.Black;
            shadow.BlurRadius = 14;
            shadow.ShadowDepth = 0;
            shadow.Opacity = 0.95;
            text.Effect = shadow;
            Content = text;

            SourceInitialized += OnSourceInitialized;
            SizeChanged += delegate { Recenter(); };

            topmostTimer.Interval = TimeSpan.FromSeconds(1);
            topmostTimer.Tick += delegate { AssertTopmost(); };
            topmostTimer.Start();
        }

        void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
            AssertTopmost();
        }

        void AssertTopmost()
        {
            if (hwnd == IntPtr.Zero || !IsVisible) return;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        void Recenter()
        {
            Left = Math.Round((SystemParameters.PrimaryScreenWidth - ActualWidth) / 2);
            Top = Math.Round((SystemParameters.PrimaryScreenHeight - ActualHeight) / 2);
        }

        public void SetDisplay(string value, Brush brush, double fontSize)
        {
            text.Text = value;
            text.Foreground = brush;
            if (Math.Abs(text.FontSize - fontSize) > 0.01) text.FontSize = fontSize;
        }
    }

    // ------------------------------------------------------- Settings window
    public sealed class SettingsWindow : Window
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }
        const uint GA_ROOT = 2;
        const int VK_ESCAPE = 0x1B;

        readonly Settings settings;
        readonly OverlayWindow overlay = new OverlayWindow();
        IntPtr hwnd = IntPtr.Zero;

        int count = 0;
        bool started = false;
        int capturing = 0; // 0 none, 1 count trigger, 2 start trigger
        int lastCaptureTick = -100000;

        Brush normalBrush = Brushes.White;
        Brush alertBrush = Brushes.Red;

        readonly TextBlock countTriggerLabel = new TextBlock();
        readonly TextBlock startTriggerLabel = new TextBlock();
        readonly Button countSetButton = new Button();
        readonly Button startSetButton = new Button();
        readonly TextBox maxBox = new TextBox();
        readonly TextBox alertBox = new TextBox();
        readonly TextBox fontBox = new TextBox();
        readonly TextBox normalColorBox = new TextBox();
        readonly TextBox alertColorBox = new TextBox();
        readonly CheckBox countBeforeStartBox = new CheckBox();
        readonly CheckBox showOverlayBox = new CheckBox();
        readonly TextBlock status = new TextBlock();
        readonly TextBlock capturePrompt = new TextBlock();
        bool loading = true;

        public SettingsWindow(Settings s)
        {
            settings = s;
            Title = "Click Counter";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontSize = 13;

            Grid grid = new Grid();
            grid.Margin = new Thickness(14);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Row: count trigger
            countSetButton.Content = "Set...";
            countSetButton.Padding = new Thickness(10, 2, 10, 2);
            countSetButton.Click += delegate { BeginCapture(1); };
            AddRow(grid, "Count trigger:", countTriggerLabel, countSetButton);

            // Row: start trigger
            startSetButton.Content = "Set...";
            startSetButton.Padding = new Thickness(10, 2, 10, 2);
            startSetButton.Click += delegate { BeginCapture(2); };
            AddRow(grid, "Start / reset trigger:", startTriggerLabel, startSetButton);

            AddRow(grid, "Max count (resets at):", maxBox, null);
            AddRow(grid, "Alert from count:", alertBox, null);
            AddRow(grid, "Overlay font size:", fontBox, null);
            AddRow(grid, "Normal color:", normalColorBox, null);
            AddRow(grid, "Alert color:", alertColorBox, null);

            countBeforeStartBox.Content = "Count clicks before the start trigger is pressed";
            AddRow(grid, "", countBeforeStartBox, null);
            showOverlayBox.Content = "Show overlay";
            AddRow(grid, "", showOverlayBox, null);

            Button resetButton = new Button();
            resetButton.Content = "Reset to 0 now";
            resetButton.Padding = new Thickness(10, 3, 10, 3);
            resetButton.HorizontalAlignment = HorizontalAlignment.Left;
            resetButton.Click += delegate { started = true; count = 0; UpdateOverlay(); };
            AddRow(grid, "", resetButton, null);

            capturePrompt.Foreground = Brushes.DarkOrange;
            capturePrompt.FontWeight = FontWeights.Bold;
            capturePrompt.TextWrapping = TextWrapping.Wrap;
            AddRow(grid, "", capturePrompt, null);

            status.TextWrapping = TextWrapping.Wrap;
            status.Foreground = Brushes.Gray;
            AddRow(grid, "", status, null);

            Content = grid;

            maxBox.Text = settings.Max.ToString(CultureInfo.InvariantCulture);
            alertBox.Text = settings.Alert.ToString(CultureInfo.InvariantCulture);
            fontBox.Text = settings.FontSize.ToString(CultureInfo.InvariantCulture);
            normalColorBox.Text = settings.NormalColor;
            alertColorBox.Text = settings.AlertColor;
            countBeforeStartBox.IsChecked = settings.CountBeforeStart;
            showOverlayBox.IsChecked = settings.ShowOverlay;
            loading = false;

            maxBox.TextChanged += delegate { ApplyFromControls(); };
            alertBox.TextChanged += delegate { ApplyFromControls(); };
            fontBox.TextChanged += delegate { ApplyFromControls(); };
            normalColorBox.TextChanged += delegate { ApplyFromControls(); };
            alertColorBox.TextChanged += delegate { ApplyFromControls(); };
            countBeforeStartBox.Checked += delegate { ApplyFromControls(); };
            countBeforeStartBox.Unchecked += delegate { ApplyFromControls(); };
            showOverlayBox.Checked += delegate { ApplyFromControls(); };
            showOverlayBox.Unchecked += delegate { ApplyFromControls(); };

            // Swallow key presses that were just consumed by trigger capture so they
            // do not also land in a text box or re-press the focused button.
            PreviewKeyDown += OnPreviewKey;
            PreviewKeyUp += OnPreviewKey;

            SourceInitialized += delegate { hwnd = new WindowInteropHelper(this).Handle; };
            Loaded += OnLoaded;
            Closing += delegate
            {
                InputHook.Uninstall();
                settings.Save();
                overlay.Close();
            };

            RefreshTriggerLabels();
            ApplyBrushes();
        }

        static void AddRow(Grid grid, string label, UIElement content, UIElement extra)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (!string.IsNullOrEmpty(label))
            {
                TextBlock tb = new TextBlock();
                tb.Text = label;
                tb.VerticalAlignment = VerticalAlignment.Center;
                tb.Margin = new Thickness(0, 4, 8, 4);
                Grid.SetRow(tb, r);
                Grid.SetColumn(tb, 0);
                grid.Children.Add(tb);
            }
            FrameworkElement fe = content as FrameworkElement;
            if (fe != null)
            {
                fe.Margin = new Thickness(0, 4, 0, 4);
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            Grid.SetRow(content, r);
            Grid.SetColumn(content, 1);
            if (extra == null) Grid.SetColumnSpan(content, 2);
            grid.Children.Add(content);
            if (extra != null)
            {
                FrameworkElement xe = extra as FrameworkElement;
                if (xe != null) xe.Margin = new Thickness(8, 4, 0, 4);
                Grid.SetRow(extra, r);
                Grid.SetColumn(extra, 2);
                grid.Children.Add(extra);
            }
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            overlay.Show();
            if (!settings.ShowOverlay) overlay.Hide();
            UpdateOverlay();
            InputHook.InputDown += OnInput;
            InputHook.Install();
        }

        void OnPreviewKey(object sender, KeyEventArgs e)
        {
            if (capturing != 0 || Environment.TickCount - lastCaptureTick < 500) e.Handled = true;
        }

        void BeginCapture(int which)
        {
            capturing = which;
            capturePrompt.Text = "Press any key or mouse button to use as the "
                + (which == 1 ? "COUNT" : "START / RESET") + " trigger. Esc cancels.";
        }

        void EndCapture()
        {
            capturing = 0;
            lastCaptureTick = Environment.TickCount;
            capturePrompt.Text = "";
            RefreshTriggerLabels();
            UpdateOverlay();
            settings.Save();
        }

        void RefreshTriggerLabels()
        {
            countTriggerLabel.Text = settings.CountTrigger.Display();
            startTriggerLabel.Text = settings.StartTrigger.Display();
        }

        void ApplyFromControls()
        {
            if (loading) return;
            int i; double d;
            if (int.TryParse(maxBox.Text.Trim(), out i) && i > 0) settings.Max = i;
            if (int.TryParse(alertBox.Text.Trim(), out i) && i >= 0) settings.Alert = i;
            if (double.TryParse(fontBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 8 && d <= 1000) settings.FontSize = d;
            settings.NormalColor = normalColorBox.Text.Trim();
            settings.AlertColor = alertColorBox.Text.Trim();
            settings.CountBeforeStart = countBeforeStartBox.IsChecked == true;
            settings.ShowOverlay = showOverlayBox.IsChecked == true;
            ApplyBrushes();
            if (settings.ShowOverlay) { if (!overlay.IsVisible) overlay.Show(); }
            else overlay.Hide();
            if (count >= settings.Max) count = 0;
            UpdateOverlay();
            settings.Save();
        }

        void ApplyBrushes()
        {
            normalBrush = ParseBrush(settings.NormalColor, Brushes.White);
            alertBrush = ParseBrush(settings.AlertColor, Brushes.Red);
        }

        static Brush ParseBrush(string text, Brush fallback)
        {
            try
            {
                Brush b = (Brush)new BrushConverter().ConvertFromString(text);
                if (b != null) { b.Freeze(); return b; }
            }
            catch { }
            return fallback;
        }

        bool IsOwnWindowInput(Trigger t, int x, int y)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (t.IsMouse)
            {
                POINT p; p.X = x; p.Y = y;
                IntPtr target = WindowFromPoint(p);
                if (target == IntPtr.Zero) return false;
                return GetAncestor(target, GA_ROOT) == hwnd;
            }
            return GetForegroundWindow() == hwnd;
        }

        void OnInput(Trigger t, int x, int y)
        {
            if (capturing != 0)
            {
                if (!t.IsMouse && t.Code == VK_ESCAPE) { EndCapture(); return; }
                if (capturing == 1) settings.CountTrigger = t; else settings.StartTrigger = t;
                EndCapture();
                return;
            }

            if (IsOwnWindowInput(t, x, y)) return;

            if (t.Same(settings.StartTrigger))
            {
                started = true;
                count = 0;
                UpdateOverlay();
                return;
            }

            if (t.Same(settings.CountTrigger))
            {
                if (!started && !settings.CountBeforeStart) return;
                started = true;
                count++;
                if (count >= settings.Max) count = 0;
                UpdateOverlay();
            }
        }

        void UpdateOverlay()
        {
            bool alert = started && settings.Alert > 0 && count >= settings.Alert;
            string shown = started ? count.ToString(CultureInfo.InvariantCulture) : "–";
            overlay.SetDisplay(shown, alert ? alertBrush : normalBrush, settings.FontSize);
            status.Text = started
                ? "Counting. Current count: " + count + " of " + settings.Max + (alert ? " (alert)" : "")
                : "Waiting for the start trigger (" + settings.StartTrigger.Display() + ").";
        }
    }

    // ------------------------------------------------------------------ Entry
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            Settings settings = Settings.Load();
            SettingsWindow win = new SettingsWindow(settings);
            app.MainWindow = win;
            app.Run(win);
        }
    }
}
