using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

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

    public string Query(string mode)
    {
        return $"mode={Uri.EscapeDataString(mode)}"
             + $"&preset={Uri.EscapeDataString(Preset)}"
             + $"&quality={Uri.EscapeDataString(Quality)}"
             + $"&bloom={(Bloom ? "1" : "0")}"
             + $"&attract={(Attract ? "1" : "0")}";
    }
}

static class AppPaths
{
    public static string Root { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InkContainer");

    public static string WebRoot { get; } = System.IO.Path.Combine(Root, "web");

    public static string EnsureHtml()
    {
        Directory.CreateDirectory(WebRoot);
        var dest = System.IO.Path.Combine(WebRoot, "fluid.html");
        using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("fluid.html")
            ?? throw new InvalidOperationException("Missing embedded fluid.html");
        using var dst = File.Create(dest);
        src.CopyTo(dst);
        return dest;
    }
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
        try { AppPaths.EnsureHtml(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Ink Container", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
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

    static (HostMode mode, IntPtr hwnd) Parse(string[] args)
    {
        if (args.Length == 0) return (HostMode.App, IntPtr.Zero);
        var joined = string.Join(" ", args).Trim();
        var a0 = args[0].ToLowerInvariant();

        if (a0 is "--obs" or "/obs" or "--source") return (HostMode.Obs, IntPtr.Zero);
        if (a0 is "--config" or "/config") return (HostMode.Config, IntPtr.Zero);

        if (a0.StartsWith("/c") || a0.StartsWith("-c"))
            return (HostMode.Config, TryHwnd(a0, args));
        if (a0.StartsWith("/p") || a0.StartsWith("-p"))
            return (HostMode.Preview, TryHwnd(a0, args));
        if (a0.StartsWith("/s") || a0.StartsWith("-s"))
            return (HostMode.Screensaver, IntPtr.Zero);

        if (joined.Contains("/c", StringComparison.OrdinalIgnoreCase))
            return (HostMode.Config, IntPtr.Zero);
        if (joined.Contains("/s", StringComparison.OrdinalIgnoreCase))
            return (HostMode.Screensaver, IntPtr.Zero);

        return (HostMode.App, IntPtr.Zero);
    }

    static IntPtr TryHwnd(string a0, string[] args)
    {
        var colon = a0.IndexOf(':');
        if (colon > 0 && long.TryParse(a0[(colon + 1)..], out var h1))
            return new IntPtr(h1);
        if (args.Length > 1 && long.TryParse(args[1].Trim(':'), out var h2))
            return new IntPtr(h2);
        return IntPtr.Zero;
    }
}

sealed class FluidForm : Form
{
    readonly HostMode _mode;
    readonly Settings _settings;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    Point _origin;
    bool _armed;
    int _cursorHidden;

    public FluidForm(HostMode mode, Settings settings, IntPtr previewHwnd, Screen screen)
    {
        _mode = mode;
        _settings = settings;
        Text = "Ink Container";
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = mode is HostMode.App or HostMode.Obs;
        KeyPreview = true;
        Controls.Add(_web);

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
            StartPosition = FormStartPosition.Manual;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(800, 500);
        }

        Load += async (_, _) => await InitWeb();
        FormClosed += (_, _) =>
        {
            EnergyHub.Message -= OnEnergyMessage;
            while (_cursorHidden > 0) { Cursor.Show(); _cursorHidden--; }
            if (mode == HostMode.Screensaver)
                Application.Exit();
        };
    }

    static bool IsEnergyKey(Keys k) =>
        k is Keys.OemOpenBrackets or Keys.OemCloseBrackets or Keys.OemMinus or Keys.Oemplus
            or Keys.Add or Keys.Subtract or Keys.Oemcomma or Keys.OemPeriod
            or Keys.PageUp or Keys.PageDown;

    void OnEnergyMessage(string json)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => OnEnergyMessage(json));
            return;
        }
        try { _web.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
    }

    async Task InitWeb()
    {
        var profile = System.IO.Path.Combine(AppPaths.Root, "wv2-" + _mode.ToString().ToLowerInvariant());
        Directory.CreateDirectory(profile);
        var envOptions = new CoreWebView2EnvironmentOptions(
            additionalBrowserArguments:
                "--enable-webgl2 --ignore-gpu-blocklist --disable-background-timer-throttling "
                + "--disable-renderer-backgrounding --disable-backgrounding-occluded-windows "
                + "--disable-features=CalculateNativeWinOcclusion "
                + "--autoplay-policy=no-user-gesture-required");
        var env = await CoreWebView2Environment.CreateAsync(null, profile, envOptions);
        await _web.EnsureCoreWebView2Async(env);

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = _mode == HostMode.App;
        core.Settings.AreDevToolsEnabled = _mode == HostMode.App;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = _mode == HostMode.App;
        core.Settings.IsZoomControlEnabled = false;
        core.SetVirtualHostNameToFolderMapping(
            "ink.local", AppPaths.WebRoot, CoreWebView2HostResourceAccessKind.Allow);

        var pageMode = _mode switch
        {
            HostMode.Screensaver or HostMode.Preview => "screensaver",
            HostMode.Obs => "obs",
            _ => "app"
        };
        EnergyHub.Message += OnEnergyMessage;
        if (_mode is HostMode.Screensaver)
            ArmExit();
        core.Navigate("https://ink.local/fluid.html?" + _settings.Query(pageMode));
    }

    void ArmExit()
    {
        _origin = Cursor.Position;
        _armed = false;
        var t = new System.Windows.Forms.Timer { Interval = 400 };
        t.Tick += (_, _) => { _armed = true; t.Stop(); t.Dispose(); };
        t.Start();

        MouseMove += OnSaverInput;
        KeyDown += (_, e) => { if (!IsEnergyKey(e.KeyCode)) ExitSaver(); };
        _web.PreviewKeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.None && !IsEnergyKey(e.KeyCode)) ExitSaver();
        };
        _web.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            _web.CoreWebView2.WebMessageReceived += (_, msg) =>
            {
                var s = msg.TryGetWebMessageAsString();
                if (s == "exit") ExitSaver();
            };
            _ = _web.CoreWebView2.ExecuteScriptAsync("""
                (() => {
                  const go = () => { try { chrome.webview.postMessage('exit'); } catch(e) {} };
                  const keep = new Set(['[',']','-','=','_','+',',','.','PageUp','PageDown']);
                  let ox = 0, oy = 0, armed = false;
                  setTimeout(() => { armed = true; ox = 0; oy = 0; }, 500);
                  window.addEventListener('mousemove', e => {
                    if (!armed) { ox = e.screenX; oy = e.screenY; return; }
                    if (Math.abs(e.screenX - ox) + Math.abs(e.screenY - oy) > 12) go();
                  }, true);
                  window.addEventListener('keydown', e => {
                    if (!keep.has(e.key)) go();
                  }, true);
                  window.addEventListener('mousedown', go, true);
                  window.addEventListener('pointerdown', go, true);
                })();
                """);
        };
    }

    void OnSaverInput(object? sender, MouseEventArgs e)
    {
        if (!_armed) return;
        var d = Math.Abs(Cursor.Position.X - _origin.X) + Math.Abs(Cursor.Position.Y - _origin.Y);
        if (d > 16) ExitSaver();
    }

    void ExitSaver()
    {
        if (_mode != HostMode.Screensaver) return;
        Application.Exit();
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
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White
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
        var preset = Combo(new[] { "demo", "ink", "cloud", "fire", "fog" }, s.Preset, 18);
        L("Quality", 54);
        var quality = Combo(new[] { "low", "medium", "high", "ultra" }, s.Quality, 52);

        CheckBox Ck(string t, bool on, int y)
        {
            var c = new CheckBox { Text = t, Left = 18, Top = y, Width = 300, Checked = on, ForeColor = Color.FromArgb(210, 210, 204) };
            Controls.Add(c);
            return c;
        }

        var bloom = Ck("Glow / bloom", s.Bloom, 96);
        var attract = Ck("Attract mode (self-stirring)", s.Attract, 124);
        var all = Ck("All monitors", s.AllMonitors, 152);

        var ok = new Button
        {
            Text = "OK", Left = 170, Top = 200, Width = 80, DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(50, 50, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat
        };
        var cancel = new Button
        {
            Text = "Cancel", Left = 256, Top = 200, Width = 80, DialogResult = DialogResult.Cancel,
            BackColor = Color.FromArgb(50, 50, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat
        };
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(ok);
        Controls.Add(cancel);

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
