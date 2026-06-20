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
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Media.Imaging;
using QRCoder;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

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
            var server = new PhoneServer(overlay.FeedPhone);   // phone sensor bridge
            var panel = new ControlWindow(overlay, server);
            overlay.Show();
            panel.Show();
            server.Start();
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
        public string DebugText = "";            // live pipeline state for the panel's debug readout
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
        int tiltHold;                            // frames left of fast gravity-tracking after a tilt
        double bandW;
        double appliedScale = -1;                // last dot size pushed to the ellipses

        // raw sensor values written by sensor threads, read on the UI tick
        volatile bool hasReading;
        double rawX, rawY, rawZ;
        double rawGyroX, rawGyroY, rawYaw;       // gyro deg/s (rawYaw = Z)
        bool hasGyro;

        Accelerometer acc;
        Gyrometer gyro;

        // --- phone bridge: while a phone frame is fresh it overrides the laptop sensor ---
        long lastPhoneTick = long.MinValue / 2;
        bool lastPhoneActive;
        public bool PhoneActive => Environment.TickCount64 - lastPhoneTick < 1500;

        // Called from the phone-server thread; mirrors exactly what the laptop
        // sensor handlers write (accel in m/s² incl. gravity, gyro in deg/s) so the
        // rest of the pipeline — gravity removal, axis auto-map, Flip/Swap — is unchanged.
        public void FeedPhone(double ax, double ay, double az, double gx, double gy, double gz, bool gyroValid)
        {
            rawX = ax; rawY = ay; rawZ = az; hasReading = true;
            if (gyroValid) { rawGyroX = gx; rawGyroY = gy; rawYaw = gz; hasGyro = true; }
            lastPhoneTick = Environment.TickCount64;
        }

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
            ApplyDotScale();                    // size + place dots now so the first paint is correct
            Render(near, offX, offY, W, H);
            Render(far, offX * 0.55, offY * 0.55, W, H);
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
                    if (PhoneActive) return;                 // phone is driving — ignore the laptop sensor
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
                SetStatus("No sensor on this PC — use your phone: open the link in the Phone section below.");
            }

            gyro = Gyrometer.GetDefault();
            if (gyro != null)
            {
                hasGyro = true;
                gyro.ReportInterval = Math.Max(gyro.MinimumReportInterval, 16u);
                gyro.ReadingChanged += (s, e) =>
                {
                    if (PhoneActive) return;            // phone is driving — ignore the laptop gyro
                    var g = e.Reading;                  // deg/s
                    rawGyroX = g.AngularVelocityX;
                    rawGyroY = g.AngularVelocityY;
                    rawYaw = g.AngularVelocityZ;
                };
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
            // Is the device being *tilted* (e.g. opening/closing the lid)? That rotates
            // the gravity direction in the sensor frame, which the freeze below would
            // otherwise mistake for a sustained linear acceleration and keep streaming
            // for many seconds. A tilt shows up as gyro rotation *perpendicular* to
            // gravity; a car turn (yaw about vertical) is parallel to gravity, and
            // gas/brake has no rotation — so neither trips this.
            double tiltRate = 0, yawRate = 0;        // yawRate = spin about gravity = true car yaw (mount-independent)
            if (hasGyro && gReady)
            {
                double gm = Math.Sqrt(gx * gx + gy * gy + gz * gz);
                if (gm > 1e-3)
                {
                    double ux = gx / gm, uy = gy / gm, uz = gz / gm;
                    yawRate = rawGyroX * ux + rawGyroY * uy + rawYaw * uz;           // spin about gravity (yaw)
                    double px = rawGyroX - yawRate * ux, py = rawGyroY - yawRate * uy, pz = rawYaw - yawRate * uz;
                    tiltRate = Math.Sqrt(px * px + py * py + pz * pz);               // gravity-direction rotation
                }
            }

            double af, mag = 0;
            if (!gReady) { af = 1.0; }                  // snap to the first reading
            else
            {
                double rx0 = rx - gx, ry0 = ry - gy, rz0 = rz - gz;
                mag = Math.Sqrt(rx0 * rx0 + ry0 * ry0 + rz0 * rz0);  // linear accel right now
                if (tiltRate > 6.0) tiltHold = 30;       // hold fast-tracking ~0.5s past the last tilt sample
                if (tiltHold > 0) { af = 0.20; tiltHold--; }  // lid tilting -> track gravity fast, no phantom drift
                else af = mag > 0.5 ? 0.0020 : 0.04;     // ~8s τ mid-maneuver, ~0.5s τ at rest
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
            if (oriMode < 0 && mag < 0.5 && tiltHold == 0 && settleFrames > 30) oriMode = candidate;
            int mode = oriMode < 0 ? candidate : oriMode;

            double nlat, nfore;
            if (mode == 0) { nlat = dx; nfore = dy; }              // device lying flat
            else if (mode == 1) { nlat = dx; nfore = dz; }         // upright landscape (normal use)
            else { nlat = dy; nfore = dz; }                        // upright portrait

            if (SwapAxes) (nlat, nfore) = (nfore, nlat);           // fix mounts where gas/brake lands sideways

            lat += (nlat - lat) * 0.35;
            fore += (nfore - fore) * 0.35;
            yaw += (yawRate - yaw) * 0.3;            // yaw about gravity, not raw device-Z
        }

        void Frame()
        {
            double W = Width, H = Height;

            bool pa = PhoneActive;
            if (pa != lastPhoneActive)
            {
                lastPhoneActive = pa;
                ArmForNewSource();                       // re-learn gravity + axis map for the new source
                if (!pa)                                 // phone stream lost
                {
                    // No live laptop accel? Stop ingesting the phone's last (frozen) frame —
                    // otherwise its stale yaw keeps streaming the dots sideways forever.
                    if (acc == null) hasReading = false;
                    // No real laptop gyro? Drop the stale phone gyro so it can't drive yaw/tilt.
                    if (gyro == null) { hasGyro = false; rawGyroX = rawGyroY = rawYaw = 0; }
                }
                SetStatus(pa
                    ? "Phone connected — streaming its motion."
                    : (acc != null
                        ? "Phone disconnected — using this laptop’s sensor."
                        : "Phone disconnected — reopen the page on your phone to stream motion."));
            }

            if (hasReading) Ingest(rawX, rawY, rawZ);

            const double dz = 0.10;                 // dead-zone kills jitter when still
            // Turns are driven mainly by yaw rate about gravity (works in ANY mount orientation —
            // flat, upright, on-side) plus the felt lateral g. yaw is deg/s; lat is m/s².
            double rawAX = lat + yaw * 0.15;
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

            DebugText =
                $"src:{(PhoneActive ? "PHONE" : (acc != null ? "laptop" : "none"))}  mode:{oriMode}  gReady:{(gReady ? 1 : 0)}\n" +
                $"in  a:{rawX,7:0.0}{rawY,7:0.0}{rawZ,7:0.0}   |a|:{Math.Sqrt(rawX * rawX + rawY * rawY + rawZ * rawZ),5:0.0}\n" +
                $"grav :{gx,7:0.0}{gy,7:0.0}{gz,7:0.0}\n" +
                $"gyro :{rawGyroX,7:0.0}{rawGyroY,7:0.0}{rawYaw,7:0.0}\n" +
                $"lat:{lat,7:0.00}  fore:{fore,7:0.00}\n" +
                $"vel:{velX,7:0.0}  {velY,7:0.0}";
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
            tiltHold = 0;
            lat = fore = yaw = 0;
            velX = velY = offX = offY = 0;
        }

        // Re-arm gravity + axis-map learning when the motion source switches
        // (laptop<->phone): their gravity vectors/orientations differ, so a carried-over
        // estimate would read as seconds of phantom linear accel and lock the wrong axes.
        // Unlike Recenter it leaves offX/offY, so the dots don't visibly snap.
        void ArmForNewSource()
        {
            gReady = false;
            oriMode = -1;
            settleFrames = 0;
            tiltHold = 0;
            lat = fore = yaw = 0;
            velX = velY = 0;
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
        const uint VK_P = 0x50, VK_R = 0x52, VK_V = 0x56, VK_H = 0x48, VK_OEM4 = 0xDB, VK_OEM6 = 0xDD;
        const int HK_PAUSE = 1, HK_RECENTER = 2, HK_DOWN = 3, HK_UP = 4, HK_FLIPV = 5, HK_FLIPH = 6;
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        readonly OverlayWindow ov;
        readonly PhoneServer server;
        Slider slider, sizeSlider;
        CheckBox pause, flipV, flipH, swap, dbg;
        TextBlock hotkeyHint, dbgText;
        TextBlock phoneState;
        TextBox phoneUrl;
        TextBlock phoneBt;
        System.Windows.Controls.Image qrImage;
        string shownUrl;
        System.Windows.Forms.NotifyIcon notify;
        System.Windows.Forms.ContextMenuStrip trayMenu;
        System.Drawing.Icon trayIcon;
        HwndSource hwndSource;
        bool shuttingDown, teardownDone, reallyQuit;

        public ControlWindow(OverlayWindow ovIn, PhoneServer serverIn)
        {
            ov = ovIn;
            server = serverIn;
            Title = "Steady";
            Width = 320; Height = 680;
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

            // --- phone sensor pairing ---
            root.Children.Add(new TextBlock
            {
                Text = "PHONE SENSOR",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0xD8, 0xC6)),
                Margin = new Thickness(0, 18, 0, 4)
            });
            phoneState = new TextBlock
            {
                Text = "Starting server…",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6))
            };
            root.Children.Add(phoneState);
            phoneBt = new TextBlock          // primary path — shown first
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6)),
                Margin = new Thickness(0, 6, 0, 8)
            };
            root.Children.Add(phoneBt);
            phoneUrl = new TextBox
            {
                IsReadOnly = true,
                Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1F, 0x2B)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 12,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(phoneUrl);
            qrImage = new System.Windows.Controls.Image
            {
                Width = 184,
                Height = 184,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 8),
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(qrImage, BitmapScalingMode.NearestNeighbor);
            root.Children.Add(qrImage);
            root.Children.Add(new TextBlock
            {
                Text = "Bluetooth needs no network — just pair once. The QR opens the Steady Phone app with the " +
                       "WiFi address pre-filled (no browser warning). Install the app once, then pick Bluetooth or WiFi in it.",
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x74, 0x86))
            });

            // --- debug readout (diagnose what the pipeline actually sees) ---
            dbg = new CheckBox
            {
                Content = "Debug readout",
                Foreground = Brushes.White,
                FontSize = 11,
                Margin = new Thickness(0, 16, 0, 6)
            };
            root.Children.Add(dbg);
            dbgText = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xE6, 0xD8)),
                Visibility = Visibility.Collapsed
            };
            dbg.Checked += (s, e) => dbgText.Visibility = Visibility.Visible;
            dbg.Unchecked += (s, e) => dbgText.Visibility = Visibility.Collapsed;
            root.Children.Add(dbgText);
            var dbgTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            dbgTimer.Tick += (s, e) => { if (dbg.IsChecked == true) dbgText.Text = ov.DebugText; };
            dbgTimer.Start();

            hotkeyHint = new TextBlock
            {
                Text = "Hotkeys (work anywhere):\n" +
                       "Ctrl+Alt+P  pause/resume\n" +
                       "Ctrl+Alt+R  recenter\n" +
                       "Ctrl+Alt+[ / ]  strength − / +\n" +
                       "Ctrl+Alt+V / H  flip ↕ / ↔\n" +
                       "Close [X] / Minimize → tray · Quit to exit",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x74, 0x86)),
                Margin = new Thickness(0, 14, 0, 0)
            };
            root.Children.Add(hotkeyHint);

            Content = new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            // refresh URL/QR/connection state (IPs can appear after launch via USB tether)
            var phoneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            phoneTimer.Tick += (s, e) => UpdatePhone();
            phoneTimer.Start();
            if (server != null) server.StateChanged += () => Dispatcher.Invoke(UpdatePhone);
            UpdatePhone();

            SetupTray();
            SourceInitialized += (s, e) => RegisterHotKeys();
            StateChanged += (s, e) => { if (WindowState == WindowState.Minimized) Hide(); };  // minimize -> tray
            Closing += (s, e) => { if (!reallyQuit) { e.Cancel = true; Hide(); } };           // [X] -> tray
            Dispatcher.ShutdownStarted += (s, e) => Teardown();                               // belt-and-suspenders
            if (Application.Current != null)
                Application.Current.SessionEnding += (s, e) => reallyQuit = true;             // let Windows log off / shut down
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Teardown();                      // clear tray even on abnormal exit
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
            Reg(HK_PAUSE, cm | MOD_NOREPEAT, VK_P, "P");
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

        // --- phone pairing panel refresh ---
        void UpdatePhone()
        {
            if (server == null || phoneState == null) return;
            server.RefreshUrls();
            if (!string.IsNullOrEmpty(server.Error))
            {
                phoneState.Text = "Phone server error: " + server.Error;
                phoneState.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x8A));
                return;
            }
            if (server.Connected || ov.PhoneActive)          // WS client OR fresh UDP frames
            {
                phoneState.Text = "Phone connected ✓ — streaming.";
                phoneState.Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0xD8, 0xC6));
            }
            else if (server.Urls.Count > 0)
            {
                phoneState.Text = "Waiting for phone…";
                phoneState.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6));
            }
            else
            {
                phoneState.Text = "Waiting for phone… (use Bluetooth below, or a hotspot/tether for WiFi)";
                phoneState.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA6));
            }

            phoneBt.Text = server.BtListening
                ? $"① Bluetooth (recommended, no network) — pair this PC (\"{server.BtName}\") in the phone's Bluetooth settings, then in the app pick Bluetooth ▸ this PC."
                : "① Bluetooth — " + (string.IsNullOrEmpty(server.BtError) ? "starting…" : server.BtError);
            string appLink = string.IsNullOrEmpty(server.PrimaryIp) ? ""
                : $"steady://connect?host={server.PrimaryIp}&port={server.Port}";
            phoneUrl.Text = string.IsNullOrEmpty(server.PrimaryIp) ? ""
                : $"② WiFi — same hotspot, same WiFi, or USB tether.\n   Scan the QR to open the app pre-filled, or enter  {server.PrimaryIp} : {server.Port}\n   Browser fallback: {server.PrimaryUrl}  (warns 'not secure' → Advanced ▸ proceed)";
            if (appLink != shownUrl)               // re-render the QR only when the target changes
            {
                shownUrl = appLink;
                qrImage.Source = LoadPng(string.IsNullOrEmpty(appLink) ? null : PhoneServer.QrPng(appLink));
            }
        }

        static BitmapImage LoadPng(byte[] png)
        {
            if (png == null) return null;
            try
            {
                var img = new BitmapImage();
                using var ms = new MemoryStream(png);
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch { return null; }
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
                if (notify != null) { notify.Visible = false; notify.Dispose(); notify = null; }
                trayMenu?.Dispose();
                trayIcon?.Dispose();
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------
    // Phone sensor bridge: a tiny self-signed HTTPS + WebSocket server.
    //
    // Why HTTPS at all on a LAN? Browsers block the motion-sensor APIs on
    // insecure origins (no http://<lan-ip>), so a self-signed cert — which the
    // user accepts once on the phone — is the price of a zero-install page.
    //
    // The phone opens the page over any *local* link (WiFi hotspot or USB
    // tether — no internet/cell needed), streams DeviceMotion (accel in m/s²
    // incl. gravity + gyro in deg/s) over a WebSocket, and we feed each frame
    // into the same pipeline the laptop sensor uses.
    //
    // Self-contained: raw TcpListener + SslStream (no http.sys/netsh binding,
    // so no admin needed) with a minimal HTTP + RFC 6455 WebSocket reader.
    // ---------------------------------------------------------------
    public class PhoneServer
    {
        readonly Action<double, double, double, double, double, double, bool> onFrame;
        public event Action StateChanged;
        public bool Connected { get; private set; }
        public int Port { get; private set; }
        public string PrimaryUrl { get; private set; } = "";
        public string PrimaryIp { get; private set; } = "";
        public List<string> Urls { get; private set; } = new List<string>();
        public string Error { get; private set; }

        // Bluetooth (RFCOMM/SPP) — the no-network transport. Must match the Android app's UUID.
        public static readonly Guid BtUuid = new Guid("b1a7e94c-1c3a-4e7e-9b2a-0a1b2c3d4e5f");
        public bool BtListening { get; private set; }
        public string BtName { get; private set; } = "";
        public string BtError { get; private set; } = "";

        TcpListener listener;
        UdpClient udp;                      // plain UDP ingest for the native app (no TLS needed off-browser)
        BluetoothListener btListener;
        volatile X509Certificate2 cert;     // swapped at runtime when link IPs change; read per-connection
        string certIps = "";
        int clients;

        static string Dir => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Steady");

        public PhoneServer(Action<double, double, double, double, double, double, bool> onFrame)
        {
            this.onFrame = onFrame;
        }

        public void Start()
        {
            try
            {
                var ips = LocalIPs();
                cert = LoadOrCreateCert(ips);
                certIps = IpsKey(ips);
                Port = Bind();
                RefreshUrls();
                TryOpenFirewall();
                new Thread(AcceptLoop) { IsBackground = true, Name = "PhoneServer" }.Start();
                try
                {
                    udp = new UdpClient(new IPEndPoint(IPAddress.Any, Port));  // same port, UDP namespace
                    new Thread(UdpLoop) { IsBackground = true, Name = "PhoneUDP" }.Start();
                }
                catch { /* UDP optional; the browser WS path still works */ }
                StartBt();
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                StateChanged?.Invoke();
            }
        }

        int Bind()
        {
            for (int p = 8443; p < 8443 + 20; p++)
            {
                try { listener = new TcpListener(IPAddress.Any, p); listener.Start(); return p; }
                catch (SocketException) { /* in use — try next */ }
            }
            throw new Exception("no free TCP port in 8443–8462");
        }

        static List<IPAddress> LocalIPs()
        {
            var list = new List<IPAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        var a = ua.Address;
                        if (a.AddressFamily != AddressFamily.InterNetwork) continue;  // IPv4 only
                        if (IPAddress.IsLoopback(a)) continue;
                        if (!list.Contains(a)) list.Add(a);
                    }
                }
            }
            catch { }
            list.Sort((x, y) => Rank(x).CompareTo(Rank(y)));   // hotspot/tether ranges first
            return list;
        }

        // Prefer direct-link subnets so the QR/primary auto-targets a USB tether or hotspot
        // over the laptop's home-WiFi address.
        static int Rank(IPAddress a)
        {
            var b = a.GetAddressBytes();
            if (b[0] == 192 && b[1] == 168 && b[2] == 42) return 0;   // Android USB tether
            if (b[0] == 192 && b[1] == 168 && b[2] == 43) return 0;   // Android hotspot (legacy)
            if (b[0] == 172 && b[1] == 20 && b[2] == 10) return 0;    // iPhone hotspot
            if (b[0] == 192 && b[1] == 168) return 1;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return 2;
            if (b[0] == 10) return 3;
            return 4;
        }

        public bool RefreshUrls()
        {
            if (Port == 0) return false;
            var ips = LocalIPs();
            EnsureCert(ips);                                 // keep cert SAN matching the live link IP(s)
            var urls = new List<string>();
            foreach (var ip in ips) urls.Add($"https://{ip}:{Port}/");
            bool changed = urls.Count != Urls.Count;
            if (!changed) for (int i = 0; i < urls.Count; i++) if (urls[i] != Urls[i]) { changed = true; break; }
            if (changed)
            {
                Urls = urls;
                PrimaryUrl = urls.Count > 0 ? urls[0] : "";
                PrimaryIp = ips.Count > 0 ? ips[0].ToString() : "";
            }
            return changed;
        }

        static string IpsKey(List<IPAddress> ips) => string.Join(",", ips.Select(i => i.ToString()));

        // Rebuild (or reload from cache) the cert when the live IP set changes, so its SAN
        // matches the URL the phone actually opens. Without this the launch-time cert (often
        // localhost-only, before the hotspot/tether is up) triggers a name-mismatch warning on
        // top of the self-signed one — the hard-fail case on iOS Safari. The on-disk cache is
        // keyed by IP set, so a given network is regenerated once then reused across launches.
        void EnsureCert(List<IPAddress> ips)
        {
            string want = IpsKey(ips);
            if (want == certIps) return;
            try { cert = LoadOrCreateCert(ips); certIps = want; }
            catch { /* keep the previous cert */ }
        }

        // --- TLS cert: cache one keyed by the current IP set; regenerate when IPs change ---
        X509Certificate2 LoadOrCreateCert(List<IPAddress> ips)
        {
            string pfx = System.IO.Path.Combine(Dir, "cert.pfx");
            string ipsFile = System.IO.Path.Combine(Dir, "cert.ips");
            string want = string.Join(",", ips.Select(i => i.ToString()));
            try
            {
                if (System.IO.File.Exists(pfx) && System.IO.File.Exists(ipsFile)
                    && System.IO.File.ReadAllText(ipsFile) == want)
                {
                    var c = new X509Certificate2(System.IO.File.ReadAllBytes(pfx), "steady",
                        X509KeyStorageFlags.Exportable);
                    if (c.NotAfter > DateTime.Now.AddDays(1)) return c;
                }
            }
            catch { /* corrupt/stale -> regenerate */ }
            return CreateCert(ips, pfx, ipsFile, want);
        }

        static X509Certificate2 CreateCert(List<IPAddress> ips, string pfx, string ipsFile, string want)
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=Steady Overlay", rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            foreach (var ip in ips) san.AddIpAddress(ip);
            req.CertificateExtensions.Add(san.Build());
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            var now = DateTimeOffset.UtcNow;
            using var made = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));
            var bytes = made.Export(X509ContentType.Pfx, "steady");
            try
            {
                Directory.CreateDirectory(Dir);
                System.IO.File.WriteAllBytes(pfx, bytes);
                System.IO.File.WriteAllText(ipsFile, want);
            }
            catch { /* best effort cache */ }
            // re-import from PFX so SChannel can use the key (ephemeral keys can't auth a server)
            return new X509Certificate2(bytes, "steady", X509KeyStorageFlags.Exportable);
        }

        void TryOpenFirewall()
        {
            // best-effort; silently no-ops without admin (Windows then prompts on first connect)
            FirewallRule("TCP");   // browser WS
            FirewallRule("UDP");   // native app
        }

        void FirewallRule(string proto)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall add rule name=\"Steady Phone {proto} {Port}\" " +
                                $"dir=in action=allow protocol={proto} localport={Port}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                });
            }
            catch { }
        }

        void AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { break; }       // listener stopped
                ThreadPool.QueueUserWorkItem(_ => Handle(client));
            }
        }

        // Native-app ingest: one JSON datagram per sample, same shape as the WS frame.
        void UdpLoop()
        {
            var ep = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                byte[] data;
                try { data = udp.Receive(ref ep); }
                catch { break; }       // socket closed
                if (data != null && data.Length > 0) HandleText(data);
            }
        }

        // --- Bluetooth RFCOMM/SPP: no-network transport. Pair the phone with this PC once,
        //     then the app connects to our service UUID and streams newline-delimited JSON. ---
        void StartBt()
        {
            try
            {
                var radio = BluetoothRadio.Default;
                if (radio == null) { BtError = "no Bluetooth radio on this PC"; return; }
                BtName = radio.Name;
                btListener = new BluetoothListener(BtUuid) { ServiceName = "SteadyPhone" };
                btListener.Start();
                BtListening = true;
                new Thread(BtAcceptLoop) { IsBackground = true, Name = "PhoneBT" }.Start();
            }
            catch (Exception ex) { BtError = ex.Message; BtListening = false; }
        }

        void BtAcceptLoop()
        {
            while (true)
            {
                BluetoothClient c;
                try { c = btListener.AcceptBluetoothClient(); }
                catch { break; }       // listener stopped
                ThreadPool.QueueUserWorkItem(_ => BtHandle(c));
            }
        }

        void BtHandle(BluetoothClient c)
        {
            try
            {
                using (c)
                {
                    try { c.Client.ReceiveTimeout = 30000; } catch { }  // free the worker if the link half-opens
                    using var s = c.GetStream();
                    using var reader = new StreamReader(s, Encoding.UTF8);
                    string line;
                    while ((line = reader.ReadLine()) != null)   // one JSON object per line
                        if (line.Length > 0) Parse(line, onFrame);
                }
            }
            catch { /* connection dropped */ }
        }

        void Handle(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 30000;
                client.NoDelay = true;
                using (client)
                using (var net = client.GetStream())
                using (var ssl = new SslStream(net, false))
                {
                    ssl.AuthenticateAsServer(cert, false, SslProtocols.Tls12 | SslProtocols.Tls13, false);
                    var (reqLine, headers) = ReadHeaders(ssl);
                    if (reqLine == null) return;
                    if (headers.TryGetValue("upgrade", out var up) &&
                        up.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        headers.TryGetValue("sec-websocket-key", out var key))
                        ServeWebSocket(ssl, key);
                    else
                        ServeHttp(ssl);
                }
            }
            catch { /* drop the connection */ }
        }

        static (string, Dictionary<string, string>) ReadHeaders(Stream s)
        {
            var buf = new byte[8192];
            int n = 0;
            while (n < buf.Length)
            {
                int r = s.Read(buf, n, 1);
                if (r <= 0) return (null, null);
                n += r;
                if (n >= 4 && buf[n - 4] == 13 && buf[n - 3] == 10 && buf[n - 2] == 13 && buf[n - 1] == 10) break;
            }
            var lines = Encoding.ASCII.GetString(buf, 0, n).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) headers[lines[i].Substring(0, c).Trim().ToLowerInvariant()] = lines[i].Substring(c + 1).Trim();
            }
            return (lines.Length > 0 ? lines[0] : null, headers);
        }

        void ServeHttp(Stream s)
        {
            byte[] body = Encoding.UTF8.GetBytes(PAGE);
            string head =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n";
            s.Write(Encoding.ASCII.GetBytes(head));
            s.Write(body, 0, body.Length);
            s.Flush();
        }

        void ServeWebSocket(Stream s, string key)
        {
            string accept = Convert.ToBase64String(SHA1.HashData(
                Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            string resp =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
            s.Write(Encoding.ASCII.GetBytes(resp));
            s.Flush();

            Interlocked.Increment(ref clients);
            SetConnected(true);
            try
            {
                while (true)
                {
                    var (op, payload) = ReadFrame(s);
                    if (op < 0 || op == 0x8) break;                       // error / close
                    if (op == 0x9) { SendFrame(s, 0xA, payload); continue; }  // ping -> pong
                    if (op == 0x1 && payload != null) HandleText(payload);    // text -> sensor frame
                }
            }
            catch { }
            finally
            {
                if (Interlocked.Decrement(ref clients) <= 0) SetConnected(false);
            }
        }

        void SetConnected(bool c)
        {
            if (Connected == c) return;
            Connected = c;
            StateChanged?.Invoke();
        }

        void HandleText(byte[] payload) => Parse(Encoding.UTF8.GetString(payload), onFrame);

        // Shared JSON-frame parser for every transport (WS, UDP, Bluetooth).
        static void Parse(string json, Action<double, double, double, double, double, double, bool> onFrame)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                double G(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                bool gValid = r.TryGetProperty("g", out var gv) && gv.ValueKind == JsonValueKind.Number && gv.GetDouble() != 0;
                onFrame(G("ax"), G("ay"), G("az"), G("gx"), G("gy"), G("gz"), gValid);
            }
            catch { /* ignore malformed frame */ }
        }

        // minimal RFC 6455 reader; client->server frames are always masked
        static (int, byte[]) ReadFrame(Stream s)
        {
            var h = new byte[2];
            if (!ReadExact(s, h, 0, 2)) return (-1, null);
            int op = h[0] & 0x0F;
            bool masked = (h[1] & 0x80) != 0;
            long len = h[1] & 0x7F;
            if (len == 126)
            {
                var e = new byte[2];
                if (!ReadExact(s, e, 0, 2)) return (-1, null);
                len = (e[0] << 8) | e[1];
            }
            else if (len == 127)
            {
                var e = new byte[8];
                if (!ReadExact(s, e, 0, 8)) return (-1, null);
                len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | e[i];
            }
            if (len < 0 || len > (1 << 20)) return (-1, null);    // 1 MB sanity cap
            var mask = new byte[4];
            if (masked && !ReadExact(s, mask, 0, 4)) return (-1, null);
            var pay = new byte[len];
            if (len > 0 && !ReadExact(s, pay, 0, (int)len)) return (-1, null);
            if (masked) for (int i = 0; i < len; i++) pay[i] ^= mask[i & 3];
            return (op, pay);
        }

        static void SendFrame(Stream s, int opcode, byte[] payload)
        {
            payload ??= Array.Empty<byte>();
            if (payload.Length > 125) return;        // we only ever send tiny control frames
            var f = new byte[2 + payload.Length];
            f[0] = (byte)(0x80 | opcode);
            f[1] = (byte)payload.Length;
            Array.Copy(payload, 0, f, 2, payload.Length);
            s.Write(f, 0, f.Length);
            s.Flush();
        }

        static bool ReadExact(Stream s, byte[] buf, int off, int len)
        {
            int got = 0;
            while (got < len)
            {
                int n = s.Read(buf, off + got, len - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        public static byte[] QrPng(string text)
        {
            try
            {
                var gen = new QRCodeGenerator();
                var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
                return new PngByteQRCode(data).GetGraphic(8);
            }
            catch { return null; }
        }

        // Self-contained phone page (no external assets — works fully offline).
        // accelerationIncludingGravity -> ax/ay/az (m/s²); rotationRate -> gx/gy/gz (deg/s).
        const string PAGE =
"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
<title>Steady - phone sensor</title>
<style>
  html,body{margin:0;height:100%;background:#10151e;color:#e8ecf2;
    font-family:-apple-system,Segoe UI,Roboto,sans-serif;-webkit-user-select:none;user-select:none}
  .wrap{display:flex;flex-direction:column;align-items:center;justify-content:center;
    height:100%;text-align:center;padding:24px;box-sizing:border-box}
  h1{font-size:20px;margin:0 0 6px;color:#6fd8c6;letter-spacing:3px}
  p{color:#8a94a6;font-size:14px;margin:6px 0;line-height:1.4}
  button{margin-top:26px;font-size:21px;padding:18px 40px;border:0;border-radius:14px;
    background:#6fd8c6;color:#06231e;font-weight:700}
  button:active{background:#5bc4b2}
  #live{display:none}
  .big{font-size:22px;font-weight:700;margin-top:8px}
  .ok{color:#6fd8c6}.bad{color:#e08a8a}
  #out{font-family:ui-monospace,Consolas,monospace;font-size:13px;color:#8a94a6;margin-top:16px}
  .hz{font-size:12px;color:#6a7486;margin-top:6px}
</style>
</head>
<body>
<div class="wrap">
  <h1>STEADY</h1>
  <div id="setup">
    <p>Mount the phone in the car.<br>Tap to stream its motion to the laptop.</p>
    <button id="startbtn">Start</button>
    <p class="hz" id="err"></p>
  </div>
  <div id="live">
    <div class="big"><span id="state" class="bad">connecting...</span></div>
    <p>Keep this screen on.<br>Phone can stay mounted &amp; charging.</p>
    <div id="out">-</div>
    <div class="hz" id="hz"></div>
  </div>
</div>
<script>
(function(){
  var ws=null, wsReady=false, lastSend=0, frames=0, lastHz=0, wake=null;
  var stateEl=document.getElementById('state');
  var outEl=document.getElementById('out');
  var hzEl=document.getElementById('hz');
  function setState(t,ok){ stateEl.textContent=t; stateEl.className=ok?'ok':'bad'; }
  function connect(){
    try{ ws=new WebSocket('wss://'+location.host+'/ws'); }
    catch(e){ setState('ws error',false); setTimeout(connect,1500); return; }
    ws.onopen=function(){ wsReady=true; setState('streaming',true); };
    ws.onclose=function(){ wsReady=false; setState('reconnecting...',false); setTimeout(connect,1200); };
    ws.onerror=function(){ try{ ws.close(); }catch(e){} };
  }
  function onMotion(e){
    var aig=e.accelerationIncludingGravity, al=e.acceleration, r=e.rotationRate||{};
    var useIG=(aig && aig.x!=null);                 // prefer gravity-inclusive (lets PC learn orientation)
    var src=useIG?aig:(al||{});                      // fall back to linear accel if IG is missing
    var ax=src.x||0, ay=src.y||0, az=src.z||0;
    var gx=(r.beta==null?0:r.beta), gy=(r.gamma==null?0:r.gamma), gz=(r.alpha==null?0:r.alpha);
    var gValid=(r.alpha!=null||r.beta!=null||r.gamma!=null)?1:0;
    var haveAccel=(useIG||(al&&al.x!=null))?1:0;
    frames++;
    var now=(window.performance&&performance.now)?performance.now():Date.now();
    if(wsReady && now-lastSend>=14){
      lastSend=now;
      try{ ws.send(JSON.stringify({ax:ax,ay:ay,az:az,gx:gx,gy:gy,gz:gz,g:gValid,grav:useIG?1:0})); }catch(e){}
    }
    if(now-lastHz>=400){
      var hz=Math.round(frames*1000/(now-lastHz)); frames=0; lastHz=now;
      var mag=Math.sqrt(ax*ax+ay*ay+az*az);
      var label=useIG?'accel+g':(al&&al.x!=null?'accel(lin)':'NO ACCEL');
      hzEl.textContent=hz+' Hz   |a| '+mag.toFixed(1)+(haveAccel?'':'   (no accelerometer!)');
      outEl.textContent=label+' '+ax.toFixed(1)+' '+ay.toFixed(1)+' '+az.toFixed(1)+
        '   gyro '+gx.toFixed(0)+' '+gy.toFixed(0)+' '+gz.toFixed(0);
    }
  }
  async function reqWake(){
    try{ if('wakeLock' in navigator){ wake=await navigator.wakeLock.request('screen'); } }catch(e){}
  }
  async function start(){
    var err=document.getElementById('err');
    if(typeof DeviceMotionEvent==='undefined'){ err.textContent='This browser has no motion sensor API.'; return; }
    try{
      if(typeof DeviceMotionEvent.requestPermission==='function'){   // iOS
        var p=await DeviceMotionEvent.requestPermission();
        if(p!=='granted'){ err.textContent='Motion permission denied.'; return; }
      }
    }catch(e){ err.textContent='Permission error: '+e; }
    window.addEventListener('devicemotion', onMotion);
    document.getElementById('setup').style.display='none';
    document.getElementById('live').style.display='block';
    reqWake();
    connect();
  }
  document.getElementById('startbtn').addEventListener('click', start);
  document.addEventListener('visibilitychange', function(){
    if(document.visibilityState==='visible') reqWake();
  });
})();
</script>
</body>
</html>
""";
    }
}
