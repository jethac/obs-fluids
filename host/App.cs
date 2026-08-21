using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace InkContainer;

static class Native
{
    public const int GwlStyle = -16;
    public const int WsChild = 0x40000000;

    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);
    [DllImport("user32.dll")] public static extern int ShowCursor(bool bShow);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
}

sealed class Settings
{
    public string Preset { get; set; } = "demo";
    public string Quality { get; set; } = "high";
    public bool Bloom { get; set; } = true;
    public bool Attract { get; set; } = true;
    public bool AllMonitors { get; set; } = true;

    static string Path => System.IO.Path.Combine(AppPaths.Root, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

static class AppPaths
{
    public static string Root { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkContainer");
}

enum HostMode { App, Screensaver, Preview, Config, Obs }

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Directory.CreateDirectory(AppPaths.Root);
        try
        {
            EnergyHub.Start();

            var (mode, previewHwnd) = Parse(args);
            var settings = Settings.Load();

            switch (mode)
            {
            case HostMode.Config:
                Application.Run(new ConfigForm(settings));
                break;
            case HostMode.Preview:
                Application.Run(new FluidForm(HostMode.Preview, settings, previewHwnd, Screen.PrimaryScreen!));
                break;
            case HostMode.Screensaver:
                {
                    var screens = settings.AllMonitors ? Screen.AllScreens : new[] { Screen.PrimaryScreen! };
                    var forms = screens.Select(s => new FluidForm(HostMode.Screensaver, settings, IntPtr.Zero, s)).ToList();
                    foreach (var f in forms) f.Show();
                    Application.Run();
                }
                break;
            case HostMode.Obs:
                Application.Run(new FluidForm(HostMode.Obs, settings, IntPtr.Zero, Screen.PrimaryScreen!));
                break;
            default:
                Application.Run(new FluidForm(HostMode.App, settings, IntPtr.Zero, Screen.PrimaryScreen!));
                break;
            }
        }
        catch (Exception ex)
        {
            var log = System.IO.Path.Combine(AppPaths.Root, "crash.log");
            File.WriteAllText(log, ex.ToString());
            MessageBox.Show(ex.ToString(), "Ink Container");
        }
    }

    static (HostMode mode, IntPtr hwnd) Parse(string[] args)
    {
        if (args.Length == 0) return (HostMode.App, IntPtr.Zero);
        var joined = string.Join(" ", args).Trim();
        var a0 = args[0].ToLowerInvariant();
        if (a0 is "--obs" or "/obs" or "--source") return (HostMode.Obs, IntPtr.Zero);
        if (a0 is "--config" or "/config") return (HostMode.Config, IntPtr.Zero);
        if (a0.StartsWith("/c") || a0.StartsWith("-c")) return (HostMode.Config, TryHwnd(a0, args));
        if (a0.StartsWith("/p") || a0.StartsWith("-p")) return (HostMode.Preview, TryHwnd(a0, args));
        if (a0.StartsWith("/s") || a0.StartsWith("-s")) return (HostMode.Screensaver, IntPtr.Zero);
        if (joined.Contains("/c", StringComparison.OrdinalIgnoreCase)) return (HostMode.Config, IntPtr.Zero);
        if (joined.Contains("/s", StringComparison.OrdinalIgnoreCase)) return (HostMode.Screensaver, IntPtr.Zero);
        return (HostMode.App, IntPtr.Zero);
    }

    static IntPtr TryHwnd(string a0, string[] args)
    {
        var colon = a0.IndexOf(':');
        if (colon > 0 && long.TryParse(a0[(colon + 1)..], out var h1)) return new IntPtr(h1);
        if (args.Length > 1 && long.TryParse(args[1].Trim(':'), out var h2)) return new IntPtr(h2);
        return IntPtr.Zero;
    }
}

sealed class FluidForm : Form
{
    readonly HostMode _mode;
    readonly Settings _settings;
    readonly Sim _sim = new();
    readonly Gpu _gpu = new();
    readonly Panel _view = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    readonly Panel _side;
    Point _origin;
    bool _armed, _gpuReady, _dragging;
    float _px, _py;
    int _cursorHidden;
    long _lastStamp;
    double _fps = 60;
    Label? _hud;
    System.Windows.Forms.Timer? _tick;

    public FluidForm(HostMode mode, Settings settings, IntPtr previewHwnd, Screen screen)
    {
        _mode = mode;
        _settings = settings;
        _sim.ApplyPreset(settings.Preset);
        _sim.Quality = settings.Quality;
        _sim.Bloom = settings.Bloom;
        _sim.Attract = settings.Attract;
        Text = "Ink Container";
        BackColor = Color.FromArgb(7, 8, 10);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = mode is HostMode.App or HostMode.Obs;
        KeyPreview = true;
        DoubleBuffered = false;

        _side = BuildSide();
        var showUi = mode == HostMode.App;
        if (showUi)
        {
            _side.Dock = DockStyle.Right;
            _side.Width = 280;
            Controls.Add(_view);
            Controls.Add(_side);
        }
        else
        {
            Controls.Add(_view);
        }

        if (mode == HostMode.Preview && previewHwnd != IntPtr.Zero)
        {
            FormBorderStyle = FormBorderStyle.None;
            Native.SetParent(Handle, previewHwnd);
            var style = Native.GetWindowLong(Handle, Native.GwlStyle);
            Native.SetWindowLong(Handle, Native.GwlStyle, style | Native.WsChild);
            Native.GetClientRect(previewHwnd, out var rc);
            Size = new Size(Math.Max(1, rc.Width), Math.Max(1, rc.Height));
            Location = Point.Empty;
        }
        else if (mode == HostMode.Screensaver)
        {
            FormBorderStyle = FormBorderStyle.None;
            Bounds = screen.Bounds;
            TopMost = true;
            Cursor.Hide();
            _cursorHidden++;
        }
        else if (mode == HostMode.Obs)
        {
            FormBorderStyle = FormBorderStyle.None;
            Bounds = new Rectangle(screen.Bounds.X, screen.Bounds.Y, 1920, 1080);
            if (Bounds.Width > screen.Bounds.Width || Bounds.Height > screen.Bounds.Height)
                Bounds = screen.WorkingArea;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(900, 560);
            var wa = screen.WorkingArea;
            Size = new Size(Math.Min(1440, wa.Width), Math.Min(900, wa.Height));
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);
        }

        _view.MouseDown += OnViewDown;
        _view.MouseMove += OnViewMove;
        _view.MouseUp += (_, _) => _dragging = false;
        MouseWheel += (_, e) => _sim.NudgeEnergy((e.Delta > 0 ? 1 : -1) * (ModifierKeys.HasFlag(Keys.Shift) ? 0.35f : 1f), "wheel");

        Load += (_, _) => InitGpu();
        Shown += (_, _) => { if (mode == HostMode.App) WindowState = FormWindowState.Maximized; };
        FormClosed += (_, _) =>
        {
            _tick?.Stop();
            _tick?.Dispose();
            EnergyHub.Message -= OnEnergy;
            _gpu.Dispose();
            while (_cursorHidden > 0) { Cursor.Show(); _cursorHidden--; }
            if (mode == HostMode.Screensaver) Application.Exit();
        };
        KeyDown += OnKey;
        if (mode == HostMode.Screensaver) ArmExit();
    }

    void InitGpu()
    {
        try
        {
            _gpu.Init(_view.Handle, Math.Max(8, _view.ClientSize.Width), Math.Max(8, _view.ClientSize.Height), _sim.Quality);
            _sim.ResetGhosts();
            _sim.Seed();
            _gpuReady = true;
            _lastStamp = Environment.TickCount64;
            EnergyHub.Message += OnEnergy;
            _tick = new System.Windows.Forms.Timer { Interval = 15 };
            _tick.Tick += OnIdle;
            _tick.Start();
            OnIdle(null, EventArgs.Empty);
            _view.Resize += (_, _) =>
            {
                if (!_gpuReady) return;
                _gpu.Resize(Math.Max(8, _view.ClientSize.Width), Math.Max(8, _view.ClientSize.Height), _sim.Quality);
                _sim.Seed();
            };
        }
        catch (Exception ex)
        {
            File.WriteAllText(System.IO.Path.Combine(AppPaths.Root, "crash.log"), ex.ToString());
            MessageBox.Show(ex.ToString(), "Ink Container — D3D11");
        }
    }

    void OnIdle(object? sender, EventArgs e)
    {
        if (!_gpuReady || IsDisposed) return;
        var now = Environment.TickCount64;
        var raw = Math.Min(0.05f, (now - _lastStamp) / 1000f);
        if (raw <= 0) raw = 0.016f;
        _lastStamp = now;
        _fps = _fps * 0.9 + (1.0 / raw) * 0.1;
        var t = now / 1000f;
        if (!_sim.Paused)
            _sim.TickAttract(raw * _sim.Timestep, t);
        _gpu.Frame(_sim, raw * _sim.Timestep, _mode != HostMode.Obs);
        if (_hud != null)
            _hud.Text = $"fluidShape1  {_gpu.VelW}×{_gpu.VelH}  dye {_gpu.DyeW}×{_gpu.DyeH}\n{(int)_fps} fps  ·  ENERGY {(_sim.Energy * 10):0.0}  ·  {_sim.EnergySrc}";
    }

    void OnEnergy(string json)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => OnEnergy(json)); return; }
        _sim.ApplyEnergyJson(json);
        SyncEnergyUi();
    }

    void OnViewDown(object? sender, MouseEventArgs e)
    {
        _dragging = true;
        ToUv(e.Location, out _px, out _py);
        _view.Capture = true;
    }

    void OnViewMove(object? sender, MouseEventArgs e)
    {
        if (_mode == HostMode.Screensaver && _armed)
        {
            var d = Math.Abs(Cursor.Position.X - _origin.X) + Math.Abs(Cursor.Position.Y - _origin.Y);
            if (d > 16) ExitSaver();
            return;
        }
        if (!_dragging) return;
        ToUv(e.Location, out var x, out var y);
        _sim.PointerSplat(x, y, x - _px, y - _py);
        _px = x; _py = y;
    }

    void ToUv(Point pt, out float x, out float y)
    {
        var r = _view.ClientSize;
        x = r.Width <= 0 ? 0.5f : pt.X / (float)r.Width;
        y = r.Height <= 0 ? 0.5f : 1f - pt.Y / (float)r.Height;
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (IsEnergyKey(e.KeyCode))
        {
            var dir = e.KeyCode is Keys.OemCloseBrackets or Keys.Oemplus or Keys.Add or Keys.OemPeriod or Keys.PageUp ? 1 : -1;
            _sim.NudgeEnergy(dir * (e.Shift ? 0.35f : 1f), "key");
            SyncEnergyUi();
            e.Handled = true;
            return;
        }
        if (_mode == HostMode.Screensaver) { ExitSaver(); return; }
        switch (e.KeyCode)
        {
            case Keys.Space:
                _sim.Paused = !_sim.Paused; break;
            case Keys.R:
                _gpu.ClearSim(); _sim.ResetGhosts(); _sim.Seed(); break;
            case Keys.C:
                _gpu.ClearSim(); break;
            case Keys.H:
                if (_side.Parent != null) _side.Visible = !_side.Visible; break;
            case Keys.A:
                _sim.Attract = !_sim.Attract; break;
            case Keys.D1: _sim.Viz = Viz.Dye; break;
            case Keys.D2: _sim.Viz = Viz.Raw; break;
            case Keys.D3: _sim.Viz = Viz.Velocity; break;
            case Keys.D4: _sim.Viz = Viz.Pressure; break;
            case Keys.D5: _sim.Viz = Viz.Temperature; break;
            case Keys.F11:
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                break;
        }
    }

    static bool IsEnergyKey(Keys k) =>
        k is Keys.OemOpenBrackets or Keys.OemCloseBrackets or Keys.OemMinus or Keys.Oemplus
            or Keys.Add or Keys.Subtract or Keys.Oemcomma or Keys.OemPeriod
            or Keys.PageUp or Keys.PageDown;

    void ArmExit()
    {
        _origin = Cursor.Position;
        _armed = false;
        var t = new System.Windows.Forms.Timer { Interval = 400 };
        t.Tick += (_, _) => { _armed = true; t.Stop(); t.Dispose(); };
        t.Start();
    }

    void ExitSaver()
    {
        if (_mode != HostMode.Screensaver) return;
        Application.Exit();
    }

    TrackBar? _energyBar;
    Label? _energyVal;

    Panel BuildSide()
    {
        var p = new Panel
        {
            BackColor = Color.FromArgb(28, 29, 31),
            ForeColor = Color.FromArgb(200, 196, 186),
            Padding = new Padding(10),
            AutoScroll = true
        };
        var y = 8;
        void Head(string t)
        {
            p.Controls.Add(new Label { Text = t, Left = 10, Top = y, Width = 250, ForeColor = Color.FromArgb(196, 165, 116), Font = new Font("Segoe UI", 8, FontStyle.Bold) });
            y += 20;
        }
        Head("fluidShape1");
        _hud = new Label { Left = 10, Top = y, Width = 250, Height = 36, ForeColor = Color.FromArgb(200, 212, 184), Font = new Font("Consolas", 8) };
        p.Controls.Add(_hud);
        y += 42;
        Head("ENERGY");
        _energyBar = new TrackBar { Left = 6, Top = y, Width = 180, Minimum = 0, Maximum = 100, Value = 50, TickStyle = TickStyle.None };
        _energyVal = new Label { Left = 190, Top = y + 8, Width = 60, Text = "5.0", ForeColor = Color.White, Font = new Font("Consolas", 11) };
        _energyBar.Scroll += (_, _) => { _sim.SetEnergy(_energyBar.Value / 100f, "panel"); _energyVal.Text = (_sim.Energy * 10).ToString("0.0", CultureInfo.InvariantCulture); };
        p.Controls.Add(_energyBar);
        p.Controls.Add(_energyVal);
        y += 40;

        Button Btn(string t, Action a)
        {
            var b = new Button { Text = t, Left = 10, Top = y, Width = 80, Height = 24, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(42, 43, 45), ForeColor = Color.White };
            b.FlatAppearance.BorderColor = Color.FromArgb(85, 85, 85);
            b.Click += (_, _) => a();
            p.Controls.Add(b);
            return b;
        }
        Btn("Pause", () => _sim.Paused = !_sim.Paused);
        var r = Btn("Reset", () => { _gpu.ClearSim(); _sim.ResetGhosts(); _sim.Seed(); });
        r.Left = 96;
        var s = Btn("Seed", () => _sim.Seed());
        s.Left = 182;
        y += 32;

        Head("CONTAINER");
        Combo("Preset", ["demo", "ink", "cloud", "fire", "fog"], _sim.Preset, v => { _sim.ApplyPreset(v); _gpu.ClearSim(); _sim.Seed(); }, p, ref y);
        Combo("Quality", ["low", "medium", "high", "ultra"], _sim.Quality, v => { _sim.Quality = v; if (_gpuReady) { _gpu.CreateSim(v); _sim.Seed(); } }, p, ref y);
        Slider("Swirl", 0, 60, _sim.Swirl, v => _sim.Swirl = v, p, ref y);
        Slider("Viscosity", 0, 0.8f, _sim.Viscosity, v => _sim.Viscosity = v, p, ref y);
        Slider("Buoyancy", 0, 4, _sim.Buoyancy, v => _sim.Buoyancy = v, p, ref y);
        Slider("Self Shadow", 0, 3, _sim.Shadow, v => _sim.Shadow = v, p, ref y);
        Slider("Glow", 0, 1.2f, _sim.BloomAmt, v => _sim.BloomAmt = v, p, ref y);
        Combo("View", ["dye", "raw", "velocity", "pressure", "temperature"], "dye", v => _sim.Viz = v switch
        {
            "raw" => Viz.Raw,
            "velocity" => Viz.Velocity,
            "pressure" => Viz.Pressure,
            "temperature" => Viz.Temperature,
            _ => Viz.Dye
        }, p, ref y);
        return p;
    }

    void SyncEnergyUi()
    {
        if (_energyBar == null || _energyVal == null) return;
        _energyBar.Value = (int)Math.Clamp(_sim.Energy * 100, 0, 100);
        _energyVal.Text = (_sim.Energy * 10).ToString("0.0", CultureInfo.InvariantCulture);
    }

    static void Combo(string label, string[] items, string value, Action<string> set, Panel p, ref int y)
    {
        p.Controls.Add(new Label { Text = label, Left = 10, Top = y + 4, Width = 90, ForeColor = Color.FromArgb(140, 140, 134) });
        var c = new ComboBox { Left = 100, Top = y, Width = 150, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(28, 29, 31), ForeColor = Color.White };
        c.Items.AddRange(items);
        c.SelectedItem = items.Contains(value) ? value : items[0];
        c.SelectedIndexChanged += (_, _) => { if (c.SelectedItem is string s) set(s); };
        p.Controls.Add(c);
        y += 28;
    }

    static void Slider(string label, float min, float max, float value, Action<float> set, Panel p, ref int y)
    {
        p.Controls.Add(new Label { Text = label, Left = 10, Top = y + 2, Width = 90, ForeColor = Color.FromArgb(140, 140, 134) });
        var tb = new TrackBar { Left = 96, Top = y - 4, Width = 120, Minimum = 0, Maximum = 100, TickStyle = TickStyle.None };
        var val = new Label { Left = 216, Top = y + 4, Width = 48, Text = value.ToString("0.##"), ForeColor = Color.White, Font = new Font("Consolas", 8) };
        tb.Value = (int)Math.Clamp((value - min) / Math.Max(0.0001f, max - min) * 100, 0, 100);
        tb.Scroll += (_, _) =>
        {
            var v = min + (max - min) * tb.Value / 100f;
            set(v);
            val.Text = v.ToString("0.##");
        };
        p.Controls.Add(tb);
        p.Controls.Add(val);
        y += 28;
    }
}

sealed class ConfigForm : Form
{
    public ConfigForm(Settings s)
    {
        Text = "Ink Container";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 250);
        BackColor = Color.FromArgb(28, 28, 28);
        ForeColor = Color.FromArgb(220, 218, 210);
        Font = new Font("Segoe UI", 9f);

        ComboBox Combo(string[] items, string value, int y)
        {
            var c = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = 150, Top = y, Width = 180,
                BackColor = Color.FromArgb(40, 40, 40), ForeColor = Color.White
            };
            c.Items.AddRange(items);
            c.SelectedIndex = Math.Max(0, Array.IndexOf(items, value));
            Controls.Add(c);
            return c;
        }
        Label L(string t, int y)
        {
            var x = new Label { Text = t, Left = 18, Top = y + 4, Width = 130, ForeColor = Color.FromArgb(180, 180, 176) };
            Controls.Add(x);
            return x;
        }
        L("Preset", 20);
        var preset = Combo(["demo", "ink", "cloud", "fire", "fog"], s.Preset, 18);
        L("Quality", 54);
        var quality = Combo(["low", "medium", "high", "ultra"], s.Quality, 52);
        CheckBox Ck(string t, bool on, int y)
        {
            var c = new CheckBox { Text = t, Left = 18, Top = y, Width = 300, Checked = on, ForeColor = Color.FromArgb(210, 210, 204) };
            Controls.Add(c);
            return c;
        }
        var bloom = Ck("Glow / bloom", s.Bloom, 96);
        var attract = Ck("Attract mode (self-stirring)", s.Attract, 124);
        var all = Ck("All monitors", s.AllMonitors, 152);
        var ok = new Button { Text = "OK", Left = 170, Top = 200, Width = 80, DialogResult = DialogResult.OK, BackColor = Color.FromArgb(50, 50, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", Left = 256, Top = 200, Width = 80, DialogResult = DialogResult.Cancel, BackColor = Color.FromArgb(50, 50, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        AcceptButton = ok; CancelButton = cancel;
        Controls.Add(ok); Controls.Add(cancel);
        ok.Click += (_, _) =>
        {
            s.Preset = preset.SelectedItem?.ToString() ?? "demo";
            s.Quality = quality.SelectedItem?.ToString() ?? "high";
            s.Bloom = bloom.Checked;
            s.Attract = attract.Checked;
            s.AllMonitors = all.Checked;
            s.Save();
        };
    }
}
