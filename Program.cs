using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Input;
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

namespace OrbitalOverlay
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try { app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("Theme.xaml", UriKind.Relative))); }
            catch { /* styles are cosmetic — run unstyled rather than crash */ }
            var settings = Settings.Load();
            var overlay = new OverlayWindow();
            overlay.ApplySettings(settings);
            // Launched by the Windows Run key (auto-start) -> don't flash the dots at a desk boot;
            // they reveal on real motion or a manual Recenter. Manual launches show them right away.
            if (Environment.CommandLine.Contains("--autostart")) overlay.SuppressDotsOnStartup();
            var server = new PhoneServer(overlay.FeedPhone);   // phone sensor bridge
            var panel = new ControlWindow(overlay, server, settings);
            overlay.Show();
            if (settings.StartMinimized)
                new WindowInteropHelper(panel).EnsureHandle();   // create hwnd (registers hotkeys) but stay in tray
            else
                panel.Show();
            server.Start();
            app.Exit += (s, e) => Settings.Save(overlay, panel);   // remember settings + window geometry on exit
            app.Run();
        }
    }

    // ---------------------------------------------------------------
    // A remappable global shortcut: Win32 modifier flags + virtual-key code.
    // Stored in Settings and (de)serialized straight to JSON.
    // ---------------------------------------------------------------
    public class Hotkey
    {
        public uint Mods { get; set; }   // Win32 MOD_* flags: Alt=1, Control=2, Shift=4, Win=8
        public uint Vk { get; set; }     // virtual-key code (e.g. 0x50 = 'P')
        public Hotkey() { }
        public Hotkey(uint mods, uint vk) { Mods = mods; Vk = vk; }
    }

    // ---------------------------------------------------------------
    // Persisted user settings (%AppData%\Orbital\settings.json).
    // ---------------------------------------------------------------
    public class Settings
    {
        public double Sens { get; set; } = 1.8;
        public double LonGain { get; set; } = 1.5;         // accel/brake trim: SIGN = direction, |v| = sensitivity, 0 = off
        public int InvertX { get; set; } = 1;
        public int InvertY { get; set; } = 1;
        public double DotScale { get; set; } = 1.0;
        public bool SwapAxes { get; set; } = false;
        public int DotStyle { get; set; } = 1;             // 0 = light, 1 = mixed (default, matches Android), 2 = dark, 3 = accent
        public bool DarkDots { get; set; } = false;        // legacy (pre-slider) — migrated to DotStyle on load
        public uint AccentColor { get; set; } = 0;         // packed ARGB for the Accent dot style (0 = brand amber)
        public double DotOpacity { get; set; } = 1.0;      // per-dot brightness multiplier (0.2..1.0)
        public double Density { get; set; } = 1.0;         // dot-count multiplier (0.5..2.0)
        public double Decay { get; set; } = 0.94;          // flow persistence / trail (0.80..0.97)
        public double HideSensitivity { get; set; } = 1.0; // auto-hide knee scale (0.5..2.0)
        public int Placement { get; set; } = 0;            // 0 = side strips, 1 = full peripheral frame
        public int CueStyle { get; set; } = 0;             // 0 Dots,1 Streaks,2 Rails,3 Horizon,4 Flow,5 Chevrons
        public int CueModel { get; set; } = 0;             // 0 velocity-flow, 1 acceleration-pulse
        public bool StartMinimized { get; set; } = false;  // launch hidden in the tray
        public bool AutoPause { get; set; } = true;        // fade the dots out when the vehicle is stopped
        public double? WinLeft { get; set; }               // remembered panel position + size (null = use defaults)
        public double? WinTop { get; set; }
        public double? WinWidth { get; set; }
        public double? WinHeight { get; set; }
        public bool FirstRunDone { get; set; } = false;    // welcome card dismissed
        public bool PhoneOnly { get; set; } = false;       // Phone mode: ignore the laptop sensor, use the phone only
        public bool AutoHideTipSeen { get; set; } = false; // one-time "watch this on your first drive" tip shown
        public Dictionary<string, Hotkey> Hotkeys { get; set; } // remappable global shortcuts (null = built-in defaults)

        static string ConfigDir =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Orbital");
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

        public static void Save(OverlayWindow ov, ControlWindow panel)
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var s = new Settings { Sens = ov.Sens, LonGain = ov.LonGain, InvertX = ov.InvertX, InvertY = ov.InvertY, DotScale = ov.DotScale, SwapAxes = ov.SwapAxes, DotStyle = ov.DotStyle, AccentColor = ov.AccentColor, DotOpacity = ov.DotOpacity, Density = ov.Density, Decay = ov.Decay, HideSensitivity = ov.HideSensitivity, Placement = ov.Placement, CueStyle = ov.CueStyle, CueModel = ov.CueModel, StartMinimized = panel.StartMinimized, AutoPause = ov.AutoPause, FirstRunDone = panel.FirstRunDone, PhoneOnly = ov.PhoneOnly, AutoHideTipSeen = panel.AutoHideTipSeen, Hotkeys = panel.HotkeyConfig };
                var b = panel.RestoreBounds;                            // normal-state bounds even if minimized/hidden
                if (!b.IsEmpty && b.Width > 0 && b.Height > 0)
                {
                    s.WinLeft = b.Left; s.WinTop = b.Top; s.WinWidth = b.Width; s.WinHeight = b.Height;
                }
                var tmp = ConfigPath + ".tmp";                          // atomic write: a kill
                System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(s));   // mid-write can't
                System.IO.File.Move(tmp, ConfigPath, true);             // corrupt the good file
            }
            catch { /* best effort */ }
        }
    }

    // ---------------------------------------------------------------
    // "Start with Windows" — a HKCU Run-key entry (per-user, no admin).
    // ---------------------------------------------------------------
    public static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "Orbital";

        static string ExePath()
        {
            try { return Process.GetCurrentProcess().MainModule?.FileName; } catch { return null; }
        }

        public static bool IsEnabled()
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(Name) != null; }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (on) { var p = ExePath(); if (!string.IsNullOrEmpty(p)) k.SetValue(Name, "\"" + p + "\" --autostart"); }
                else k.DeleteValue(Name, false);
            }
            catch { /* best effort — registry may be locked down */ }
        }
    }

    // ---------------------------------------------------------------
    // Immediate-mode cue renderer — a 1:1 port of the Android DotsView
    // render paths so the two apps share the same cue STYLES and figures.
    // The OverlayWindow owns the physics (flow integrator); this element
    // just draws the current style each frame from that flow state.
    // ---------------------------------------------------------------
    public class CueElement : FrameworkElement
    {
        // --- flow state, written by OverlayWindow.Frame() each tick ---
        public double OffX, OffY, VelX, VelY, AccelEnv;

        // --- live params (mirror Android's DotsView) ---
        public int DotStyle = 1;          // colour mode: 0 light, 1 mixed, 2 dark, 3 accent
        public uint AccentColor = 0;      // packed ARGB for accent mode (0 = brand amber)
        public double DotScale = 1.0;     // element size multiplier
        public double DotOpacity = 1.0;   // overall brightness (0.2..1.0)
        public double Density = 1.0;      // element-count multiplier (0.5..2.0)
        public int Placement = 0;         // 0 side strips, 1 full peripheral frame
        public int CueStyle = 0;          // 0 Dots,1 Streaks,2 Rails,3 Horizon,4 Flow,5 Chevrons
        public int CueModel = 0;          // 0 velocity-flow, 1 acceleration-pulse

        class Dot { public int side; public double lx, y, r, alpha, depth; public int pick; }
        readonly List<Dot> field = new List<Dot>();
        double bandW, bandH;
        int builtW = -1, builtH = -1;
        readonly Random rnd = new Random();

        static readonly Color light = Color.FromArgb(217, 0xEC, 0xE7, 0xD7);  // warm off-white (matches PC/Android light)
        static readonly Color dark  = Color.FromArgb(217, 0x12, 0x16, 0x1E);  // near-bg dark
        static readonly Color amber = Color.FromArgb(217, 0xE8, 0xB6, 0x6F);  // accent default (Android accent_amber)

        readonly Dictionary<uint, SolidColorBrush> brushCache = new Dictionary<uint, SolidColorBrush>();
        readonly Dictionary<long, Pen> penCache = new Dictionary<long, Pen>();

        public CueElement() { IsHitTestVisible = false; }

        public void RebuildField() { builtW = -1; InvalidateVisual(); }   // counts baked in -> force a rebuild

        Color BaseColor(int pick)
        {
            switch (DotStyle)
            {
                case 0: return light;
                case 2: return dark;
                case 3: return AccentColor != 0
                    ? Color.FromArgb(0xFF, (byte)(AccentColor >> 16), (byte)(AccentColor >> 8), (byte)AccentColor)
                    : amber;
                default: return pick == 0 ? light : dark;   // Mixed: each element keeps its own pick
            }
        }

        SolidColorBrush BrushFor(Color c, double mul)
        {
            byte a = (byte)Math.Clamp((int)Math.Round(c.A * mul), 0, 255);
            uint key = ((uint)a << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            if (!brushCache.TryGetValue(key, out var b))
            {
                b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)); b.Freeze();
                brushCache[key] = b;
            }
            return b;
        }

        Pen PenFor(Color c, double mul, double th)
        {
            byte a = (byte)Math.Clamp((int)Math.Round(c.A * mul), 0, 255);
            int ti = (int)Math.Round(th * 4);
            long key = ((long)a << 40) | ((long)c.R << 32) | ((long)c.G << 24) | ((long)c.B << 16) | (uint)ti;
            if (!penCache.TryGetValue(key, out var p))
            {
                p = new Pen(BrushFor(c, mul), th) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                p.Freeze();
                penCache[key] = p;
            }
            return p;
        }

        static double Wrap(double v, double m) { double x = v % m; return x < 0 ? x + m : x; }
        static double CornerFade(double y, double h) => 0.35 + 0.65 * Math.Min(1.0, Math.Min(y, h - y) / (h * 0.14));
        static double SmoothStep(double e0, double e1, double x) { double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t); }
        static double CentreFade(double y, double h) => SmoothStep(0, 1, Math.Abs(y - h * 0.5) / (h * 0.5));

        void BuildField(double w, double h)
        {
            field.Clear();
            bandW = Math.Clamp(w * 0.22, 56, 130);
            bandH = Math.Clamp(h * 0.16, 56, 160);
            double dens = Math.Clamp(Density, 0.5, 2.0);
            double colArea = bandW * h * 2;
            int nNear = Math.Clamp((int)(colArea / 7000 * dens), 8, 200);
            int nFar  = Math.Clamp((int)(colArea / 5200 * dens), 8, 260);
            void Strip(int n, double rMin, double rJit, double alpha, double depth)
            {
                for (int i = 0; i < n; i++)
                    field.Add(new Dot { side = i & 1, lx = rnd.NextDouble() * bandW, y = rnd.NextDouble() * h, r = rMin + rnd.NextDouble() * rJit, alpha = alpha, depth = depth, pick = rnd.Next(2) });
            }
            Strip(nFar, 0.9, 0.8, 0.18, 0.55);
            Strip(nNear, 1.9, 1.3, 0.9, 1.0);
            if (Placement == 1)
            {
                double bandArea = w * bandH * 2;
                int bNear = Math.Clamp((int)(bandArea / 7000 * dens), 6, 200);
                int bFar  = Math.Clamp((int)(bandArea / 5200 * dens), 6, 260);
                void Band(int n, double rMin, double rJit, double alpha, double depth)
                {
                    for (int i = 0; i < n; i++)
                        field.Add(new Dot { side = (i & 1) == 0 ? 2 : 3, lx = rnd.NextDouble() * w, y = rnd.NextDouble() * bandH, r = rMin + rnd.NextDouble() * rJit, alpha = alpha, depth = depth, pick = rnd.Next(2) });
                }
                Band(bFar, 0.9, 0.8, 0.18, 0.55);
                Band(bNear, 1.9, 1.3, 0.9, 1.0);
            }
        }

        (double x, double y) Head(Dot dot, double sX, double sY, double w, double h)
        {
            switch (dot.side)
            {
                case 0:  return (Wrap(dot.lx + sX * dot.depth, bandW), Wrap(dot.y + sY * dot.depth, h));
                case 1:  return (w - bandW + Wrap(dot.lx + sX * dot.depth, bandW), Wrap(dot.y + sY * dot.depth, h));
                case 2:  return (Wrap(dot.lx + sX * dot.depth, w), Wrap(dot.y + sY * dot.depth, bandH));
                default: return (Wrap(dot.lx + sX * dot.depth, w), h - bandH + Wrap(dot.y + sY * dot.depth, bandH));
            }
        }

        double frW, frH, frOp, frSX, frSY, frKFlow, frIntens;

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            if (field.Count == 0 || builtW != (int)w || builtH != (int)h) { BuildField(w, h); builtW = (int)w; builtH = (int)h; }
            frW = w; frH = h; frOp = Math.Clamp(DotOpacity, 0.2, 1.0); frKFlow = 0.6;
            frSX = OffX * frKFlow; frSY = OffY * frKFlow;
            frIntens = CueModel == 1 ? Math.Clamp(AccelEnv, 0, 1) : 1.0;
            switch (CueStyle)
            {
                case 1: Streaks(dc); break;
                case 2: Rails(dc); break;
                case 3: Horizon(dc); break;
                case 4: FlowGrid(dc); break;
                case 5: Chevrons(dc); break;
                default: Dots(dc); break;
            }
        }

        void Dots(DrawingContext dc)
        {
            foreach (var dot in field)
            {
                var (x, y) = Head(dot, frSX, frSY, frW, frH);
                double mul = frOp * dot.alpha * CornerFade(y, frH) * frIntens;
                double rr = dot.r * DotScale;
                dc.DrawEllipse(BrushFor(BaseColor(dot.pick), mul), null, new Point(x, y), rr, rr);
            }
        }

        void Streaks(DrawingContext dc)
        {
            double spd = Math.Sqrt(VelX * VelX + VelY * VelY);
            if (spd < 1e-3) { Dots(dc); return; }                 // steady cruise -> collapse to dots
            double ux = VelX / spd, uy = VelY / spd;
            foreach (var dot in field)
            {
                var (x, y) = Head(dot, frSX, frSY, frW, frH);
                double len = Math.Clamp(spd * frKFlow * dot.depth * 2.2 * (CueModel == 1 ? (0.4 + Math.Clamp(AccelEnv, 0, 1)) : 1.0), 2, 26);
                double mul = frOp * dot.alpha * CornerFade(y, frH) * frIntens;
                double th = Math.Max(1.0, dot.r * DotScale * 0.9);
                dc.DrawLine(PenFor(BaseColor(dot.pick), mul, th), new Point(x, y), new Point(x - ux * len, y - uy * len));
            }
        }

        void Rails(DrawingContext dc)
        {
            double w = frW, h = frH;
            int nBars = Math.Clamp((int)(6 * Density), 3, 12);
            double barH = h / nBars;
            double phase = Wrap(frSY * 0.02, barH);
            var col = BaseColor(0);
            double k = 1.0 / 8.0;
            double leftLit = Math.Clamp(-VelX * k, 0, 1), rightLit = Math.Clamp(VelX * k, 0, 1);
            for (int side = 0; side < 2; side++)
            {
                double x0 = side == 0 ? w * 0.012 : w - bandW + w * 0.012;
                double x1 = side == 0 ? bandW - w * 0.012 : w - w * 0.012;
                double sideI = side == 0 ? leftLit : rightLit;
                for (int i = -1; i <= nBars; i++)
                {
                    double cyTop = i * barH + phase;
                    double segH = barH * 0.6;
                    double mul = frOp * (0.30 + 0.70 * sideI) * CornerFade(cyTop + segH * 0.5, h) * frIntens;
                    dc.DrawRoundedRectangle(BrushFor(col, mul), null, new Rect(x0, cyTop, Math.Max(0, x1 - x0), segH), 4, 4);
                }
            }
        }

        void Horizon(DrawingContext dc)
        {
            double w = frW, h = frH;
            double pitch = Math.Clamp(VelY * 4.0, -h * 0.18, h * 0.18);
            double rollDeg = Math.Clamp(VelX * 1.6, -16, 16);
            double cy = h * 0.5 + pitch;
            var col = BaseColor(0);
            dc.PushTransform(new RotateTransform(rollDeg, w * 0.5, cy));
            var penMain = PenFor(col, frOp * frIntens, 2.4 * DotScale);
            double inset = w * 0.06, gapHalf = w * 0.15;
            dc.DrawLine(penMain, new Point(inset, cy), new Point(w * 0.5 - gapHalf, cy));
            dc.DrawLine(penMain, new Point(w * 0.5 + gapHalf, cy), new Point(w - inset, cy));
            int rungs = Math.Clamp((int)(3 * Density), 2, 6);
            double sp = 26.0;
            double phase = Wrap(frSY * 0.10, sp);
            for (int n = -rungs; n <= rungs; n++)
            {
                double yy = cy + n * sp + phase - sp;
                double ladderA = 1.0 - Math.Abs(yy - cy) / ((rungs + 1) * sp);
                if (ladderA > 0)
                {
                    double half = w * 0.035 * (1 + Math.Abs(n) * 0.12);
                    dc.DrawLine(PenFor(col, 0.5 * ladderA * frIntens * frOp, 1.4 * DotScale), new Point(w * 0.5 - half, yy), new Point(w * 0.5 + half, yy));
                }
            }
            dc.Pop();
        }

        void FlowGrid(DrawingContext dc)
        {
            double w = frW, h = frH;
            double th = 0.8 * DotScale;
            var col = BaseColor(0);
            double horizonY = h * 0.46;
            double vanishX = w * 0.5 + frSX * 0.30;
            double scroll = frSY * 0.012;
            int rows = Math.Clamp((int)(9 * Density), 4, 16);
            int cols = Math.Clamp((int)(7 * Density), 3, 14);
            for (int i = 0; i < rows; i++)
            {
                double z = ((i + scroll) % rows) / rows;
                if (z <= 0) z += 1;
                double y = horizonY + (h - horizonY) * z * z;
                double bank = (vanishX - w * 0.5) * (1 - z);
                double mul = z * CentreFade(y, h) * 0.5 * frIntens * frOp;
                dc.DrawLine(PenFor(col, mul, th), new Point(bank, y), new Point(w + bank, y));
            }
            for (int kk = -cols; kk <= cols; kk++)
            {
                double xB = w * 0.5 + kk * (w * 0.5 / cols) + (vanishX - w * 0.5);
                dc.DrawLine(PenFor(col, 0.35 * frIntens * frOp, th), new Point(vanishX, horizonY), new Point(xB, h));
            }
        }

        void Chevrons(DrawingContext dc)
        {
            double w = frW, h = frH;
            double spd = Math.Sqrt(VelX * VelX + VelY * VelY);
            if (spd < 1e-3) return;
            double mag = Math.Clamp(spd / 8.0, 0, 1);
            double magVis = mag > 0.25 ? mag : 0.25;              // comfort floor while a maneuver is happening
            double ux = VelX / spd, uy = VelY / spd;
            double s = 8.0 * DotScale;
            int k = Math.Clamp((int)(5 * Density), 3, 9);
            var col = BaseColor(0);
            double th = 2.4 * DotScale;
            for (int side = 0; side < 2; side++)
            {
                double cx = side == 0 ? bandW * 0.5 : w - bandW * 0.5;
                for (int i = 0; i < k; i++)
                {
                    double f = (double)i / k + frSY * 0.004;
                    f -= Math.Floor(f);
                    double cyy = f * h;
                    double edge = Math.Min(1.0, Math.Min(cyy, h - cyy) / (h * 0.12));
                    var pen = PenFor(col, magVis * edge * frIntens * frOp, th);
                    double tx = cx + ux * s, ty = cyy + uy * s;   // tip points where the force pushes
                    double px = -uy, py = ux;                     // perpendicular wings
                    double bx = cx - ux * s * 0.25, by = cyy - uy * s * 0.25;
                    dc.DrawLine(pen, new Point(bx + px * s, by + py * s), new Point(tx, ty));
                    dc.DrawLine(pen, new Point(bx - px * s, by - py * s), new Point(tx, ty));
                }
            }
        }
    }

    // ---------------------------------------------------------------
    // Simulation Mode — a synthetic IMU *source* for desk testing.
    // It does NOT bypass the pipeline: it produces raw accel (m/s² incl.
    // gravity) + gyro (deg/s) exactly like a real sensor frame so gravity
    // removal, axis auto-learn, the gate, jerk limit, deadband and the cue
    // renderer are all exercised. When sim is ON it OVERRIDES the real sensor.
    // Device is assumed flat, screen up (R = identity): x = right (East),
    // y = forward (North), z = up; resting reading = (0, 0, +G).
    // ---------------------------------------------------------------
    public enum SimScenario { Off, All, Accelerate, Brake, TurnLeft, TurnRight, Uphill, Downhill, Sideways }

    public class MotionSimulator
    {
        public const double G = 9.81;
        const double Deg = Math.PI / 180.0;
        const double Ramp = 0.6;            // smoothstep ease-in/out per active phase (s) so the cue glides
        const double GradeRate = 4.5;       // deg/s max road-grade pitch rate -> stays under the lid-tilt reject knee (6)

        public SimScenario Scenario = SimScenario.Off;
        public string PhaseName { get; private set; } = "";

        // outputs, written each Tick — mirror a real sensor frame
        public double Ax, Ay, Az;           // accel incl. gravity, m/s²
        public double Gx, Gy, Gz;           // gyro deg/s (Gx = pitch about x, Gz = yaw about z)
        public double Speed = 15;           // GPS ground speed fed to the gate (m/s) — keep "moving" the whole run

        double t;                           // elapsed time within the script (s)
        double grade;                       // current road-grade angle (deg), rate-limited toward target

        struct Phase { public string Name; public double Dur, AFwd, ARight, Yaw, Grade, Psi; }
        static Phase P(string n, double d, double f, double r, double y, double g, double p)
            => new Phase { Name = n, Dur = d, AFwd = f, ARight = r, Yaw = y, Grade = g, Psi = p };

        // The "All" script (loops forever). Each ACTIVE phase eases in/out; REST lets the
        // cue return to neutral and gravity re-settle. Magnitudes match the shared spec.
        static readonly Phase[] Script =
        {
            P("Rest",           1.5,  0,    0,    0,   0,  0),
            P("Accelerate",     3.0,  2.5,  0,    0,   0,  0),
            P("Rest",           1.5,  0,    0,    0,   0,  0),
            P("Brake",          3.0, -3.5,  0,    0,   0,  0),
            P("Rest",           1.5,  0,    0,    0,   0,  0),
            P("Turn left",      3.0,  0,    2.8,  28,  0,  0),
            P("Rest",           1.5,  0,    0,    0,   0,  0),
            P("Turn right",     3.0,  0,   -2.8, -28,  0,  0),
            P("Rest",           1.5,  0,    0,    0,   0,  0),
            P("Uphill",         3.5,  0.4,  0,    0,   9,  0),
            P("Rest",           1.5,  0,    0,    0,   0,  0),
            P("Downhill",       3.5, -0.4,  0,    0,  -9,  0),
            P("Rest",           1.5,  0,    0,    0,   0,  0),
            P("Sideways accel", 3.0,  2.5,  0,    0,   0,  90),
            P("Sideways brake", 3.0, -3.0,  0,    0,   0,  90),
            P("Sideways turn",  3.0,  0,    2.6,  24,  0,  90),
            P("Rest",           2.0,  0,    0,    0,   0,  0),
        };

        // A single held scenario: ease in over Ramp, then hold its target indefinitely (no rest/loop).
        static Phase Single(SimScenario s) => s switch
        {
            SimScenario.Accelerate => P("Accelerate",     0,  2.5, 0,    0,   0,  0),
            SimScenario.Brake      => P("Brake",          0, -3.5, 0,    0,   0,  0),
            SimScenario.TurnLeft   => P("Turn left",      0,  0,   2.8,  28,  0,  0),
            SimScenario.TurnRight  => P("Turn right",     0,  0,  -2.8, -28,  0,  0),
            SimScenario.Uphill     => P("Uphill",         0,  0.4, 0,    0,   9,  0),
            SimScenario.Downhill   => P("Downhill",       0, -0.4, 0,    0,  -9,  0),
            SimScenario.Sideways   => P("Sideways accel", 0,  2.5, 0,    0,   0,  90),
            _                      => P("Rest",           0,  0,   0,    0,   0,  0),
        };

        static double SmoothStep(double e0, double e1, double x)
        { double tt = Math.Clamp((x - e0) / (e1 - e0), 0, 1); return tt * tt * (3 - 2 * tt); }

        // Called when (re)starting: clear time + grade and snap the output to a clean rest frame.
        public void Reset() { t = 0; grade = 0; PhaseName = ""; Ax = Ay = 0; Az = G; Gx = Gy = Gz = 0; }

        public void Tick(double dt)
        {
            if (Scenario == SimScenario.Off) return;
            t += dt;

            double aFwd, aRight, yaw, gradeTarget, psi;
            if (Scenario == SimScenario.All)
            {
                double total = 0; foreach (var ph in Script) total += ph.Dur;
                double tt = t % total;
                int i = 0; while (i < Script.Length - 1 && tt >= Script[i].Dur) { tt -= Script[i].Dur; i++; }
                var p = Script[i];
                double s = Math.Min(SmoothStep(0, Ramp, tt), SmoothStep(0, Ramp, p.Dur - tt));  // ease in + out
                aFwd = p.AFwd * s; aRight = p.ARight * s; yaw = p.Yaw * s;
                gradeTarget = p.Grade;                  // grade is rate-limited below, not smoothstepped
                psi = p.Psi; PhaseName = p.Name;
            }
            else
            {
                var p = Single(Scenario);
                double s = SmoothStep(0, Ramp, t);      // ease in, then hold
                aFwd = p.AFwd * s; aRight = p.ARight * s; yaw = p.Yaw * s;
                gradeTarget = p.Grade; psi = p.Psi; PhaseName = p.Name;
            }

            // Road grade: move the pitch angle toward target at a gentle rate so the emitted
            // pitch gyro stays under Windows' lid-tilt reject knee (tiltRate > 6) — otherwise
            // the tilt would be absorbed into the gravity estimate and the fore cue suppressed.
            double dgr = Math.Clamp(gradeTarget - grade, -GradeRate * dt, GradeRate * dt);
            grade += dgr;
            double pitchRate = dt > 1e-6 ? dgr / dt : 0;    // deg/s about device x

            // device-frame accel = pitched gravity + vehicle linear accel rotated by seating heading ψ.
            // ψ=0 -> x = aRight, y = aFwd (normal car seat); ψ=90 -> forward lands on device-x (train).
            double pr = psi * Deg, th = grade * Deg;
            double sinp = Math.Sin(pr), cosp = Math.Cos(pr);
            double lx = aFwd * sinp + aRight * cosp;
            double ly = aFwd * cosp - aRight * sinp;
            Ax = lx;
            Ay = -G * Math.Sin(th) + ly;                    // road-grade longitudinal component via the gravity path
            Az = G * Math.Cos(th);
            Gx = pitchRate; Gy = 0; Gz = yaw;               // gyro: pitch about x during grade ramp, yaw about z
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
        public double LonGain = 1.5;             // signed accel/brake trim: sign = direction, |v| = sensitivity (composes with InvertY), 0 = off
        public int InvertX = 1;
        public int InvertY = 1;
        public bool Paused = false;
        public double DotScale = 1.0;            // visual dot-size multiplier
        public bool SwapAxes = false;            // swap which axis drives vertical vs horizontal
        public int DotStyle = 1;                 // 0 = light, 1 = mixed (default, matches Android), 2 = dark, 3 = accent
        public uint AccentColor = 0;             // packed ARGB for the Accent dot style (0 = brand amber)
        public double DotOpacity = 1.0;          // per-dot brightness multiplier (0.2..1.0)
        public double Density = 1.0;             // dot-count multiplier (0.5..2.0)
        public double Decay = 0.94;              // flow persistence / trail (0.80..0.97)
        public double HideSensitivity = 1.0;     // auto-hide energy-knee scale (0.5..2.0)
        public int Placement = 0;                // 0 = side strips, 1 = full peripheral frame (adds top/bottom bands)
        public int CueStyle = 0;                 // 0 Dots,1 Streaks,2 Rails,3 Horizon,4 Flow,5 Chevrons (Android parity)
        public int CueModel = 0;                 // 0 velocity-flow, 1 acceleration-pulse
        public bool AutoPause = true;            // fade dots out when motion stops (parked / at a desk)

        // --- motion-energy gate for AutoPause ---
        // Key: constant-speed cruising has ~zero linear accel/turn, so the SMOOTHED
        // maneuver signal can't tell it from parked. Road vibration can: track the
        // high-frequency jitter of raw linear accel — it persists while driving at a
        // steady speed and collapses when actually stopped.
        double magSlow;                          // slow baseline of linear-accel magnitude
        double jitterEma;                        // smoothed HF jitter (road buzz)
        double activityEma;                      // combined motion energy fed to the gate
        int stillFrames;                         // consecutive frames the desired-state has persisted (confirm counter)
        int dwellFrames = 999;                   // frames since the last visibility toggle (min-dwell lockout); init high so first reveal isn't delayed
        bool autoStill;                          // currently judged stopped
        double dotsOpacity = 1.0;                // eased overlay opacity (fades on auto-pause)
        bool startupSuppress;                    // hide dots until first motion (set on Windows auto-start)

        // --- status surfaced to the control panel ---
        public string StatusText = "Starting…";
        public string DebugText = "";            // live pipeline state for the panel's debug readout
        public event Action<string> StatusChanged;
        void SetStatus(string t) { StatusText = t; StatusChanged?.Invoke(t); }

        readonly CueElement cue = new CueElement();   // immediate-mode renderer (shared styles with Android)
        double offX, offY, velX, velY;          // accumulated offset + flow velocity
        double accelEnv;                        // smoothed 0..1 accel envelope (Acceleration-pulse model)
        double lat, fore, yaw;                   // resolved motion
        double fwd1, fwd2 = 1;                    // inertial forward-axis unit estimate (horizontal plane); default (0,1) = old fixed mapping
        double gx, gy, gz; bool gReady;          // gravity estimate
        int oriMode = -1;                        // locked axis mapping (-1 = not yet detected)
        int settleFrames;                        // frames since (re)arm — gates the lock
        int tiltHold;                            // frames left of fast gravity-tracking after a tilt

        // raw sensor values written by sensor threads, read on the UI tick
        volatile bool hasReading;
        double rawX, rawY, rawZ;
        double rawGyroX, rawGyroY, rawYaw;       // gyro deg/s (rawYaw = Z)
        bool hasGyro;

        Accelerometer acc;
        Gyrometer gyro;

        // --- Simulation Mode: a synthetic IMU source that OVERRIDES the real sensor while ON ---
        public bool Simulating;                          // sim source is driving -> laptop ReadingChanged early-returns
        public SimScenario SimScenarioMode = SimScenario.Off;
        readonly MotionSimulator sim = new MotionSimulator();
        DispatcherTimer simTimer;                        // ~16ms tick writing synthetic frames (like FeedPhone)
        long simLastTick;
        public string SimPhase => Simulating ? sim.PhaseName : "";   // active phase name for the panel/debug readout

        // --- phone bridge: while a phone frame is fresh it overrides the laptop sensor ---
        long lastPhoneTick = long.MinValue / 2;
        bool lastPhoneActive;
        public bool PhoneActive => Environment.TickCount64 - lastPhoneTick < 1500;
        public bool PhoneOnly { get; set; }             // Phone mode: ignore the built-in sensor entirely
        public bool HasLaptopSensor => acc != null && !PhoneOnly;   // built-in accelerometer usable as a source

        // --- GPS speed (m/s) from the phone, when its location permission is granted ---
        double phoneSpeed = -1;                  // -1 = unknown/unavailable
        long phoneSpeedTick = long.MinValue / 2;
        bool SpeedFresh => phoneSpeed >= 0 && Environment.TickCount64 - phoneSpeedTick < 5000;

        // --- live gate state surfaced to the control panel's drive-test indicator ---
        public bool AutoHidden => AutoPause && autoStill;            // dots currently hidden by the stopped-gate
        public bool SpeedKnown => SpeedFresh;                        // GPS ground speed is fresh enough to show
        public double SpeedKmh => SpeedFresh ? phoneSpeed * 3.6 : -1;   // km/h, or -1 when unknown
        public bool MotionLive => PhoneActive || HasLaptopSensor;   // a source is actually feeding the gate

        // Called from the phone-server thread; mirrors exactly what the laptop
        // sensor handlers write (accel in m/s² incl. gravity, gyro in deg/s) so the
        // rest of the pipeline — gravity removal, axis auto-map, Flip/Swap — is unchanged.
        // speed is GPS ground speed in m/s, or <0 when the phone can't supply it.
        public void FeedPhone(double ax, double ay, double az, double gx, double gy, double gz, double speed, bool gyroValid)
        {
            rawX = ax; rawY = ay; rawZ = az; hasReading = true;
            if (gyroValid) { rawGyroX = gx; rawGyroY = gy; rawYaw = gz; hasGyro = true; }
            if (speed >= 0) { phoneSpeed = speed; phoneSpeedTick = Environment.TickCount64; }
            lastPhoneTick = Environment.TickCount64;
        }

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

            Content = cue;

            SourceInitialized += (s, e) => MakeClickThrough();
            Loaded += OnLoaded;
            SizeChanged += (s, e) => cue.RebuildField();
        }

        public void ApplySettings(Settings s)
        {
            Sens = double.IsFinite(s.Sens) ? Math.Clamp(s.Sens, 0.3, 6) : 1.8;  // keep model in slider range
            LonGain = double.IsFinite(s.LonGain) ? Math.Clamp(s.LonGain, -4, 4) : 1.5;  // signed accel/brake trim
            InvertX = s.InvertX < 0 ? -1 : 1;
            InvertY = s.InvertY < 0 ? -1 : 1;
            DotScale = double.IsFinite(s.DotScale) ? Math.Clamp(s.DotScale, 0.4, 3.0) : 1.0;
            SwapAxes = s.SwapAxes;
            DotStyle = Math.Clamp(s.DotStyle, 0, 3);
            if (DotStyle == 0 && s.DarkDots) DotStyle = 2;   // migrate the old bool
            AccentColor = s.AccentColor;
            DotOpacity = double.IsFinite(s.DotOpacity) ? Math.Clamp(s.DotOpacity, 0.2, 1.0) : 1.0;
            Density = double.IsFinite(s.Density) ? Math.Clamp(s.Density, 0.5, 2.0) : 1.0;
            Decay = double.IsFinite(s.Decay) ? Math.Clamp(s.Decay, 0.80, 0.97) : 0.94;
            HideSensitivity = double.IsFinite(s.HideSensitivity) ? Math.Clamp(s.HideSensitivity, 0.5, 2.0) : 1.0;
            Placement = Math.Clamp(s.Placement, 0, 1);
            CueStyle = Math.Clamp(s.CueStyle, 0, 5);
            CueModel = Math.Clamp(s.CueModel, 0, 1);
            AutoPause = s.AutoPause;
            PhoneOnly = s.PhoneOnly;
        }

        // Toggle Phone mode at runtime and refresh the status line immediately.
        public void SetPhoneOnly(bool on)
        {
            PhoneOnly = on;
            if (on)
                SetStatus("Phone mode — connect your phone below to stream its motion.");
            else
                SetStatus(acc != null ? "Reading this laptop’s motion sensor."
                                      : "No sensor on this PC — use your phone: open the link in the Phone section below.");
        }

        void MakeClickThrough()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            StartSensors();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (a, b) => Frame();
            timer.Start();
        }

        // rebuild the cue field when Density / Placement change (counts are baked in)
        public void RebuildField() => cue.RebuildField();

        // recolour the dots: 0 light, 1 mixed, 2 dark, 3 accent
        public void SetDotStyle(int style)
        {
            DotStyle = Math.Clamp(style, 0, 3);
            cue.DotStyle = DotStyle; cue.InvalidateVisual();
        }

        // choose a custom accent colour (packed ARGB) and switch to the Accent dot style
        public void SetAccentColor(uint argb)
        {
            AccentColor = argb; DotStyle = 3;
            cue.AccentColor = argb; cue.DotStyle = 3; cue.InvalidateVisual();
        }

        // pick the cue STYLE (Dots/Streaks/Rails/Horizon/Flow/Chevrons) and motion MODEL (flow/pulse)
        public void SetCueStyle(int style) { CueStyle = Math.Clamp(style, 0, 5); cue.CueStyle = CueStyle; cue.InvalidateVisual(); }
        public void SetCueModel(int model) { CueModel = Math.Clamp(model, 0, 1); cue.CueModel = CueModel; }

        void StartSensors()
        {
            acc = Accelerometer.GetDefault();
            if (acc != null)
            {
                acc.ReportInterval = Math.Max(acc.MinimumReportInterval, 16u);
                acc.ReadingChanged += (s, e) =>
                {
                    if (Simulating || PhoneActive || PhoneOnly) return;    // sim/phone is driving (or forced) — ignore the laptop sensor
                    var r = e.Reading;                      // AccelerationX/Y/Z in g, includes gravity
                    rawX = r.AccelerationX * 9.81;
                    rawY = r.AccelerationY * 9.81;
                    rawZ = r.AccelerationZ * 9.81;
                    hasReading = true;
                };
                SetStatus(PhoneOnly
                    ? "Phone mode — connect your phone below to stream its motion."
                    : "Reading this laptop’s motion sensor.");
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
                    if (Simulating || PhoneActive || PhoneOnly) return;   // sim/phone is driving (or forced) — ignore the laptop gyro
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
                double hf = Math.Abs(mag - magSlow);     // deviation from baseline = road buzz / engine vibration
                magSlow += (mag - magSlow) * 0.10;
                jitterEma += (hf - jitterEma) * 0.05;    // smoothed vibration energy (survives constant-speed cruise)
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

            // The two horizontal (non-gravity) device-axis residuals. Which one is travel-forward
            // vs sideways is NOT knowable from gravity alone, so don't guess by mount — learn it.
            double h1, h2;
            if (mode == 0) { h1 = dx; h2 = dy; }                   // device lying flat (z = gravity)
            else if (mode == 1) { h1 = dx; h2 = dz; }              // upright landscape (y = gravity)
            else { h1 = dy; h2 = dz; }                             // upright portrait (x = gravity)

            // Inertial forward-axis estimate in the horizontal plane: track the dominant
            // straight-line accel direction as "forward", so gas/brake is never pinned to a guessed
            // device axis. The old fixed `nfore = h2` silently sent longitudinal into the lateral
            // channel on most mounts (the laptop analogue of the phone's forward=North bug) — that's
            // why turns worked but accel/brake produced no vertical flow. Default fwd=(0,1) exactly
            // reproduces the old mapping until it converges, so this is a safe superset. Turns are
            // excluded via yawRate (deg/s) so cornering can't pull the axis sideways.
            double hmag = Math.Sqrt(h1 * h1 + h2 * h2);
            if (hmag > 0.20 && Math.Abs(yawRate) < 8.0)
            {
                double d1 = h1 / hmag, d2 = h2 / hmag;
                if (d1 * fwd1 + d2 * fwd2 < 0) { d1 = -d1; d2 = -d2; }    // gas & brake share the line
                fwd1 += (d1 - fwd1) * 0.02; fwd2 += (d2 - fwd2) * 0.02;   // ~multi-second EMA
                double fn = Math.Sqrt(fwd1 * fwd1 + fwd2 * fwd2);
                if (fn > 1e-3) { fwd1 /= fn; fwd2 /= fn; }
            }
            double nfore = h1 * fwd1 + h2 * fwd2;                  // signed projection onto estimated forward
            double nlat = h1 * fwd2 - h2 * fwd1;                   // perpendicular = lateral

            if (SwapAxes) (nlat, nfore) = (nfore, nlat);           // manual override if polarity reads wrong

            lat += (nlat - lat) * 0.25;              // a touch more smoothing -> road wiggle doesn't reach the dots
            fore += (nfore - fore) * 0.25;
            yaw += (yawRate - yaw) * 0.3;            // yaw about gravity, not raw device-Z
        }

        // Soft dead-zone: ramps up from 0 at the threshold instead of jumping, so a signal that
        // hovers near the knee (vehicle wiggling at a light) can't chatter the dots on/off.
        static double SoftDead(double v, double dz)
        {
            double a = Math.Abs(v);
            return a <= dz ? 0 : Math.Sign(v) * (a - dz);
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
            double aX = SoftDead(rawAX, dz) * InvertX;   // turns  -> horizontal
            // Longitudinal (gas/brake) axis: LonGain is the signed accel/brake trim — its SIGN sets
            // direction and its magnitude sets sensitivity (mirrors Android's lonGain). It COMPOSES
            // with InvertY so the "Flip vertical" toggle still reverses the whole Y axis as before.
            double aY = SoftDead(fore, dz) * InvertY * LonGain;    // gas/brake -> vertical (signed trim)

            // smoothed accel envelope for the Acceleration-pulse cue model (band-passed mag, never raw -> no strobe)
            double cueMag = Math.Sqrt(lat * lat + fore * fore);
            accelEnv += (Math.Clamp(cueMag / 3.0, 0, 1) - accelEnv) * 0.20;

            // --- auto-pause: judge "useful to show" ---
            // GPS speed is authoritative when the phone supplies it: motion sickness
            // needs real travel speed, and nobody feels ill crawling/parked — so hide
            // below ~7 km/h and show above ~12. No guessing about jitter.
            // Raw "should be hidden?" desire, with a WIDE hysteresis band (separate show/hide knees)
            // so a single lateral bump can't cross both knees in one frame. Inside the band the current
            // state is held; the actual toggle is gated by the min-dwell debounce below.
            bool wantStill = autoStill;
            if (SpeedFresh)
            {
                if (phoneSpeed > 3.6) wantStill = false;         // >~13 km/h -> moving (show)
                else if (phoneSpeed < 1.7) wantStill = true;     // <~6 km/h -> stopped (hide)
                // 6–13 km/h: hold current state (hysteresis)
            }
            else
            {
                // Accel fallback (laptop sensor, or phone without location): a held device
                // has a constant hand-tremor floor, so the bar to count as "moving" has to
                // sit above that floor — otherwise it never hides off a table. Jitter is
                // de-weighted now that GPS does the cruise-detection job it was invented for.
                double energy = jitterEma * 1.5 + Math.Sqrt(lat * lat + fore * fore) + Math.Abs(yaw) * 0.03;
                activityEma += (energy - activityEma) * 0.06;   // smoothing (gives the gate inertia)
                double hs = Math.Clamp(HideSensitivity, 0.5, 2.0);   // >1 biases toward hiding sooner
                if (activityEma > 0.50 * hs) wantStill = false;  // clear maneuver -> wake (wider show knee)
                else if (activityEma < 0.18 * hs) wantStill = true;  // low motion -> hide (wider hide knee)
                // between the knees: hold current state (hysteresis)
            }
            // MIN-DWELL debounce: after any toggle, hold the new visibility state at least minDwell
            // frames before another toggle is allowed; AND require the desired state to persist
            // `confirm` frames before it is honoured. Together these stop small bumps from strobing
            // the dots on/off. (Tunable.)
            const int minDwell = 75;        // ~1.25s lockout after a toggle (60fps)
            const int confirm = 18;         // desired state must hold ~0.3s before flipping
            if (dwellFrames < minDwell) dwellFrames++;   // saturate (only >= minDwell matters) so it can't overflow on long uptime
            if (wantStill != autoStill && dwellFrames >= minDwell)
            {
                if (++stillFrames >= confirm) { autoStill = wantStill; stillFrames = 0; dwellFrames = 0; }
            }
            else stillFrames = 0;
            // Launched by Windows auto-start: keep the dots invisible until the car
            // actually moves (or a manual Recenter), so a desk/boot doesn't flash them.
            if (startupSuppress && !autoStill) startupSuppress = false;   // first real motion reveals them
            bool hide = startupSuppress || (AutoPause && autoStill);
            bool freeze = Paused || hide;

            double decay = Math.Clamp(Decay, 0.80, 0.97);   // how long flow persists (user-tunable)
            const double gain = 0.105;              // accel -> velocity
            const double slew = 0.40;               // max velocity change per frame -> a road jolt can't jump the dots
            if (freeze) { velX *= 0.85; velY *= 0.85; }
            else
            {
                velX = velX * decay + Math.Clamp(-aX * gain * Sens, -slew, slew);
                // forward acceleration drives dots DOWN, braking drives them UP
                // (for the common upright mounting; Flip *if* reversed on your car).
                velY = velY * decay + Math.Clamp(-aY * gain * Sens, -slew, slew);
            }
            const double vmax = 22;
            velX = Math.Clamp(velX, -vmax, vmax);
            velY = Math.Clamp(velY, -vmax, vmax);

            offX += velX;                           // keep streaming while motion lasts
            offY += velY;

            // ease overlay opacity: dots fade away when auto-paused / startup-suppressed, fade back on motion.
            // Asymmetric + slow so a (debounced) gate flip can't snap the layer: fade IN a touch quicker
            // than OUT for a graceful, non-strobing transition. (Tunable.)
            double tgtOpacity = hide ? 0.0 : 1.0;
            double opEase = tgtOpacity > dotsOpacity ? 0.045 : 0.025;
            dotsOpacity += (tgtOpacity - dotsOpacity) * opEase;
            if (Math.Abs(dotsOpacity - Opacity) > 0.001) Opacity = dotsOpacity;

            // hand the current flow state + live params to the renderer and repaint this frame
            cue.OffX = offX; cue.OffY = offY; cue.VelX = velX; cue.VelY = velY; cue.AccelEnv = accelEnv;
            cue.DotScale = DotScale; cue.DotStyle = DotStyle; cue.AccentColor = AccentColor;
            cue.DotOpacity = DotOpacity; cue.Density = Density; cue.Placement = Placement;
            cue.CueStyle = CueStyle; cue.CueModel = CueModel;
            cue.InvalidateVisual();

            DebugText =
                $"src:{(Simulating ? "SIM" : PhoneActive ? "PHONE" : acc != null ? "laptop" : "none")}  mode:{oriMode}  gReady:{(gReady ? 1 : 0)}\n" +
                $"in  a:{rawX,7:0.0}{rawY,7:0.0}{rawZ,7:0.0}   |a|:{Math.Sqrt(rawX * rawX + rawY * rawY + rawZ * rawZ),5:0.0}\n" +
                $"grav :{gx,7:0.0}{gy,7:0.0}{gz,7:0.0}\n" +
                $"gyro :{rawGyroX,7:0.0}{rawGyroY,7:0.0}{rawYaw,7:0.0}\n" +
                $"lat:{lat,7:0.00}  fore:{fore,7:0.00}\n" +
                $"vel:{velX,7:0.0}  {velY,7:0.0}\n" +
                $"act:{activityEma,6:0.000} jit:{jitterEma,5:0.00} spd:{(SpeedFresh ? (phoneSpeed * 3.6).ToString("0.0") + "km/h" : "--")}  {(AutoPause ? (autoStill ? "HIDDEN (stopped)" : "moving") : "autohide off")}" +
                (Simulating ? $"\nSIM phase: {sim.PhaseName}" : "");
        }

        public void Recenter()
        {
            gReady = false;
            oriMode = -1;                           // re-detect mounting orientation
            settleFrames = 0;                       // re-arm the warm-up gate before re-locking
            tiltHold = 0;
            lat = fore = yaw = 0;
            fwd1 = 0; fwd2 = 1;                     // re-learn the forward axis from scratch
            velX = velY = offX = offY = 0;
            startupSuppress = false;                // an explicit recenter means "show me the dots now"
            autoStill = false; stillFrames = 0; activityEma = 0.3; jitterEma = 0.05;   // wake the dots
        }

        // Windows auto-start launch: start with the dots invisible (autoStill=true so the gate
        // only reveals them on real motion), avoiding a flash of dots at a desk boot.
        public void SuppressDotsOnStartup()
        {
            startupSuppress = true;
            autoStill = true;
            dotsOpacity = 0.0;
            Opacity = 0.0;
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
            fwd1 = 0; fwd2 = 1;                     // laptop and phone differ in mount -> re-learn forward
            velX = velY = 0;
            if (!startupSuppress) autoStill = false;                 // a source switch (e.g. phone connect) must NOT
            stillFrames = 0; activityEma = 0.3;                      // reveal dots during startup suppression; wake on real motion
        }

        // --- Simulation Mode: a synthetic IMU source for desk testing ----------------
        // When ON it OVERRIDES the real sensor (the laptop ReadingChanged handlers
        // early-return on Simulating, exactly like PhoneActive/PhoneOnly). The sim
        // timer writes raw accel+gyro+speed every ~16ms just like FeedPhone, so the
        // WHOLE pipeline runs unchanged. Off restores the real sensor and re-arms.
        public void SetSimScenario(SimScenario sc)
        {
            SimScenarioMode = sc;
            if (sc == SimScenario.Off)
            {
                if (!Simulating) return;                 // already off -> nothing to restore (byte-for-byte original)
                simTimer?.Stop();
                Simulating = false;
                sim.Scenario = SimScenario.Off;
                hasReading = false;                      // drop the last synthetic frame so it isn't re-ingested
                if (gyro == null) { hasGyro = false; rawGyroX = rawGyroY = rawYaw = 0; }
                phoneSpeed = -1;                         // stop forcing "moving"
                ArmForNewSource();                       // re-learn gravity/axis map for the real sensor
                SetStatus(PhoneOnly ? "Phone mode — connect your phone below to stream its motion."
                          : acc != null ? "Reading this laptop’s motion sensor."
                          : "No sensor on this PC — use your phone: open the link in the Phone section below.");
            }
            else
            {
                sim.Scenario = sc; sim.Reset();
                Simulating = true;
                ArmForNewSource();                       // re-learn gravity/axis cleanly for the sim source
                if (simTimer == null)
                {
                    simTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                    simTimer.Tick += (s, e) => SimTick();
                }
                simLastTick = Environment.TickCount64;
                simTimer.Start();
                SetStatus("Simulation running — " + sc + ". Set it to Off to use the real sensor.");
            }
        }

        // Synthesize one frame and write it into the raw fields — mirrors FeedPhone
        // (and the laptop ReadingChanged handlers) so the pipeline is identical.
        void SimTick()
        {
            long now = Environment.TickCount64;
            double dt = (now - simLastTick) / 1000.0;
            simLastTick = now;
            if (dt <= 0) dt = 0.016;
            if (dt > 0.10) dt = 0.10;                    // clamp after a stall so the script doesn't jump
            sim.Tick(dt);
            rawX = sim.Ax; rawY = sim.Ay; rawZ = sim.Az;
            rawGyroX = sim.Gx; rawGyroY = sim.Gy; rawYaw = sim.Gz;
            hasGyro = true; hasReading = true;
            phoneSpeed = sim.Speed; phoneSpeedTick = now;   // feed the gate so auto-hide sees "moving"
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
        const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;
        const uint VK_P = 0x50, VK_R = 0x52, VK_V = 0x56, VK_H = 0x48, VK_OEM4 = 0xDB, VK_OEM6 = 0xDD;
        const int HK_PAUSE = 1, HK_RECENTER = 2, HK_DOWN = 3, HK_UP = 4, HK_FLIPV = 5, HK_FLIPH = 6;
        // ordered binding table: action key (matches Settings.Hotkeys), Win32 hotkey id, panel label, repeat-on-hold
        static readonly (string Key, int Id, string Label, bool Repeat)[] HotkeyActions =
        {
            ("Pause",        HK_PAUSE,    "Pause / resume",  false),
            ("Recenter",     HK_RECENTER, "Recenter",        false),
            ("StrengthDown", HK_DOWN,     "Strength −",      true),
            ("StrengthUp",   HK_UP,       "Strength +",      true),
            ("FlipV",        HK_FLIPV,    "Flip vertical",   false),
            ("FlipH",        HK_FLIPH,    "Flip horizontal", false),
        };
        static Dictionary<string, Hotkey> DefaultHotkeys() => new Dictionary<string, Hotkey>
        {
            ["Pause"]        = new Hotkey(MOD_CONTROL | MOD_ALT, VK_P),
            ["Recenter"]     = new Hotkey(MOD_CONTROL | MOD_ALT, VK_R),
            ["StrengthDown"] = new Hotkey(MOD_CONTROL | MOD_ALT, VK_OEM4),
            ["StrengthUp"]   = new Hotkey(MOD_CONTROL | MOD_ALT, VK_OEM6),
            ["FlipV"]        = new Hotkey(MOD_CONTROL | MOD_ALT, VK_V),
            ["FlipH"]        = new Hotkey(MOD_CONTROL | MOD_ALT, VK_H),
        };
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        // live remappable-shortcut state (see Settings.Hotkeys / HotkeyActions)
        Dictionary<string, Hotkey> hotkeys;
        readonly Dictionary<string, Button> hotkeyButtons = new Dictionary<string, Button>();
        string capturingKey;                       // action currently listening for a new combo, or null

        readonly OverlayWindow ov;
        readonly PhoneServer server;
        Slider slider, sizeSlider, lonSlider, opacitySlider, densitySlider, decaySlider, hideSlider;
        StackPanel accentRow;
        CheckBox pause, flipV, flipH, swap, dbg, placementTog;
        TextBlock hotkeyHint, dbgText;
        StackPanel autoHideStatusRow;            // live gate indicator under the auto-hide toggle
        System.Windows.Shapes.Ellipse autoHideDot;
        TextBlock autoHideStatusText, autoHideTip;
        TextBlock phoneState;
        TextBox phoneUrl;
        TextBlock phoneBt;
        Border statusChip;
        System.Windows.Shapes.Ellipse statusChipDot;
        TextBlock statusChipText;
        System.Windows.Controls.Image qrImage;
        UIElement qrPlaceholder, qrConnected;    // shown inside the white frame: no address yet / phone streaming
        string shownUrl;
        System.Windows.Forms.NotifyIcon notify;
        System.Windows.Forms.ContextMenuStrip trayMenu;
        System.Drawing.Icon trayIcon;
        HwndSource hwndSource;
        bool shuttingDown, teardownDone, reallyQuit;
        CheckBox startMin, autoStart, autoPauseTog;
        Grid contentShell;
        Button phoneExpander;
        StackPanel phoneDetail;
        CheckBox phoneOnlyTog;
        bool phoneDetailOpen, phoneExpUserSet, phoneAutoCollapsed;
        DispatcherTimer saveTimer;
        Border helpOverlay;
        bool firstRunDone;
        public bool FirstRunDone => firstRunDone;
        bool autoHideTipSeen;
        public bool AutoHideTipSeen => autoHideTipSeen;
        public bool StartMinimized => startMin?.IsChecked == true;

        public ControlWindow(OverlayWindow ovIn, PhoneServer serverIn, Settings settings)
        {
            ov = ovIn;
            server = serverIn;
            firstRunDone = settings.FirstRunDone;
            autoHideTipSeen = settings.AutoHideTipSeen;
            hotkeys = DefaultHotkeys();                 // start from defaults, then layer any saved bindings on top
            if (settings.Hotkeys != null)
                foreach (var kv in settings.Hotkeys)
                    if (kv.Value != null) hotkeys[kv.Key] = kv.Value;   // honour saved entries incl. an explicit unset (Vk==0 = user removed it)
            Title = "Orbital";
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/orbital.ico", UriKind.Absolute)); } catch { /* icon is cosmetic */ }
            Width = 420; Height = 820;
            MinWidth = 360; MinHeight = 520;
            ResizeMode = ResizeMode.CanResize;
            WindowStyle = WindowStyle.None;                 // custom dark caption bar instead of the OS one
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 28; Top = 28;
            ApplySavedGeometry(settings);                   // restore remembered position + size (clamped on-screen)
            Topmost = false;                                // not always-on-top — other windows can sit above the panel
            Background = Brush("BgBrush");
            Foreground = Brush("TextBrush");
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 36,                         // top strip is draggable
                ResizeBorderThickness = new Thickness(6),   // thin resize grips on every edge
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });

            var root = new StackPanel { Margin = new Thickness(16, 16, 16, 18) };

            // ---- header: wordmark + live status chip ----
            var header = new Grid { Margin = new Thickness(2, 0, 2, 14) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var brandRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            brandRow.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 14, Height = 14, Fill = Brush("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0)
            });
            brandRow.Children.Add(Styled(new TextBlock { Text = "Orbital", VerticalAlignment = VerticalAlignment.Center }, "Wordmark"));
            Grid.SetColumn(brandRow, 0);
            header.Children.Add(brandRow);

            statusChip = MakeChip(out statusChipDot, out statusChipText);
            statusChip.HorizontalAlignment = HorizontalAlignment.Right;
            statusChip.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(statusChip, 1);
            header.Children.Add(statusChip);
            root.Children.Add(header);

            var status = Styled(new TextBlock { Text = ov.StatusText, Margin = new Thickness(2, 0, 2, 14) }, "BodyText");
            root.Children.Add(status);
            ov.StatusChanged += t => Dispatcher.Invoke(() => status.Text = t);

            // ============================================================
            //  Cards are grouped by what the user is trying to do, so each
            //  setting is easy to find: CUE STYLE (shape) → FEEL (response)
            //  → APPEARANCE (looks) → DIRECTION (axes) → COMFORT (auto-hide)
            //  → PHONE SENSOR → STARTUP → SHORTCUTS → DIAGNOSTICS.
            // ============================================================

            // ---- CUE STYLE card (shared set with the Android app) ----
            var cueCard = new StackPanel();
            cueCard.Children.Add(SectionHead("CUE STYLE"));
            var styleNames = new[] { "Dots", "Streaks", "Rails", "Horizon", "Flow", "Chevrons" };
            var styleWrap = new WrapPanel();
            var styleBtns = new Button[styleNames.Length];
            void SelectStyle(int idx)
            {
                ov.SetCueStyle(idx);
                for (int i = 0; i < styleBtns.Length; i++)
                {
                    styleBtns[i].Background = i == idx ? Brush("AccentBrush") : Brush("CardHiBrush");
                    styleBtns[i].Foreground = i == idx ? Brush("BgBrush") : Brush("TextBrush");
                }
            }
            for (int i = 0; i < styleNames.Length; i++)
            {
                int idx = i;
                var b = Styled(new Button { Content = styleNames[i], Margin = new Thickness(0, 0, 6, 6), MinWidth = 84 }, "OrbitButton");
                b.ToolTip = "Switch the shape of the peripheral motion cue.";
                b.Click += (s, e) => { SelectStyle(idx); QueueSave(); };
                styleBtns[i] = b;
                styleWrap.Children.Add(b);
            }
            cueCard.Children.Add(styleWrap);

            // motion model: velocity-flow (steady) vs acceleration-pulse (brightens on accel/brake)
            cueCard.Children.Add(SubLabel("Motion model"));
            var modelRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var modelNames = new[] { "Flow", "Pulse" };
            var modelBtns = new Button[2];
            void SelectModel(int m)
            {
                ov.SetCueModel(m);
                for (int i = 0; i < modelBtns.Length; i++)
                {
                    modelBtns[i].Background = i == m ? Brush("AccentBrush") : Brush("CardHiBrush");
                    modelBtns[i].Foreground = i == m ? Brush("BgBrush") : Brush("TextBrush");
                }
            }
            for (int i = 0; i < 2; i++)
            {
                int m = i;
                var b = Styled(new Button { Content = modelNames[i] }, "OrbitButton");
                b.ToolTip = m == 0 ? "Steady flow — constant brightness." : "Pulse — cue brightens with acceleration / braking.";
                b.Click += (s, e) => { SelectModel(m); QueueSave(); };
                Grid.SetColumn(b, m * 2);
                modelBtns[i] = b;
                modelRow.Children.Add(b);
            }
            cueCard.Children.Add(modelRow);
            root.Children.Add(CardOf(cueCard));
            SelectStyle(ov.CueStyle);            // reflect persisted style/model in the buttons
            SelectModel(ov.CueModel);

            // ---- FEEL card: how strongly the cue responds ----
            var feel = new StackPanel();
            feel.Children.Add(SectionHead("FEEL"));

            // quick feel presets — these just drive the underlying sliders (Android parity);
            // nothing extra is persisted, the sliders' own handlers do the saving.
            var presetRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            for (int pc = 0; pc < 5; pc++)
                presetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = pc % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(6) });
            void AddPreset(int col, string name, double sens, double lon, double den, double op)
            {
                var b = Styled(new Button { Content = name }, "OrbitButton");
                b.ToolTip = $"Preset: strength {sens:0.0}×, density {den:0.0}×, opacity {(int)(op * 100)}%.";
                b.Click += (s, e) =>
                {
                    slider.Value = sens; lonSlider.Value = lon;
                    densitySlider.Value = den; opacitySlider.Value = op;
                };
                Grid.SetColumn(b, col);
                presetRow.Children.Add(b);
            }
            AddPreset(0, "Calm", 1.0, 1.0, 0.7, 0.6);
            AddPreset(2, "Balanced", 1.8, 1.5, 1.0, 1.0);
            AddPreset(4, "Strong", 3.0, 2.2, 1.4, 1.0);
            feel.Children.Add(presetRow);

            feel.Children.Add(FieldHead("Strength", out var strengthVal));
            slider = Styled(new Slider
            {
                Minimum = 0.3, Maximum = 6, Value = ov.Sens,
                TickFrequency = 0.1, IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 4, 0, 12)
            }, "OrbitSlider");
            slider.ToolTip = "How far the dots drift for a given motion. Higher = stronger cue.";
            slider.ValueChanged += (s, e) => { ov.Sens = e.NewValue; strengthVal.Text = e.NewValue.ToString("0.0") + "×"; QueueSave(); };
            strengthVal.Text = ov.Sens.ToString("0.0") + "×";
            feel.Children.Add(slider);

            // --- Accel / Brake trim (longitudinal): bipolar -4..4, sign = direction, 0 = off (mirrors Android).
            // Sign composes with the "Flip vertical" toggle below; centre is off. ---
            feel.Children.Add(FieldHead("Accel / Brake", out var lonVal));
            lonSlider = Styled(new Slider
            {
                Minimum = -4, Maximum = 4, Value = ov.LonGain,
                TickFrequency = 0.1, IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 4, 0, 12)
            }, "OrbitSlider");
            lonSlider.ToolTip = "Forward/back cue trim. Sign sets direction (accelerate = dots down); centre is off.";
            lonSlider.ValueChanged += (s, e) => { ov.LonGain = e.NewValue; lonVal.Text = Math.Abs(e.NewValue) < 0.05 ? "off" : e.NewValue.ToString("+0.0;-0.0") + "×"; QueueSave(); };
            lonVal.Text = Math.Abs(ov.LonGain) < 0.05 ? "off" : ov.LonGain.ToString("+0.0;-0.0") + "×";
            feel.Children.Add(lonSlider);

            // --- Smoothness / trail (flow decay) — how long the flow persists ---
            feel.Children.Add(FieldHead("Smoothness / trail", out var decayVal));
            decaySlider = Styled(new Slider
            {
                Minimum = 0.80, Maximum = 0.97, Value = ov.Decay,
                TickFrequency = 0.01, IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 4, 0, 0)
            }, "OrbitSlider");
            decaySlider.ToolTip = "How long the dot flow persists. Higher = smoother, longer trails.";
            decaySlider.ValueChanged += (s, e) => { ov.Decay = e.NewValue; decayVal.Text = e.NewValue.ToString("0.00"); QueueSave(); };
            decayVal.Text = ov.Decay.ToString("0.00");
            feel.Children.Add(decaySlider);
            root.Children.Add(CardOf(feel));

            // ---- APPEARANCE card: how the cue looks ----
            var appearance = new StackPanel();
            appearance.Children.Add(SectionHead("APPEARANCE"));

            appearance.Children.Add(FieldHead("Dot size", out var sizeVal));
            sizeSlider = Styled(new Slider
            {
                Minimum = 0.4, Maximum = 3.0, Value = ov.DotScale,
                TickFrequency = 0.1, IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 4, 0, 12)
            }, "OrbitSlider");
            sizeSlider.ToolTip = "Diameter of the drifting dots.";
            sizeSlider.ValueChanged += (s, e) => { ov.DotScale = e.NewValue; sizeVal.Text = e.NewValue.ToString("0.0") + "×"; QueueSave(); };
            sizeVal.Text = ov.DotScale.ToString("0.0") + "×";
            appearance.Children.Add(sizeSlider);

            // --- Opacity: per-dot brightness (composes with the auto-hide fade, never fights it) ---
            appearance.Children.Add(FieldHead("Opacity", out var opacityVal));
            opacitySlider = Styled(new Slider
            {
                Minimum = 0.2, Maximum = 1.0, Value = ov.DotOpacity,
                TickFrequency = 0.05, IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 4, 0, 12)
            }, "OrbitSlider");
            opacitySlider.ToolTip = "How bright the dots are. Lower for a subtler cue.";
            opacitySlider.ValueChanged += (s, e) => { ov.DotOpacity = e.NewValue; opacityVal.Text = ((int)Math.Round(e.NewValue * 100)) + "%"; QueueSave(); };
            opacityVal.Text = ((int)Math.Round(ov.DotOpacity * 100)) + "%";
            appearance.Children.Add(opacitySlider);

            // --- Density: how many dots are drawn (rebuilds the field) ---
            appearance.Children.Add(FieldHead("Density", out var densityVal));
            densitySlider = Styled(new Slider
            {
                Minimum = 0.5, Maximum = 2.0, Value = ov.Density,
                TickFrequency = 0.1, IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 4, 0, 12)
            }, "OrbitSlider");
            densitySlider.ToolTip = "How many dots are drawn.";
            densitySlider.ValueChanged += (s, e) => { ov.Density = e.NewValue; densityVal.Text = e.NewValue.ToString("0.0") + "×"; ov.RebuildField(); QueueSave(); };
            densityVal.Text = ov.Density.ToString("0.0") + "×";
            appearance.Children.Add(densitySlider);

            appearance.Children.Add(Divider());
            appearance.Children.Add(FieldHead("Dot colour", out var dotColourVal));
            string[] dotNames = { "Light", "Mixed", "Dark" };
            var dotStyleSlider = Styled(new Slider
            {
                Minimum = 0, Maximum = 2, Value = ov.DotStyle,
                TickFrequency = 1, IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 4, 0, 2)
            }, "OrbitSlider");
            dotStyleSlider.ToolTip = "Light reads on dark windows, Dark reads on light ones, Mixed works on either.";
            dotStyleSlider.ValueChanged += (s, e) =>
            {
                int v = (int)Math.Round(e.NewValue);
                ov.SetDotStyle(v);
                dotColourVal.Text = dotNames[Math.Clamp(v, 0, 2)];
                QueueSave();
            };
            dotColourVal.Text = dotNames[Math.Clamp(ov.DotStyle, 0, 2)];
            appearance.Children.Add(dotStyleSlider);
            var dotLabels = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            dotLabels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dotLabels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dotLabels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            void DotLab(string t, int col, HorizontalAlignment ha)
            {
                var tb = Styled(new TextBlock { Text = t, HorizontalAlignment = ha }, "FaintText");
                Grid.SetColumn(tb, col);
                dotLabels.Children.Add(tb);
            }
            DotLab("Light", 0, HorizontalAlignment.Left);
            DotLab("Mixed", 1, HorizontalAlignment.Center);
            DotLab("Dark", 2, HorizontalAlignment.Right);
            appearance.Children.Add(dotLabels);

            // Accent colour: a 4th dot style — teal / amber / blue (Android parity).
            // Picking a swatch switches to the Accent style; moving the slider above leaves it.
            appearance.Children.Add(SubLabel("Accent colour"));
            accentRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
            void Swatch(uint argb, string brushKey, string tip)
            {
                var sw = new System.Windows.Shapes.Ellipse
                {
                    Width = 26, Height = 26, Margin = new Thickness(0, 0, 10, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Fill = Brush(brushKey), Stroke = Brush("BorderBrush2"), StrokeThickness = 1,
                    ToolTip = tip
                };
                sw.MouseLeftButtonUp += (s, e) => { ov.SetAccentColor(argb); dotColourVal.Text = "Accent"; QueueSave(); };
                accentRow.Children.Add(sw);
            }
            Swatch(0xFF6FD8C6, "AccentBrush", "Teal");
            Swatch(0xFFE8B66F, "AccentAmberBrush", "Amber");
            Swatch(0xFF6FA8D8, "BlueBrush", "Blue");
            appearance.Children.Add(accentRow);
            if (ov.DotStyle == 3) dotColourVal.Text = "Accent";

            // --- Coverage: side strips vs full peripheral frame ---
            appearance.Children.Add(Divider());
            placementTog = Toggle("Frame the whole screen");
            placementTog.ToolTip = "Add top and bottom dot bands for a full peripheral frame (default: side strips only).";
            placementTog.IsChecked = ov.Placement == 1;
            placementTog.Checked += (s, e) => { ov.Placement = 1; ov.RebuildField(); QueueSave(); };
            placementTog.Unchecked += (s, e) => { ov.Placement = 0; ov.RebuildField(); QueueSave(); };
            appearance.Children.Add(placementTog);

            // --- Reset every visual/motion tunable to its default ---
            var resetVis = Styled(new Button { Content = "Reset visuals to defaults", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0) }, "OrbitButton");
            resetVis.ToolTip = "Restore strength, accel/brake, size, opacity, density, colour, style, smoothness and hide sensitivity to defaults.";
            resetVis.Click += (s, e) =>
            {
                slider.Value = 1.8; lonSlider.Value = 1.5; sizeSlider.Value = 1.0;
                opacitySlider.Value = 1.0; densitySlider.Value = 1.0;
                decaySlider.Value = 0.94; hideSlider.Value = 1.0;
                ov.SetDotStyle(1); dotStyleSlider.Value = 1; dotColourVal.Text = dotNames[1];
                SelectStyle(0); SelectModel(0);     // back to Dots + velocity-flow
                placementTog.IsChecked = false;     // fires Unchecked -> Placement 0 + rebuild
                ov.RebuildField();
                QueueSave();
            };
            appearance.Children.Add(resetVis);
            root.Children.Add(CardOf(appearance));

            // ---- DIRECTION card: fix which way the dots move ----
            var direction = new StackPanel();
            direction.Children.Add(SectionHead("DIRECTION"));
            pause = Toggle("Pause");
            pause.ToolTip = "Freeze the dots (Ctrl+Alt+P).";
            pause.Checked += (s, e) => ov.Paused = true;
            pause.Unchecked += (s, e) => ov.Paused = false;
            direction.Children.Add(pause);
            direction.Children.Add(Divider());
            flipV = Toggle("Flip vertical  ↕");
            flipV.ToolTip = "Reverse up/down dot drift (Ctrl+Alt+V).";
            flipV.Checked += (s, e) => { ov.InvertY = -1; QueueSave(); };
            flipV.Unchecked += (s, e) => { ov.InvertY = 1; QueueSave(); };
            flipV.IsChecked = ov.InvertY == -1;     // reflect persisted settings
            direction.Children.Add(flipV);
            direction.Children.Add(Divider());
            flipH = Toggle("Flip horizontal  ↔");
            flipH.ToolTip = "Reverse left/right dot drift (Ctrl+Alt+H).";
            flipH.Checked += (s, e) => { ov.InvertX = -1; QueueSave(); };
            flipH.Unchecked += (s, e) => { ov.InvertX = 1; QueueSave(); };
            flipH.IsChecked = ov.InvertX == -1;
            direction.Children.Add(flipH);
            direction.Children.Add(Divider());
            swap = Toggle("Swap ↕↔");
            swap.ToolTip = "Exchange the vertical and horizontal axes if the dots move the wrong way.";
            swap.Checked += (s, e) => { ov.SwapAxes = true; QueueSave(); };
            swap.Unchecked += (s, e) => { ov.SwapAxes = false; QueueSave(); };
            swap.IsChecked = ov.SwapAxes;            // reflect persisted setting
            direction.Children.Add(swap);
            direction.Children.Add(Styled(new TextBlock
            {
                Text = "Turn on Swap if gas/brake moves the dots sideways.",
                Margin = new Thickness(0, 2, 0, 0)
            }, "FaintText"));
            root.Children.Add(CardOf(direction));

            // ---- action buttons ----
            var row2 = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var recenter = Styled(new Button { Content = "Recenter" }, "OrbitButtonPrimary");
            recenter.ToolTip = "Reset the dots to centre — use after settling into your seat (Ctrl+Alt+R).";
            recenter.Click += (s, e) => ov.Recenter();
            Grid.SetColumn(recenter, 0);
            var quit = Styled(new Button { Content = "Quit" }, "OrbitButton");
            quit.ToolTip = "Exit Orbital completely.";
            quit.Click += (s, e) => QuitApp();
            Grid.SetColumn(quit, 2);
            row2.Children.Add(recenter);
            row2.Children.Add(quit);
            root.Children.Add(row2);

            // ---- COMFORT card: auto-hide when stopped ----
            var comfort = new StackPanel();
            comfort.Children.Add(SectionHead("COMFORT"));
            autoPauseTog = Toggle("Auto-hide dots when stopped");
            autoPauseTog.ToolTip = "Fade the dots away when the vehicle is parked, bring them back on motion.";
            autoPauseTog.IsChecked = ov.AutoPause;
            autoPauseTog.Checked += (s, e) =>
            {
                ov.AutoPause = true;
                if (!autoHideTipSeen)
                {
                    autoHideTipSeen = true;
                    if (autoHideTip != null) autoHideTip.Visibility = Visibility.Visible;
                }
                if (autoHideStatusRow != null) autoHideStatusRow.Visibility = Visibility.Visible;
                UpdateAutoHideStatus();
                QueueSave();
            };
            autoPauseTog.Unchecked += (s, e) =>
            {
                ov.AutoPause = false;
                if (autoHideStatusRow != null) autoHideStatusRow.Visibility = Visibility.Collapsed;
                if (autoHideTip != null) autoHideTip.Visibility = Visibility.Collapsed;
                QueueSave();
            };
            comfort.Children.Add(autoPauseTog);

            // live drive-test indicator (plain-language gate state), only visible while auto-hide is on
            autoHideDot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Brush("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
            autoHideStatusText = new TextBlock { Text = "Waiting for motion…", FontSize = 11, Foreground = Brush("MutedBrush"), VerticalAlignment = VerticalAlignment.Center };
            autoHideStatusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 6, 0, 0) };
            autoHideStatusRow.Children.Add(autoHideDot);
            autoHideStatusRow.Children.Add(autoHideStatusText);
            autoHideStatusRow.Visibility = ov.AutoPause ? Visibility.Visible : Visibility.Collapsed;
            comfort.Children.Add(autoHideStatusRow);

            autoHideTip = Styled(new TextBlock
            {
                Text = "Watch this on your first drive — it should hide when you stop and reappear as you pull away.",
                Margin = new Thickness(2, 4, 0, 0),
                Visibility = (ov.AutoPause && !autoHideTipSeen) ? Visibility.Visible : Visibility.Collapsed
            }, "FaintText");
            comfort.Children.Add(autoHideTip);
            // Auto-hide already on from a prior session: the Checked handler never fired for the
            // construction-time IsChecked assignment, so consume the one-time flag here (else the
            // tip nags on every launch).
            if (ov.AutoPause && !autoHideTipSeen) { autoHideTipSeen = true; QueueSave(); }

            // --- Hide sensitivity (auto-hide knee scale) ---
            comfort.Children.Add(FieldHead("Hide sensitivity", out var hideVal));
            hideSlider = Styled(new Slider
            {
                Minimum = 0.5, Maximum = 2.0, Value = ov.HideSensitivity,
                TickFrequency = 0.1, IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 4, 0, 0)
            }, "OrbitSlider");
            hideSlider.ToolTip = "How eagerly the dots hide when you stop. Higher = hide sooner.";
            hideSlider.ValueChanged += (s, e) => { ov.HideSensitivity = e.NewValue; hideVal.Text = e.NewValue.ToString("0.0") + "×"; QueueSave(); };
            hideVal.Text = ov.HideSensitivity.ToString("0.0") + "×";
            comfort.Children.Add(hideSlider);
            root.Children.Add(CardOf(comfort));

            // ---- PHONE SENSOR card ----
            var phone = new StackPanel();
            phone.Children.Add(SectionHead("PHONE SENSOR"));
            phoneState = Styled(new TextBlock { Text = "Starting server…", Margin = new Thickness(0, 0, 0, 8) }, "BodyText");
            phone.Children.Add(phoneState);

            // Phone mode — ignore the laptop's own sensor and use the phone as the only source.
            phoneOnlyTog = Toggle("Phone mode (use phone only)");
            phoneOnlyTog.ToolTip = "Ignore this laptop's built-in sensor and drive the dots from your phone only.";
            phoneOnlyTog.IsChecked = ov.PhoneOnly;
            phoneOnlyTog.Margin = new Thickness(0, 0, 0, 4);
            phoneOnlyTog.Checked += (s, e) =>
            {
                ov.SetPhoneOnly(true);
                phoneDetailOpen = true; phoneExpUserSet = true; SetPhoneExpander();
                QueueSave(); UpdatePhone();
            };
            phoneOnlyTog.Unchecked += (s, e) => { ov.SetPhoneOnly(false); QueueSave(); UpdatePhone(); };
            phone.Children.Add(phoneOnlyTog);

            // The setup detail is tall and optional, so it collapses behind an expander
            // when the laptop's own sensor is already driving the dots.
            phoneDetail = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            phoneExpander = Styled(new Button { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(11, 6, 11, 6) }, "OrbitButton");
            phoneExpander.ToolTip = "Show or hide the phone pairing details.";
            phoneDetailOpen = true;                         // re-evaluated by UpdatePhone once the sensor source settles
            phoneExpander.Click += (s, e) => { phoneDetailOpen = !phoneDetailOpen; phoneExpUserSet = true; SetPhoneExpander(); };
            phone.Children.Add(phoneExpander);
            phone.Children.Add(phoneDetail);

            // Bluetooth path (no network needed) — labelled row + a "Recommended" pill
            var btRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            btRow.Children.Add(SubLabel("Bluetooth"));
            btRow.Children.Add(Pill("Recommended"));
            phoneDetail.Children.Add(btRow);
            phoneBt = Styled(new TextBlock { Margin = new Thickness(0, 0, 0, 12) }, "BodyText");
            phoneDetail.Children.Add(phoneBt);

            phoneDetail.Children.Add(Divider());

            // Wi-Fi path — labelled row, copyable address, QR
            phoneDetail.Children.Add(SubLabel("Wi-Fi"));
            phoneUrl = Styled(new TextBox { Margin = new Thickness(0, 4, 0, 10) }, "OrbitReadout");
            phoneDetail.Children.Add(phoneUrl);
            var qrFrame = new Border
            {
                Background = Brushes.White, CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };
            // The white frame stacks three mutually-exclusive layers, all centered:
            // the QR itself, a "connecting / no network" placeholder, and a "connected" affirmation.
            var qrStack = new Grid { Width = 184, Height = 184 };
            qrImage = new System.Windows.Controls.Image
            {
                Width = 184, Height = 184,
                Stretch = Stretch.Fill, SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(qrImage, BitmapScalingMode.NearestNeighbor);
            qrStack.Children.Add(qrImage);
            qrStack.Children.Add(qrPlaceholder = BuildQrPlaceholder());
            qrStack.Children.Add(qrConnected = BuildQrConnected());
            qrFrame.Child = qrStack;
            phoneDetail.Children.Add(qrFrame);
            phoneDetail.Children.Add(Styled(new TextBlock
            {
                Text = "Scan the QR to open the Orbital Phone app with the address pre-filled, then pick Bluetooth or Wi-Fi in it. Install the app once."
            }, "FaintText"));
            SetPhoneExpander();
            root.Children.Add(CardOf(phone));

            // ---- STARTUP card ----
            var startup = new StackPanel();
            startup.Children.Add(SectionHead("STARTUP"));
            startMin = Toggle("Start minimized to tray");
            startMin.IsChecked = settings.StartMinimized;
            startMin.Checked += (s, e) => QueueSave();
            startMin.Unchecked += (s, e) => QueueSave();
            startup.Children.Add(startMin);
            startup.Children.Add(Divider());
            autoStart = Toggle("Start with Windows");
            autoStart.IsChecked = AutoStart.IsEnabled();
            autoStart.Checked += (s, e) => AutoStart.Set(true);
            autoStart.Unchecked += (s, e) => AutoStart.Set(false);
            startup.Children.Add(autoStart);
            root.Children.Add(CardOf(startup));

            // ---- SHORTCUTS card: remappable global hotkeys ----
            var shortcuts = new StackPanel();
            shortcuts.Children.Add(SectionHead("SHORTCUTS"));
            shortcuts.Children.Add(Styled(new TextBlock
            {
                Text = "Global shortcuts work anywhere. Click a binding, then press the new key combo (Esc cancels).",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            }, "FaintText"));
            bool firstHkRow = true;
            foreach (var a in HotkeyActions)
            {
                if (!firstHkRow) shortcuts.Children.Add(Divider());
                firstHkRow = false;
                var hkRow = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                hkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                hkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                hkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var hkLbl = SubLabel(a.Label);
                hkLbl.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(hkLbl, 0);
                hkRow.Children.Add(hkLbl);
                var hkBtn = BuildHotkeyButton(a.Key);
                Grid.SetColumn(hkBtn, 1);
                hkRow.Children.Add(hkBtn);
                var hkClear = BuildHotkeyClear(a.Key);
                Grid.SetColumn(hkClear, 2);
                hkRow.Children.Add(hkClear);
                shortcuts.Children.Add(hkRow);
            }
            var hkReset = Styled(new Button { Content = "Reset shortcuts to defaults", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0) }, "OrbitButton");
            hkReset.Click += (s, e) => { hotkeys = DefaultHotkeys(); RefreshHotkeyButtons(); ReapplyHotKeys(); QueueSave(); };
            shortcuts.Children.Add(hkReset);
            root.Children.Add(CardOf(shortcuts));

            // ---- SIMULATION card: synthetic motion for desk testing (dev/test aid) ----
            var simCard = new StackPanel();
            simCard.Children.Add(SectionHead("SIMULATION (test)"));
            simCard.Children.Add(Styled(new TextBlock
            {
                Text = "Fake the motion so you can check the cue reacts — no driving needed. Pick a scenario; All loops through every one. Default Off.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            }, "FaintText"));
            var simScenarios = new[]
            {
                SimScenario.Off, SimScenario.All, SimScenario.Accelerate, SimScenario.Brake,
                SimScenario.TurnLeft, SimScenario.TurnRight, SimScenario.Uphill, SimScenario.Downhill, SimScenario.Sideways
            };
            var simLabels = new[] { "Off", "All", "Accel", "Brake", "Left", "Right", "Uphill", "Downhill", "Sideways" };
            var simWrap = new WrapPanel();
            var simBtns = new Button[simScenarios.Length];
            void HighlightSim(int idx)
            {
                for (int i = 0; i < simBtns.Length; i++)
                {
                    simBtns[i].Background = i == idx ? Brush("AccentBrush") : Brush("CardHiBrush");
                    simBtns[i].Foreground = i == idx ? Brush("BgBrush") : Brush("TextBrush");
                }
            }
            for (int i = 0; i < simScenarios.Length; i++)
            {
                int idx = i;
                var b = Styled(new Button { Content = simLabels[i], Margin = new Thickness(0, 0, 6, 6), MinWidth = 76 }, "OrbitButton");
                b.ToolTip = idx == 0 ? "Stop the simulation and use the real sensor."
                          : "Feed synthetic " + simLabels[idx] + " motion into the cue pipeline.";
                b.Click += (s, e) => { HighlightSim(idx); ov.SetSimScenario(simScenarios[idx]); };
                simBtns[i] = b;
                simWrap.Children.Add(b);
            }
            simCard.Children.Add(simWrap);
            var simPhaseLbl = Styled(new TextBlock { Text = "Off", Margin = new Thickness(2, 6, 0, 0) }, "FaintText");
            simCard.Children.Add(simPhaseLbl);
            root.Children.Add(CardOf(simCard));
            HighlightSim(0);                                 // default Off at launch — does NOT start the sim (off = original behavior)
            var simPhaseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            simPhaseTimer.Tick += (s, e) => simPhaseLbl.Text = ov.Simulating ? "Active phase: " + ov.SimPhase : "Off";
            simPhaseTimer.Start();

            // ---- DIAGNOSTICS card ----
            var debug = new StackPanel();
            debug.Children.Add(SectionHead("DIAGNOSTICS"));
            dbg = Toggle("Debug readout");
            dbg.Margin = new Thickness(0, 0, 0, 0);
            debug.Children.Add(dbg);
            dbgText = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = Brush("AccentBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };
            dbg.Checked += (s, e) => dbgText.Visibility = Visibility.Visible;
            dbg.Unchecked += (s, e) => dbgText.Visibility = Visibility.Collapsed;
            debug.Children.Add(dbgText);
            root.Children.Add(CardOf(debug));
            var dbgTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            dbgTimer.Tick += (s, e) =>
            {
                if (dbg.IsChecked == true) dbgText.Text = ov.DebugText;
                if (ov.AutoPause) UpdateAutoHideStatus();
            };
            dbgTimer.Start();

            // The full shortcut list lives in the About flyout now; build it here so
            // RegisterHotKeys can still append any "key in use" warnings to it.
            hotkeyHint = Styled(new TextBlock
            {
                Text = "Hotkeys (work anywhere)\n" +
                       "Ctrl+Alt+P  pause / resume\n" +
                       "Ctrl+Alt+R  recenter\n" +
                       "Ctrl+Alt+[ / ]  strength − / +\n" +
                       "Ctrl+Alt+V / H  flip ↕ / ↔\n" +
                       "Minimize → taskbar · Close [X] → tray",
                Margin = new Thickness(0, 0, 0, 0)
            }, "FaintText");

            var aboutLink = Styled(new Button { Content = "Keyboard shortcuts & about", HorizontalAlignment = HorizontalAlignment.Center }, "OrbitButton");
            aboutLink.ToolTip = "How Orbital works, shortcuts, and version.";
            aboutLink.Click += (s, e) => ShowHelp(true);
            aboutLink.Margin = new Thickness(0, 2, 0, 0);
            root.Children.Add(aboutLink);

            var scroller = new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var shell = new Grid { Opacity = 0 };           // faded in on load
            shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var caption = BuildCaption();
            Grid.SetRow(caption, 0);
            Grid.SetRow(scroller, 1);
            shell.Children.Add(caption);
            shell.Children.Add(scroller);

            helpOverlay = BuildHelpOverlay();               // modal About/help, hidden until opened
            Panel.SetZIndex(helpOverlay, 50);
            Grid.SetRow(helpOverlay, 0);
            Grid.SetRowSpan(helpOverlay, 2);
            shell.Children.Add(helpOverlay);

            Content = shell;
            contentShell = shell;

            if (!firstRunDone) ShowWelcome();               // first-launch onboarding card

            // refresh URL/QR/connection state (IPs can appear after launch via USB tether)
            var phoneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            phoneTimer.Tick += (s, e) => UpdatePhone();
            phoneTimer.Start();
            if (server != null) server.StateChanged += () => Dispatcher.Invoke(UpdatePhone);
            UpdatePhone();

            SetupTray();

            // Persist settings on change (debounced) — not only on a clean Quit, so closing
            // to the tray or a later shutdown can't lose the latest tweaks.
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); Settings.Save(ov, this); };
            LocationChanged += (s, e) => QueueSave();
            SizeChanged += (s, e) => QueueSave();

            SourceInitialized += (s, e) => RegisterHotKeys();
            // minimize (—) goes to the taskbar like a normal window; [X] hides to the tray
            Loaded += (s, e) => contentShell?.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
                { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
            Closing += (s, e) => { if (!reallyQuit) { e.Cancel = true; Settings.Save(ov, this); Hide(); } };   // [X] -> tray (flush settings first)
            Dispatcher.ShutdownStarted += (s, e) => Teardown();                               // belt-and-suspenders
            if (Application.Current != null)
                Application.Current.SessionEnding += (s, e) => reallyQuit = true;             // let Windows log off / shut down
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Teardown();                      // clear tray even on abnormal exit
            Closed += OnClosed;
        }

        // restore remembered window position+size, clamped so it can't open off-screen
        void ApplySavedGeometry(Settings s)
        {
            if (s?.WinWidth is not double w || s.WinHeight is not double h) return;
            w = Math.Max(MinWidth, w); h = Math.Max(MinHeight, h);
            var va = SystemParameters.VirtualScreenWidth > 0
                ? new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                           SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
                : new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            double l = s.WinLeft ?? Left, t = s.WinTop ?? Top;
            l = Math.Min(Math.Max(l, va.Left), va.Right - Math.Min(w, va.Width));   // keep the title bar reachable
            t = Math.Min(Math.Max(t, va.Top), va.Bottom - Math.Min(h, va.Height));
            Width = w; Height = h; Left = l; Top = t;
        }

        // --- styling helpers (resolve Theme.xaml resources; no-op if it failed to load) ---
        static Brush Brush(string key)
        {
            var b = Application.Current?.TryFindResource(key) as Brush;
            return b ?? Brushes.Gray;
        }
        static T Styled<T>(T el, string key) where T : FrameworkElement
        {
            if (Application.Current?.TryFindResource(key) is Style st) el.Style = st;
            return el;
        }
        static Border CardOf(UIElement child)
        {
            var card = new Border { Child = child };
            if (Application.Current?.TryFindResource("Card") is Style st) card.Style = st;
            else { card.Background = Brush("CardBrush"); card.CornerRadius = new CornerRadius(12); card.Padding = new Thickness(14); card.Margin = new Thickness(0, 0, 0, 12); }
            return card;
        }
        static CheckBox Toggle(string label)
        {
            var c = new CheckBox { Content = label };
            return Styled(c, "OrbitToggle");
        }
        // section title with a teal accent marker (consistent, font-glyph-free)
        static StackPanel SectionHead(string text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            sp.Children.Add(new Border
            {
                Width = 3, Height = 12, CornerRadius = new CornerRadius(2),
                Background = Brush("AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0)
            });
            var t = Styled(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }, "SectionHeader");
            t.Margin = new Thickness(0);
            sp.Children.Add(t);
            return sp;
        }

        // a "Label …………… value" header row above a slider; value updates live
        static Grid FieldHead(string label, out TextBlock val)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var l = Styled(new TextBlock { Text = label }, "FieldLabel");
            l.Margin = new Thickness(0);
            Grid.SetColumn(l, 0);
            val = new TextBlock
            {
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = Brush("AccentBrush"), VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetColumn(val, 1);
            g.Children.Add(l);
            g.Children.Add(val);
            return g;
        }
        static Border Divider() => new Border
        {
            Height = 1, Background = Brush("BorderBrush2"),
            Margin = new Thickness(0, 2, 0, 2), Opacity = 0.6
        };
        static TextBlock SubLabel(string t) => new TextBlock
        {
            Text = t, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextBrush"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 8, 0)
        };
        static Border Pill(string t) => new Border
        {
            Background = Brush("CardHiBrush"),
            BorderBrush = Brush("AccentBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 1, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            Child = new TextBlock { Text = t, FontSize = 10, Foreground = Brush("AccentBrush") }
        };
        static Border MakeChip(out System.Windows.Shapes.Ellipse dot, out TextBlock text)
        {
            dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Brush("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
            text = new TextBlock { Text = "Starting…", FontSize = 11, Foreground = Brush("MutedBrush"), VerticalAlignment = VerticalAlignment.Center };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(dot);
            sp.Children.Add(text);
            return new Border
            {
                Background = Brush("CardBrush"),
                BorderBrush = Brush("BorderBrush2"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(10, 4, 11, 4),
                Child = sp
            };
        }
        // debounce a settings write; many of these fire rapidly (slider drag, window move)
        void QueueSave()
        {
            if (saveTimer == null) return;
            saveTimer.Stop();
            saveTimer.Start();
        }
        void SetPhoneExpander()
        {
            if (phoneExpander == null) return;
            phoneDetail.Visibility = phoneDetailOpen ? Visibility.Visible : Visibility.Collapsed;
            phoneExpander.Content = phoneDetailOpen ? "Hide phone setup  ▴" : "Set up phone  ▾";
        }

        static string AppVersion()
        {
            try { var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version; return v == null ? "1.0" : $"{v.Major}.{v.Minor}"; }
            catch { return "1.0"; }
        }

        // dark scrim wrapping a centred card; clicking the scrim (not the card) closes it
        static Border ModalScrim(UIElement card, Action onClose)
        {
            var scrim = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x06, 0x09, 0x0F)),
                Visibility = Visibility.Collapsed
            };
            scrim.MouseDown += (s, e) => onClose?.Invoke();
            if (card is FrameworkElement fe) fe.MouseDown += (s, e) => e.Handled = true;   // swallow clicks on the card
            scrim.Child = card;
            return scrim;
        }

        Border BuildHelpOverlay()
        {
            var col = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            titleRow.Children.Add(new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = Brush("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            titleRow.Children.Add(Styled(new TextBlock { Text = "Orbital", VerticalAlignment = VerticalAlignment.Center }, "Wordmark"));
            col.Children.Add(titleRow);
            col.Children.Add(Styled(new TextBlock { Text = "Version " + AppVersion() + " · early access", Margin = new Thickness(0, 0, 0, 12) }, "FaintText"));

            col.Children.Add(Styled(new TextBlock
            {
                Text = "Orbital eases motion sickness by drifting faint cue dots along your screen edges, matching the motion your inner ear feels — so your eyes and your balance agree.",
                Margin = new Thickness(0, 0, 0, 12)
            }, "BodyText"));

            col.Children.Add(SectionHead("GETTING STARTED"));
            col.Children.Add(Styled(new TextBlock
            {
                Text = "1.  Settle into your seat, then press Recenter.\n" +
                       "2.  Tune Strength until the dots feel right — not distracting.\n" +
                       "3.  For stronger or remote motion, pair a phone in Phone Sensor.",
                Margin = new Thickness(0, 0, 0, 14)
            }, "BodyText"));

            col.Children.Add(SectionHead("KEYBOARD SHORTCUTS"));
            col.Children.Add(hotkeyHint);                   // built earlier; conflict warnings append here

            var done = Styled(new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 16, 0, 0) }, "OrbitButtonPrimary");
            done.Click += (s, e) => ShowHelp(false);
            col.Children.Add(done);

            var card = new Border
            {
                Background = Brush("CardBrush"),
                BorderBrush = Brush("BorderBrush2"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18),
                MaxWidth = 360,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20),
                Child = new ScrollViewer { Content = col, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 640 }
            };
            return ModalScrim(card, () => ShowHelp(false));
        }

        void ShowHelp(bool on)
        {
            if (helpOverlay == null) return;
            helpOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on)
                helpOverlay.BeginAnimation(OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        }

        // first-launch onboarding — a one-time welcome card over the panel
        void ShowWelcome()
        {
            var col = new StackPanel();
            col.Children.Add(Styled(new TextBlock { Text = "Welcome to Orbital", Margin = new Thickness(0, 0, 0, 8) }, "Wordmark"));
            col.Children.Add(Styled(new TextBlock
            {
                Text = "Orbital drifts faint dots along your screen edges to match the motion your body feels, easing car and motion sickness while you work.",
                Margin = new Thickness(0, 0, 0, 10)
            }, "BodyText"));
            col.Children.Add(Styled(new TextBlock
            {
                Text = "It's already reading motion. Press Recenter once you're settled, then tune Strength to taste. A phone can stream stronger motion — see Phone Sensor.",
                Margin = new Thickness(0, 0, 0, 16)
            }, "BodyText"));

            Border scrim = null;
            var go = Styled(new Button { Content = "Get started", HorizontalAlignment = HorizontalAlignment.Stretch }, "OrbitButtonPrimary");
            go.Click += (s, e) =>
            {
                firstRunDone = true;
                QueueSave();
                if (scrim != null) contentShell.Children.Remove(scrim);
            };
            col.Children.Add(go);

            var card = new Border
            {
                Background = Brush("CardBrush"),
                BorderBrush = Brush("BorderBrush2"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18),
                MaxWidth = 340,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20),
                Child = col
            };
            scrim = ModalScrim(card, null);                 // no click-out; must press Get started
            scrim.Visibility = Visibility.Visible;
            Grid.SetRow(scrim, 0);
            Grid.SetRowSpan(scrim, 2);
            contentShell.Children.Add(scrim);
        }
        void SetChip(string label, Brush color)
        {
            if (statusChipText == null) return;
            statusChipText.Text = label;
            statusChipText.Foreground = color;
            statusChipDot.Fill = color;
        }

        // live drive-test label: plain-language gate state + colored dot + speed when GPS is fresh
        void UpdateAutoHideStatus()
        {
            if (autoHideStatusText == null) return;
            if (!ov.MotionLive)
            {
                autoHideDot.Fill = Brush("MutedBrush");
                autoHideStatusText.Foreground = Brush("MutedBrush");
                autoHideStatusText.Text = "Waiting for motion…";
                return;
            }
            string speed = ov.SpeedKnown ? "  ·  " + ov.SpeedKmh.ToString("0") + " km/h" : "";
            if (ov.AutoHidden)
            {
                autoHideDot.Fill = Brush("MutedBrush");
                autoHideStatusText.Foreground = Brush("MutedBrush");
                autoHideStatusText.Text = "Stopped — dots hidden" + speed;
            }
            else
            {
                autoHideDot.Fill = Brush("AccentBrush");
                autoHideStatusText.Foreground = Brush("TextBrush");
                autoHideStatusText.Text = "Moving — dots showing" + speed;
            }
        }

        // --- custom dark title bar (replaces the OS caption) ---
        FrameworkElement BuildCaption()
        {
            var bar = new Grid { Height = 36, Background = Brush("BgBrush") };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            left.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = Brush("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            left.Children.Add(new TextBlock { Text = "Orbital", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("MutedBrush"), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(left, 0);
            bar.Children.Add(left);

            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            var help = CaptionBtn(0xE897, "CaptionButton");     // (?) help glyph
            help.ToolTip = "About & keyboard shortcuts";
            help.Click += (s, e) => ShowHelp(true);
            btns.Children.Add(help);
            var min = CaptionBtn(0xE921, "CaptionButton");
            min.Click += (s, e) => WindowState = WindowState.Minimized;
            var close = CaptionBtn(0xE8BB, "CaptionClose");
            close.Click += (s, e) => Close();              // Closing handler routes to tray
            btns.Children.Add(min);
            btns.Children.Add(close);
            Grid.SetColumn(btns, 1);
            bar.Children.Add(btns);
            return bar;
        }

        Button CaptionBtn(int glyph, string styleKey)
        {
            var b = Styled(new Button
            {
                Width = 46,
                Height = 36,
                Content = new TextBlock { Text = char.ConvertFromUtf32(glyph), FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10 }
            }, styleKey);
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(b, true);
            return b;
        }

        void QuitApp()
        {
            reallyQuit = true;
            Application.Current.Shutdown();
        }

        // --- global hotkeys (remappable; bindings live in `hotkeys`, persisted via Settings.Hotkeys) ---
        public Dictionary<string, Hotkey> HotkeyConfig => hotkeys;

        void RegisterHotKeys()
        {
            var h = new WindowInteropHelper(this).Handle;
            if (hwndSource == null)                         // hook once; re-applies reuse it
            {
                hwndSource = HwndSource.FromHwnd(h);
                hwndSource?.AddHook(WndProc);
            }
            var fails = new List<string>();
            foreach (var a in HotkeyActions)
            {
                if (!hotkeys.TryGetValue(a.Key, out var hk) || hk == null || hk.Vk == 0) continue;
                uint mod = hk.Mods | (a.Repeat ? 0u : MOD_NOREPEAT);   // [ / ] repeat on hold; the rest fire once
                if (!RegisterHotKey(h, a.Id, mod, hk.Vk)) fails.Add(ComboText(hk));
            }
            RefreshHotkeyHint(fails);
        }

        // unregister the old set, register the current `hotkeys` map (call after a remap)
        void ReapplyHotKeys()
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;                  // hwnd not created yet — first Register will pick up the new map
            foreach (var a in HotkeyActions) UnregisterHotKey(h, a.Id);
            RegisterHotKeys();
        }

        // rebuild the About-flyout shortcut list from the live bindings (+ any "in use" warnings)
        void RefreshHotkeyHint(List<string> fails)
        {
            if (hotkeyHint == null) return;
            var sb = new StringBuilder("Hotkeys (work anywhere)\n");
            foreach (var a in HotkeyActions)
                sb.Append(ComboText(hotkeys.TryGetValue(a.Key, out var hk) ? hk : null))
                  .Append("  ").Append(a.Label).Append('\n');
            sb.Append("Minimize → taskbar · Close [X] → tray");
            if (fails != null && fails.Count > 0)
                sb.Append("\n⚠ ").Append(string.Join(" / ", fails)).Append(" in use by another app");
            hotkeyHint.Text = sb.ToString();
        }

        void RefreshHotkeyButtons()
        {
            foreach (var kv in hotkeyButtons)
                kv.Value.Content = ComboText(hotkeys.TryGetValue(kv.Key, out var hk) ? hk : null);
        }

        // one capture-button per action: click to listen, then the next combo is captured + re-registered
        Button BuildHotkeyButton(string actionKey)
        {
            var btn = Styled(new Button { MinWidth = 134, HorizontalAlignment = HorizontalAlignment.Right }, "OrbitButton");
            btn.Content = ComboText(hotkeys.TryGetValue(actionKey, out var hk0) ? hk0 : null);
            btn.ToolTip = "Click, then press the key combo you want. Esc cancels.";
            btn.Click += (s, e) =>
            {
                capturingKey = actionKey;
                // release the live global hotkeys so the next keystroke reaches PreviewKeyDown instead
                // of being swallowed by RegisterHotKey — otherwise you can't rebind onto an in-use combo.
                var h = new WindowInteropHelper(this).Handle;
                if (h != IntPtr.Zero) foreach (var a in HotkeyActions) UnregisterHotKey(h, a.Id);
                btn.Content = "Press keys… (Esc)";
                Keyboard.Focus(btn);
            };
            btn.LostFocus += (s, e) =>
            {
                if (capturingKey != actionKey) return;
                capturingKey = null;
                btn.Content = ComboText(hotkeys.TryGetValue(actionKey, out var hk) ? hk : null);
                ReapplyHotKeys();      // re-register the global hotkeys released when capture began
            };
            btn.PreviewKeyDown += (s, e) =>
            {
                if (capturingKey != actionKey) return;
                e.Handled = true;
                var key = (e.Key == Key.System) ? e.SystemKey : e.Key;   // Alt combos report Key.System
                if (key == Key.Escape)
                {
                    capturingKey = null;
                    btn.Content = ComboText(hotkeys.TryGetValue(actionKey, out var hkc) ? hkc : null);
                    ReapplyHotKeys();  // re-register the global hotkeys released when capture began
                    return;
                }
                // ignore bare modifier presses — wait for a real key with the chord still held
                if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                    key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin ||
                    key == Key.System || key == Key.None) return;
                uint mods = 0;
                var m = Keyboard.Modifiers;
                if ((m & ModifierKeys.Control) != 0) mods |= MOD_CONTROL;
                if ((m & ModifierKeys.Alt) != 0) mods |= MOD_ALT;
                if ((m & ModifierKeys.Shift) != 0) mods |= MOD_SHIFT;
                if ((m & ModifierKeys.Windows) != 0) mods |= MOD_WIN;
                // require a modifier for ordinary keys so capture can't register a bare letter as a
                // global hotkey and hijack normal typing system-wide (F-keys may stand alone).
                bool isFunctionKey = key >= Key.F1 && key <= Key.F24;
                if (mods == 0 && !isFunctionKey) { btn.Content = "Add Ctrl/Alt/Shift… (Esc)"; return; }
                capturingKey = null;
                hotkeys[actionKey] = new Hotkey(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
                btn.Content = ComboText(hotkeys[actionKey]);
                ReapplyHotKeys();      // swap the live registration to the new combo immediately
                QueueSave();
            };
            hotkeyButtons[actionKey] = btn;
            return btn;
        }

        // small "✕" next to each binding: clears it (unbinds the shortcut). Re-add by capturing a new combo.
        Button BuildHotkeyClear(string actionKey)
        {
            var btn = Styled(new Button { Content = "✕", MinWidth = 34, Margin = new Thickness(6, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right }, "OrbitButton");
            btn.ToolTip = "Remove this shortcut (leave it unbound).";
            btn.Click += (s, e) =>
            {
                hotkeys[actionKey] = new Hotkey(0, 0);     // Vk==0 -> unregistered + shows "Unset"
                if (hotkeyButtons.TryGetValue(actionKey, out var capBtn)) capBtn.Content = ComboText(null);
                ReapplyHotKeys();                          // drop the live registration immediately
                QueueSave();
            };
            return btn;
        }

        // "Ctrl+Alt+P"-style label for a binding
        static string ComboText(Hotkey hk)
        {
            if (hk == null || hk.Vk == 0) return "Unset";
            var parts = new List<string>();
            if ((hk.Mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((hk.Mods & MOD_ALT) != 0) parts.Add("Alt");
            if ((hk.Mods & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((hk.Mods & MOD_WIN) != 0) parts.Add("Win");
            parts.Add(KeyName(hk.Vk));
            return string.Join("+", parts);
        }

        static string KeyName(uint vk)
        {
            switch (vk)
            {
                case VK_OEM4: return "[";
                case VK_OEM6: return "]";
            }
            var k = KeyInterop.KeyFromVirtualKey((int)vk);
            return k == Key.None ? ("0x" + vk.ToString("X2")) : k.ToString();
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
                phoneState.Foreground = Brush("DangerBrush");
                SetChip("Error", Brush("DangerBrush"));
                // show the placeholder instead of a blank white QR box (this branch returns
                // before the layer-visibility block below ever runs)
                if (qrConnected != null) qrConnected.Visibility = Visibility.Collapsed;
                if (qrImage != null) qrImage.Visibility = Visibility.Collapsed;
                if (qrPlaceholder != null) qrPlaceholder.Visibility = Visibility.Visible;
                return;
            }
            if (server.Connected || ov.PhoneActive)          // WS client OR fresh UDP frames
            {
                phoneState.Text = "Phone connected ✓ — streaming its motion.";
                phoneState.Foreground = Brush("AccentBrush");
                SetChip("Phone sensor", Brush("AccentBrush"));
            }
            else if (ov.PhoneOnly)                           // Phone mode, no phone connected yet
            {
                phoneState.Text = "Phone mode — waiting for your phone. Connect it below.";
                phoneState.Foreground = Brush("MutedBrush");
                SetChip("Phone mode", Brush("MutedBrush"));
            }
            else if (ov.HasLaptopSensor)                     // laptop's own accelerometer is feeding motion
            {
                phoneState.Text = "Using this laptop's built-in sensor. Connect a phone below for stronger or remote motion.";
                phoneState.Foreground = Brush("MutedBrush");
                SetChip("Laptop sensor", Brush("AccentBrush"));
                if (!phoneAutoCollapsed && !phoneExpUserSet)   // sensor settled after launch — fold the optional setup away once
                {
                    phoneAutoCollapsed = true;
                    phoneDetailOpen = false;
                    SetPhoneExpander();
                }
            }
            else if (server.Urls.Count > 0)
            {
                phoneState.Text = "No sensor on this PC — connect a phone to stream motion.";
                phoneState.Foreground = Brush("MutedBrush");
                SetChip("Waiting", Brush("MutedBrush"));
            }
            else
            {
                phoneState.Text = "No sensor on this PC — connect a phone (Bluetooth below, or a hotspot/tether for WiFi).";
                phoneState.Foreground = Brush("MutedBrush");
                SetChip("Waiting", Brush("MutedBrush"));
            }

            phoneBt.Text = server.BtListening
                ? $"Pair this PC (\"{server.BtName}\") in your phone's Bluetooth settings, then pick Bluetooth in the app. No network needed."
                : "Starting… " + (string.IsNullOrEmpty(server.BtError) ? "" : server.BtError);
            string appLink = string.IsNullOrEmpty(server.PrimaryIp) ? ""
                : $"orbital://connect?host={server.PrimaryIp}&port={server.Port}";
            phoneUrl.Text = string.IsNullOrEmpty(server.PrimaryIp) ? "Waiting for a network address…"
                : $"{server.PrimaryIp} : {server.Port}   ·   same Wi-Fi, hotspot, or USB tether\nBrowser fallback: {server.PrimaryUrl}  (shows a 'not secure' warning → Advanced ▸ proceed)";
            if (appLink != shownUrl)               // re-render the QR only when the target changes
            {
                shownUrl = appLink;
                qrImage.Source = LoadPng(string.IsNullOrEmpty(appLink) ? null : PhoneServer.QrPng(appLink));
            }

            // Pick which layer of the white frame shows: connected affirmation > no-address
            // placeholder > the live QR. Connected wins so a Bluetooth-only phone (no Wi-Fi IP)
            // still shows the green check instead of "Waiting for network".
            bool phoneConnected = server.Connected || ov.PhoneActive;
            bool noAddress = string.IsNullOrEmpty(appLink);
            if (qrConnected != null)
                qrConnected.Visibility = phoneConnected ? Visibility.Visible : Visibility.Collapsed;
            if (qrPlaceholder != null)
                qrPlaceholder.Visibility = (!phoneConnected && noAddress) ? Visibility.Visible : Visibility.Collapsed;
            if (qrImage != null)
                qrImage.Visibility = (!phoneConnected && !noAddress) ? Visibility.Visible : Visibility.Collapsed;
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

        // Centered "waiting for a network address" placeholder shown inside the white QR frame
        // before any Wi-Fi address exists — a dashed teal ring around an Orbital dot reads as
        // "getting ready" rather than a blank/garbage QR. Dark-on-white (frame is white).
        UIElement BuildQrPlaceholder()
        {
            var col = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            var ringHost = new Grid { Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Center };
            ringHost.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 64, Height = 64,
                Stroke = Brush("AccentBrush"), StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            });
            ringHost.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 14, Height = 14, Fill = Brush("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            col.Children.Add(ringHost);
            col.Children.Add(new TextBlock
            {
                Text = "Waiting for network…",
                Foreground = Brush("BgBrush"),
                FontWeight = FontWeights.SemiBold, FontSize = 13,
                Margin = new Thickness(0, 14, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center
            });
            col.Children.Add(new TextBlock
            {
                Text = "A QR appears once Wi-Fi,\nhotspot or tether is up.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x74, 0x84)),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center
            });
            return col;
        }

        // Centered "phone connected" affirmation that collapses the QR once a phone is streaming.
        UIElement BuildQrConnected()
        {
            var green = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
            var col = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            var ringHost = new Grid { Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Center };
            ringHost.Children.Add(new System.Windows.Shapes.Ellipse { Width = 64, Height = 64, Stroke = green, StrokeThickness = 3 });
            ringHost.Children.Add(new TextBlock
            {
                Text = "✓", Foreground = green, FontSize = 34, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            col.Children.Add(ringHost);
            col.Children.Add(new TextBlock
            {
                Text = "Phone connected",
                Foreground = Brush("BgBrush"),
                FontWeight = FontWeights.SemiBold, FontSize = 13,
                Margin = new Thickness(0, 14, 0, 2),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            col.Children.Add(new TextBlock
            {
                Text = "Streaming its motion.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x74, 0x84)),
                FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center
            });
            return col;
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
                Text = "Orbital",
                ContextMenuStrip = trayMenu
            };
            notify.DoubleClick += (s, e) => TogglePanel();
        }

        static System.Drawing.Icon BuildTrayIcon()
        {
            try   // prefer the real multi-res brand icon for crisp tray rendering
            {
                var s = Application.GetResourceStream(new Uri("pack://application:,,,/orbital.ico", UriKind.Absolute))?.Stream;
                if (s != null) using (s) return new System.Drawing.Icon(s, 32, 32);
            }
            catch { /* fall back to the drawn icon below */ }

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
        readonly Action<double, double, double, double, double, double, double, bool> onFrame;
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
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Orbital");

        public PhoneServer(Action<double, double, double, double, double, double, double, bool> onFrame)
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
                    var c = new X509Certificate2(System.IO.File.ReadAllBytes(pfx), "orbit",
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
            var req = new CertificateRequest("CN=Orbital Overlay", rsa,
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
            var bytes = made.Export(X509ContentType.Pfx, "orbit");
            try
            {
                Directory.CreateDirectory(Dir);
                System.IO.File.WriteAllBytes(pfx, bytes);
                System.IO.File.WriteAllText(ipsFile, want);
            }
            catch { /* best effort cache */ }
            // re-import from PFX so SChannel can use the key (ephemeral keys can't auth a server)
            return new X509Certificate2(bytes, "orbit", X509KeyStorageFlags.Exportable);
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
                    Arguments = $"advfirewall firewall add rule name=\"Orbital Phone {proto} {Port}\" " +
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
                btListener = new BluetoothListener(BtUuid) { ServiceName = "OrbitalPhone" };
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
        static void Parse(string json, Action<double, double, double, double, double, double, double, bool> onFrame)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                double G(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                bool gValid = r.TryGetProperty("g", out var gv) && gv.ValueKind == JsonValueKind.Number && gv.GetDouble() != 0;
                double spd = r.TryGetProperty("spd", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetDouble() : -1;
                onFrame(G("ax"), G("ay"), G("az"), G("gx"), G("gy"), G("gz"), spd, gValid);
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
<title>Orbital - phone sensor</title>
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
  /* on-phone cue dots: full-screen canvas BEHIND the readout (passenger view / motion check) */
  #dots{position:fixed;inset:0;width:100%;height:100%;z-index:0;display:none;background:#10151e;
    touch-action:none;pointer-events:none}
  .wrap{position:relative;z-index:1}
  /* mode selector: where the cue dots are drawn -- on the laptop (phone = sensor) or on this phone */
  .seglabel{font-size:13px;color:#8a94a6;margin-top:20px}
  #modeseg{display:inline-flex;margin-top:8px;border:1px solid #2a3343;border-radius:11px;overflow:hidden}
  .seg{font-size:14px;padding:11px 18px;background:#1a2230;color:#8a94a6;border:none;font-weight:600}
  .seg.on{background:#6fd8c6;color:#06231e}
  .seghint{font-size:12px;color:#6a7486;margin-top:8px;max-width:320px}
</style>
</head>
<body>
<canvas id="dots"></canvas>
<div class="wrap">
  <h1>ORBITAL</h1>
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
    <div class="seglabel">Show the cue dots on</div>
    <div id="modeseg">
      <button id="modeLaptop" class="seg on">The laptop</button>
      <button id="modePhone" class="seg">This phone</button>
    </div>
    <div class="seghint" id="seghint">Laptop mode: this phone is the sensor — dots appear on the laptop. Phone mode: dots appear here, over whatever you're doing on the phone.</div>
  </div>
</div>
<script>
(function(){
  var ws=null, wsReady=false, lastSend=0, frames=0, lastHz=0, wake=null, curSpd=-1;
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
      try{ ws.send(JSON.stringify({ax:ax,ay:ay,az:az,gx:gx,gy:gy,gz:gz,spd:curSpd,g:gValid,grav:useIG?1:0})); }catch(e){}
    }
    if(now-lastHz>=400){
      var hz=Math.round(frames*1000/(now-lastHz)); frames=0; lastHz=now;
      var mag=Math.sqrt(ax*ax+ay*ay+az*az);
      var label=useIG?'accel+g':(al&&al.x!=null?'accel(lin)':'NO ACCEL');
      hzEl.textContent=hz+' Hz   |a| '+mag.toFixed(1)+(haveAccel?'':'   (no accelerometer!)');
      outEl.textContent=label+' '+ax.toFixed(1)+' '+ay.toFixed(1)+' '+az.toFixed(1)+
        '   gyro '+gx.toFixed(0)+' '+gy.toFixed(0)+' '+gz.toFixed(0);
    }
    if(dotsOn) feedDots(ax,ay);   // local on-phone cue dots (cheap; off by default)
  }
  async function reqWake(){
    try{ if('wakeLock' in navigator){ wake=await navigator.wakeLock.request('screen'); } }catch(e){}
  }
  function startGps(){
    // GPS ground speed lets the laptop hide the dots at low speed (no sickness when crawling/parked).
    // Optional: if permission is denied or unavailable, curSpd stays -1 and the PC falls back to motion energy.
    try{
      if(!('geolocation' in navigator)) return;
      navigator.geolocation.watchPosition(
        function(p){ curSpd=(p.coords && p.coords.speed!=null && p.coords.speed>=0)?p.coords.speed:-1; },
        function(e){ curSpd=-1; },
        { enableHighAccuracy:true, maximumAge:1000, timeout:10000 });
    }catch(e){}
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
    startGps();
    connect();
  }
  document.getElementById('startbtn').addEventListener('click', start);
  document.addEventListener('visibilitychange', function(){
    if(document.visibilityState==='visible') reqWake();
  });

  // ---- optional on-phone cue dots (passenger view / visual motion check) -----------------
  // Self-contained: driven by the SAME devicemotion onMotion() already reads. Off by default,
  // and streaming to the PC continues regardless. Cheap: requestAnimationFrame + ~28 dots.
  var dotsOn=false, dotsRAF=0, dCanvas=null, dCtx=null, dW=0, dH=0, dDpr=1;
  // high-pass state (strips the slow gravity component so only real motion drifts the dots)
  var lpX=0, lpY=0, velX=0, velY=0, offX=0, offY=0, haveLp=false;
  var DOTS=28;
  function feedDots(ax,ay){            // ax=lateral, ay=fore/aft (m/s^2, gravity-inclusive)
    if(!haveLp){ lpX=ax; lpY=ay; haveLp=true; }
    lpX+=(ax-lpX)*0.04; lpY+=(ay-lpY)*0.04;   // slow low-pass ~= estimated gravity bias
    var hx=ax-lpX, hy=ay-lpY;                  // high-pass: motion only
    // mirror the PC pipeline feel: vel = vel*decay - a*gain ; off += vel
    velX=velX*0.94 - hx*0.10;
    velY=velY*0.94 - hy*0.10;
    var vmax=22; if(velX>vmax)velX=vmax; else if(velX<-vmax)velX=-vmax;
    if(velY>vmax)velY=vmax; else if(velY<-vmax)velY=-vmax;
    offX+=velX; offY+=velY;
  }
  function sizeDots(){
    dDpr=Math.min(window.devicePixelRatio||1,2);
    dW=window.innerWidth; dH=window.innerHeight;
    dCanvas.width=Math.round(dW*dDpr); dCanvas.height=Math.round(dH*dDpr);
    dCtx.setTransform(dDpr,0,0,dDpr,0,0);
  }
  function drawDots(){
    if(!dotsOn) return;
    dotsRAF=requestAnimationFrame(drawDots);
    dCtx.clearRect(0,0,dW,dH);
    var span=dH+120, spacing=span/DOTS;
    var driftY=((offY*0.6)%spacing+spacing)%spacing;   // wrap fore/aft drift -> dots roll down/up
    var driftX=offX*0.5;                                // lateral lean shifts the columns
    var lx=18+ (driftX>22?22:(driftX<-22?-22:driftX));
    var rx=dW-18 + (driftX>22?22:(driftX<-22?-22:driftX));
    for(var i=0;i<DOTS;i++){
      var y=(i*spacing+driftY)-60;
      var t=i/DOTS, r=3.2+2.6*Math.sin(t*Math.PI);     // fatter in the middle of the run
      dCtx.fillStyle='rgba(111,216,198,0.85)';
      dCtx.beginPath(); dCtx.arc(lx,y,r,0,6.2832); dCtx.fill();
      dCtx.beginPath(); dCtx.arc(rx,y,r,0,6.2832); dCtx.fill();
    }
  }
  function setDots(on){            // on = Phone mode (draw dots on this phone)
    dotsOn=on;
    if(on){
      if(!dCanvas){ dCanvas=document.getElementById('dots'); dCtx=dCanvas.getContext('2d'); }
      dCanvas.style.display='block'; sizeDots();
      if(!dotsRAF) dotsRAF=requestAnimationFrame(drawDots);
    }else{
      if(dCanvas) dCanvas.style.display='none';
      if(dotsRAF){ cancelAnimationFrame(dotsRAF); dotsRAF=0; }
    }
  }
  // Mode = where the dots are drawn. Laptop: phone stays the sensor, PC shows the dots.
  // Phone: dots render here over the phone. Streaming to the PC continues either way.
  function setMode(phone){
    setDots(phone);
    document.getElementById('modePhone').classList.toggle('on',phone);
    document.getElementById('modeLaptop').classList.toggle('on',!phone);
  }
  document.getElementById('modeLaptop').addEventListener('click', function(){ setMode(false); });
  document.getElementById('modePhone').addEventListener('click', function(){ setMode(true); });
  window.addEventListener('resize', function(){ if(dotsOn) sizeDots(); });
})();
</script>
</body>
</html>
""";
    }
}
