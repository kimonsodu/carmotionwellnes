using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Interop;
using Windows.Devices.Sensors;

namespace SteadyOverlay
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var settings = Settings.Load();
            var overlay = new OverlayWindow();
            overlay.ApplySettings(settings);
            var panel = new ControlWindow(overlay);
            overlay.Show();
            panel.Show();
            app.Exit += (s, e) => Settings.Save(overlay);   // remember strength + flips on exit
            app.Run();
        }
    }

    // ---------------------------------------------------------------
    // Persisted user settings (%AppData%\Steady\settings.json).
    // ---------------------------------------------------------------
    public class Settings
    {
        public double Sens { get; set; } = 1.8;
        public int InvertX { get; set; } = 1;
        public int InvertY { get; set; } = 1;
        public double DotScale { get; set; } = 1.0;
        public bool SwapAxes { get; set; } = false;

        static string ConfigDir =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steady");
        static string ConfigPath => System.IO.Path.Combine(ConfigDir, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (System.IO.File.Exists(ConfigPath))
                    return JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(ConfigPath)) ?? new Settings();
            }
            catch { /* corrupt or unreadable -> defaults */ }
            return new Settings();
        }

        public static void Save(OverlayWindow ov)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var s = new Settings { Sens = ov.Sens, InvertX = ov.InvertX, InvertY = ov.InvertY, DotScale = ov.DotScale, SwapAxes = ov.SwapAxes };
                var tmp = ConfigPath + ".tmp";                          // atomic write: a kill
                System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(s));   // mid-write can't
                System.IO.File.Move(tmp, ConfigPath, true);             // corrupt the good file
            }
            catch { /* best effort */ }
        }
    }

    // ---------------------------------------------------------------
    // The transparent, always-on-top, click-through cue overlay.
    // ---------------------------------------------------------------
    public class OverlayWindow : Window
    {
        // --- Win32 click-through ---
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_TOOLWINDOW = 0x80;
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        // --- tunables (mirror the web version) ---
        public double Sens = 1.8;
        public int InvertX = 1;
        public int InvertY = 1;
        public bool Paused = false;
        public double DotScale = 1.0;            // visual dot-size multiplier
        public bool SwapAxes = false;            // swap which axis drives vertical vs horizontal

        // --- status surfaced to the control panel ---
        public string StatusText = "Starting…";
        public event Action<string> StatusChanged;
        void SetStatus(string t) { StatusText = t; StatusChanged?.Invoke(t); }

        readonly Canvas canvas = new Canvas();
        readonly List<Dot> near = new List<Dot>();
        readonly List<Dot> far = new List<Dot>();
        double offX, offY, velX, velY;          // accumulated offset + flow velocity
        double lat, fore, yaw;                   // resolved motion
        double gx, gy, gz; bool gReady;          // gravity estimate
        int oriMode = -1;                        // locked axis mapping (-1 = not yet detected)
        int settleFrames;                        // frames since (re)arm — gates the lock
        double bandW;
        double appliedScale = -1;                // last dot size pushed to the ellipses

        // raw sensor values written by sensor threads, read on the UI tick
        volatile bool hasReading;
        double rawX, rawY, rawZ, rawYaw;

        Accelerometer acc;
        Gyrometer gyro;

        class Dot { public int side; public double lx, y, r, baseAlpha; public Ellipse el; }

        public OverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            IsHitTestVisible = false;

            // cover every monitor
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            Content = canvas;

            SourceInitialized += (s, e) => MakeClickThrough();
            Loaded += OnLoaded;
            SizeChanged += (s, e) => BuildDots();
        }

        public void ApplySettings(Settings s)
        {
            Sens = double.IsFinite(s.Sens) ? Math.Clamp(s.Sens, 0.3, 6) : 1.8;  // keep model in slider range
            InvertX = s.InvertX < 0 ? -1 : 1;
            InvertY = s.InvertY < 0 ? -1 : 1;
            DotScale = double.IsFinite(s.DotScale) ? Math.Clamp(s.DotScale, 0.4, 3.0) : 1.0;
            SwapAxes = s.SwapAxes;
        }

        void MakeClickThrough()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            BuildDots();
            StartSensors();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (a, b) => Frame();
            timer.Start();
        }

        void BuildDots()
        {
            canvas.Children.Clear();
            near.Clear(); far.Clear();
            double W = Width, H = Height;
            if (W <= 0 || H <= 0) return;

            bandW = Math.Max(64, Math.Min(W * 0.22, 240));   // width of each side strip
            double colArea = bandW * H * 2;
            int nNear = (int)(colArea / 7000);
            int nFar = (int)(colArea / 5200);

            var brush = new SolidColorBrush(Color.FromRgb(236, 231, 215));
            brush.Freeze();
            var rnd = new Random();

            void Make(int n, List<Dot> arr, double rMin, double rJit, double alpha)
            {
                for (int i = 0; i < n; i++)
                {
                    var d = new Dot
                    {
                        side = i % 2,                       // alternate left / right strips
                        lx = rnd.NextDouble() * bandW,
                        y = rnd.NextDouble() * H,
                        r = rMin + rnd.NextDouble() * rJit,
                        baseAlpha = alpha
                    };
                    d.el = new Ellipse { Width = d.r * 2, Height = d.r * 2, Fill = brush };
                    canvas.Children.Add(d.el);
                    arr.Add(d);
                }
            }
            Make(nFar, far, 0.9, 0.8, 0.18);
            Make(nNear, near, 1.9, 1.3, 0.9);
            appliedScale = -1;                  // force the next frame to (re)apply dot size
        }

        void ApplyDotScale()
        {
            appliedScale = DotScale;
            void Size(List<Dot> arr) { foreach (var d in arr) { d.el.Width = d.el.Height = d.r * 2 * DotScale; } }
            Size(near); Size(far);
        }

        void StartSensors()
        {
            acc = Accelerometer.GetDefault();
            if (acc != null)
            {
                acc.ReportInterval = Math.Max(acc.MinimumReportInterval, 16u);
                acc.ReadingChanged += (s, e) =>
                {
                    var r = e.Reading;                      // AccelerationX/Y/Z in g, includes gravity
                    rawX = r.AccelerationX * 9.81;
                    rawY = r.AccelerationY * 9.81;
                    rawZ = r.AccelerationZ * 9.81;
                    hasReading = true;
                };
                SetStatus("Reading this laptop’s motion sensor.");
            }
            else
            {
                SetStatus("No accelerometer found on this PC. The dots won’t move — pair a phone, or check this is the 2-in-1.");
            }

            gyro = Gyrometer.GetDefault();
            if (gyro != null)
            {
                gyro.ReportInterval = Math.Max(gyro.MinimumReportInterval, 16u);
                gyro.ReadingChanged += (s, e) => { rawYaw = e.Reading.AngularVelocityZ; };  // deg/s
            }
        }

        // gravity-removed, orientation-aware mapping -> lateral / fore.
        // The axis mapping is detected ONCE (then locked) so a hard turn can't
        // shift the gravity estimate across a branch boundary and leak the
        // lateral axis into the vertical channel. Recenter re-arms detection.
        void Ingest(double rx, double ry, double rz)
        {
            // Gravity is tracked with a low-pass, but a low-pass can't tell a
            // *sustained* linear acceleration from gravity: in a steady curve the
            // centripetal accel is constant, so a fast filter absorbs it within a
            // fraction of a second and the dots stop streaming mid-turn (you only
            // see the onset). So freeze the gravity estimate while real motion is
            // present and only re-learn it when the device is near rest.
            double af, mag;
            if (!gReady) { af = 1.0; mag = 0; }         // snap to the first reading
            else
            {
                double rx0 = rx - gx, ry0 = ry - gy, rz0 = rz - gz;
                mag = Math.Sqrt(rx0 * rx0 + ry0 * ry0 + rz0 * rz0);  // linear accel right now
                af = mag > 0.5 ? 0.0020 : 0.04;          // ~8s τ mid-maneuver, ~0.5s τ at rest
            }
            gx += (rx - gx) * af; gy += (ry - gy) * af; gz += (rz - gz) * af; gReady = true;
            double dx = rx - gx, dy = ry - gy, dz = rz - gz;

            // Pick the dominant-gravity axis mapping. Only *commit* (lock) it once
            // the device is near rest AND gravity has warmed up — otherwise a
            // recenter/launch during a maneuver snaps gravity to a sample carrying
            // linear accel and could lock the wrong lateral/fore axes for the whole
            // session. Until locked, follow the live estimate so motion still works.
            double agx = Math.Abs(gx), agy = Math.Abs(gy), agz = Math.Abs(gz);
            int candidate = (agz >= agx && agz >= agy) ? 0 : (agy >= agx ? 1 : 2);
            settleFrames++;
            if (oriMode < 0 && mag < 0.5 && settleFrames > 30) oriMode = candidate;
            int mode = oriMode < 0 ? candidate : oriMode;

            double nlat, nfore;
            if (mode == 0) { nlat = dx; nfore = dy; }              // device lying flat
            else if (mode == 1) { nlat = dx; nfore = dz; }         // upright landscape (normal use)
            else { nlat = dy; nfore = dz; }                        // upright portrait

            if (SwapAxes) (nlat, nfore) = (nfore, nlat);           // fix mounts where gas/brake lands sideways

            lat += (nlat - lat) * 0.35;
            fore += (nfore - fore) * 0.35;
            yaw += (rawYaw - yaw) * 0.3;
        }

        void Frame()
        {
            double W = Width, H = Height;
            if (hasReading) Ingest(rawX, rawY, rawZ);

            const double dz = 0.10;                 // dead-zone kills jitter when still
            double rawAX = lat + yaw * 0.05;
            double aX = (Math.Abs(rawAX) < dz ? 0 : rawAX) * InvertX;   // turns  -> horizontal
            double aY = (Math.Abs(fore) < dz ? 0 : fore) * InvertY;     // gas/brake -> vertical

            const double decay = 0.94;              // how long flow persists
            const double gain = 0.105;              // accel -> velocity
            if (Paused) { velX *= 0.85; velY *= 0.85; }
            else
            {
                velX = velX * decay - aX * gain * Sens;
                // forward acceleration drives dots DOWN, braking drives them UP
                // (for the common upright mounting; Flip *if* reversed on your car).
                velY = velY * decay - aY * gain * Sens;
            }
            const double vmax = 22;
            velX = Math.Clamp(velX, -vmax, vmax);
            velY = Math.Clamp(velY, -vmax, vmax);

            offX += velX;                           // keep streaming while motion lasts
            offY += velY;

            if (DotScale != appliedScale) ApplyDotScale();

            Render(near, offX, offY, W, H);
            Render(far, offX * 0.55, offY * 0.55, W, H);
        }

        static double Wrap(double v, double m) { v %= m; return v < 0 ? v + m : v; }

        void Render(List<Dot> arr, double ox, double oy, double W, double H)
        {
            foreach (var d in arr)
            {
                double local = Wrap(d.lx + ox, bandW);             // horizontal flow stays in the strip
                double x = d.side == 0 ? local : (W - bandW + local);
                double y = Wrap(d.y + oy, H);
                double t = Math.Min(1.0, Math.Min(y, H - y) / (H * 0.14));
                double fade = 0.35 + 0.65 * t;                     // dim toward the corners
                d.el.Opacity = d.baseAlpha * fade;
                double rr = d.r * DotScale;
                Canvas.SetLeft(d.el, x - rr);
                Canvas.SetTop(d.el, y - rr);
            }
        }

        public void Recenter()
        {
            gReady = false;
            oriMode = -1;                           // re-detect mounting orientation
            settleFrames = 0;                       // re-arm the warm-up gate before re-locking
            lat = fore = yaw = 0;
            velX = velY = offX = offY = 0;
        }
    }

    // ---------------------------------------------------------------
    // Small control panel: strength, flips, pause, recenter, quit.
    // Plus global hotkeys, a tray icon, and minimize-to-tray.
    // ---------------------------------------------------------------
    public class ControlWindow : Window
    {
        // --- global hotkeys ---
        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
        const uint VK_S = 0x53, VK_R = 0x52, VK_V = 0x56, VK_H = 0x48, VK_OEM4 = 0xDB, VK_OEM6 = 0xDD;
        const int HK_PAUSE = 1, HK_RECENTER = 2, HK_DOWN = 3, HK_UP = 4, HK_FLIPV = 5, HK_FLIPH = 6;
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        readonly OverlayWindow ov;
        Slider slider, sizeSlider;
        CheckBox pause, flipV, flipH, swap;
        TextBlock hotkeyHint;
        System.Windows.Forms.NotifyIcon notify;
        System.Windows.Forms.ContextMenuStrip trayMenu;
        System.Drawing.Icon trayIcon;
        HwndSource hwndSource;
        bool shuttingDown, teardownDone, reallyQuit;

        public ControlWindow(OverlayWindow ovIn)
        {
            ov = ovIn;
            Title = "Steady";
            Width = 300; Height = 446;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 28; Top = 28;
            Topmost = true;
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x15, 0x1E));
            Foreground = Brushes.White;

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text = "STEADY",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0xD8, 0xC6))
            });

            var status = new TextBlock
            {
                Text = ov.StatusText,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 14)
            };
            root.Children.Add(status);
            ov.StatusChanged += t => Dispatcher.Invoke(() => status.Text = t);

            root.Children.Add(new TextBlock { Text = "Strength", FontSize = 12 });
            slider = new Slider
            {
                Minimum = 0.3,
                Maximum = 6,
                Value = ov.Sens,
                TickFrequency = 0.1,
                IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 2, 0, 12)
            };
            slider.ValueChanged += (s, e) => ov.Sens = e.NewValue;
            root.Children.Add(slider);

            root.Children.Add(new TextBlock { Text = "Dot size", FontSize = 12 });
            sizeSlider = new Slider
            {
                Minimum = 0.4,
                Maximum = 3.0,
                Value = ov.DotScale,
                TickFrequency = 0.1,
                IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 2, 0, 12)
            };
            sizeSlider.ValueChanged += (s, e) => ov.DotScale = e.NewValue;
            root.Children.Add(sizeSlider);

            var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            pause = new CheckBox { Content = "Pause", Foreground = Brushes.White, Margin = new Thickness(0, 0, 14, 0) };
            pause.Checked += (s, e) => ov.Paused = true;
            pause.Unchecked += (s, e) => ov.Paused = false;
            flipV = new CheckBox { Content = "Flip ↕", Foreground = Brushes.White, Margin = new Thickness(0, 0, 14, 0) };
            flipV.Checked += (s, e) => ov.InvertY = -1;
            flipV.Unchecked += (s, e) => ov.InvertY = 1;
            flipH = new CheckBox { Content = "Flip ↔", Foreground = Brushes.White };
            flipH.Checked += (s, e) => ov.InvertX = -1;
            flipH.Unchecked += (s, e) => ov.InvertX = 1;
            flipV.IsChecked = ov.InvertY == -1;     // reflect persisted settings
            flipH.IsChecked = ov.InvertX == -1;
            row1.Children.Add(pause);
            row1.Children.Add(flipV);
            row1.Children.Add(flipH);
            root.Children.Add(row1);

            swap = new CheckBox
            {
                Content = "Swap ↕↔  (if gas/brake moves dots sideways)",
                Foreground = Brushes.White,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 10)
            };
            swap.Checked += (s, e) => ov.SwapAxes = true;
            swap.Unchecked += (s, e) => ov.SwapAxes = false;
            swap.IsChecked = ov.SwapAxes;            // reflect persisted setting
            root.Children.Add(swap);

            var row2 = new StackPanel { Orientation = Orientation.Horizontal };
            var recenter = new Button { Content = "Recenter", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
            recenter.Click += (s, e) => ov.Recenter();
            var quit = new Button { Content = "Quit", Padding = new Thickness(12, 5, 12, 5) };
            quit.Click += (s, e) => QuitApp();
            row2.Children.Add(recenter);
            row2.Children.Add(quit);
            root.Children.Add(row2);

            hotkeyHint = new TextBlock
            {
                Text = "Hotkeys (work anywhere):\n" +
                       "Ctrl+Alt+S  pause/resume\n" +
                       "Ctrl+Alt+R  recenter\n" +
                       "Ctrl+Alt+[ / ]  strength − / +\n" +
                       "Ctrl+Alt+V / H  flip ↕ / ↔\n" +
                       "Close [X] / Minimize → tray · Quit to exit",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x74, 0x86)),
                Margin = new Thickness(0, 14, 0, 0)
            };
            root.Children.Add(hotkeyHint);

            Content = root;

            SetupTray();
            SourceInitialized += (s, e) => RegisterHotKeys();
            StateChanged += (s, e) => { if (WindowState == WindowState.Minimized) Hide(); };  // minimize -> tray
            Closing += (s, e) => { if (!reallyQuit) { e.Cancel = true; Hide(); } };           // [X] -> tray
            Dispatcher.ShutdownStarted += (s, e) => Teardown();                               // belt-and-suspenders
            Closed += OnClosed;
        }

        void QuitApp()
        {
            reallyQuit = true;
            Application.Current.Shutdown();
        }

        // --- global hotkeys ---
        void RegisterHotKeys()
        {
            var h = new WindowInteropHelper(this).Handle;
            hwndSource = HwndSource.FromHwnd(h);
            hwndSource?.AddHook(WndProc);
            uint cm = MOD_CONTROL | MOD_ALT;
            var fails = new List<string>();
            void Reg(int id, uint mod, uint vk, string name) { if (!RegisterHotKey(h, id, mod, vk)) fails.Add(name); }
            Reg(HK_PAUSE, cm | MOD_NOREPEAT, VK_S, "S");
            Reg(HK_RECENTER, cm | MOD_NOREPEAT, VK_R, "R");
            Reg(HK_DOWN, cm, VK_OEM4, "[");                     // repeatable: hold to ramp
            Reg(HK_UP, cm, VK_OEM6, "]");
            Reg(HK_FLIPV, cm | MOD_NOREPEAT, VK_V, "V");
            Reg(HK_FLIPH, cm | MOD_NOREPEAT, VK_H, "H");
            if (fails.Count > 0 && hotkeyHint != null)
                hotkeyHint.Text += "\n⚠ Ctrl+Alt+" + string.Join("/", fails) + " in use by another app";
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                switch (wParam.ToInt32())
                {
                    case HK_PAUSE: pause.IsChecked = !(pause.IsChecked ?? false); handled = true; break;
                    case HK_RECENTER: ov.Recenter(); handled = true; break;
                    case HK_DOWN: StepStrength(-0.3); handled = true; break;
                    case HK_UP: StepStrength(+0.3); handled = true; break;
                    case HK_FLIPV: flipV.IsChecked = !(flipV.IsChecked ?? false); handled = true; break;
                    case HK_FLIPH: flipH.IsChecked = !(flipH.IsChecked ?? false); handled = true; break;
                }
            }
            return IntPtr.Zero;
        }

        void StepStrength(double d)
        {
            double v = Math.Round((ov.Sens + d) * 10) / 10.0;   // snap to 0.1
            slider.Value = Math.Clamp(v, slider.Minimum, slider.Maximum);
        }

        // --- system tray ---
        void SetupTray()
        {
            trayMenu = new System.Windows.Forms.ContextMenuStrip();
            var miShow = new System.Windows.Forms.ToolStripMenuItem("Show panel");
            miShow.Click += (s, e) => ShowPanel();
            var miPause = new System.Windows.Forms.ToolStripMenuItem("Pause / Resume");
            miPause.Click += (s, e) => pause.IsChecked = !(pause.IsChecked ?? false);
            var miRecenter = new System.Windows.Forms.ToolStripMenuItem("Recenter");
            miRecenter.Click += (s, e) => ov.Recenter();
            var miQuit = new System.Windows.Forms.ToolStripMenuItem("Quit");
            miQuit.Click += (s, e) => QuitApp();
            trayMenu.Items.AddRange(new System.Windows.Forms.ToolStripItem[]
            {
                miShow, miPause, miRecenter, new System.Windows.Forms.ToolStripSeparator(), miQuit
            });

            trayIcon = BuildTrayIcon();
            notify = new System.Windows.Forms.NotifyIcon
            {
                Icon = trayIcon,
                Visible = true,
                Text = "Steady",
                ContextMenuStrip = trayMenu
            };
            notify.DoubleClick += (s, e) => TogglePanel();
        }

        static System.Drawing.Icon BuildTrayIcon()
        {
            var bmp = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x10, 0x15, 0x1E));
                g.FillEllipse(bg, 1, 1, 30, 30);
                using var fg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x6F, 0xD8, 0xC6));
                g.FillEllipse(fg, 9, 9, 14, 14);
            }
            IntPtr hicon = bmp.GetHicon();                       // unmanaged HICON — not owned by FromHandle
            using var tmp = System.Drawing.Icon.FromHandle(hicon);
            var icon = (System.Drawing.Icon)tmp.Clone();         // managed copy that owns its own handle
            DestroyIcon(hicon);                                  // free the GetHicon handle
            bmp.Dispose();
            return icon;
        }

        void ShowPanel()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        void TogglePanel()
        {
            if (IsVisible && WindowState != WindowState.Minimized) Hide();
            else ShowPanel();
        }

        void OnClosed(object sender, EventArgs e)
        {
            Teardown();
            if (!shuttingDown) { shuttingDown = true; Application.Current.Shutdown(); }
        }

        // Idempotent: runs from Window.Closed and from Dispatcher.ShutdownStarted,
        // so the tray icon and hotkeys are always released even on odd exit paths.
        void Teardown()
        {
            if (teardownDone) return;
            teardownDone = true;
            try
            {
                var h = new WindowInteropHelper(this).Handle;
                for (int id = HK_PAUSE; id <= HK_FLIPH; id++) UnregisterHotKey(h, id);
                hwndSource?.RemoveHook(WndProc);
            }
            catch { }
            if (notify != null) { notify.Visible = false; notify.Dispose(); notify = null; }
            trayMenu?.Dispose();
            trayIcon?.Dispose();
        }
    }
}
