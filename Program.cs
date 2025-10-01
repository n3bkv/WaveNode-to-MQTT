using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using MQTTnet;
using MQTTnet.Client;

internal sealed class ForegroundForm : Form
{
    public ForegroundForm()
    {
        Text = "WaveNode → MQTT";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 560; Height = 420;

        var lbl = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = SystemFonts.MessageBoxFont,
            Text = "WaveNode → MQTT bridge is running.\n\nClose this window to exit."
        };
        Controls.Add(lbl);
    }
}

internal static class Program
{
    [STAThread]
private static void Main()
{
    ApplicationConfiguration.Initialize();

    var cfg = AppConfig.LoadOrPrompt();

    using var app = new WaveNodeMqttApp(cfg);
    app.StartAsync().GetAwaiter().GetResult();

    // Show a visible window:
    Application.Run(new ForegroundForm());

    app.Dispose();
}

}

#region Configuration + First-run dialog with defaults/explanations

/// <summary>
/// Centralized defaults and human-readable explanations for common variables.
/// </summary>
internal static class Defaults
{
    public const string MqttHost      = "127.0.0.1"; // Local broker (Mosquitto on the same PC)
    public const int    MqttPort      = 1883;        // Standard MQTT (unencrypted) port
    public const string MqttUser      = "";          // Leave empty if broker allows anonymous access
    public const string MqttPass      = "";          // Leave empty if anonymous or no password
    public const string BaseTopic     = "wavenode";  // Prefix for all topics (ex: wavenode/peak_watts/1)
    public const bool   Retain        = true;        // Retained messages help dashboards get latest on connect
    public const bool   DirectMode    = true;        // Ask WaveNode to post directly to this app window
    public const int    UpdateMode    = 1;           // 0=all samples, 1=only on change, 2=only on request

    // Throttle defaults
    public const int    PublishMinIntervalMs = 200;   // don't republish same topic faster than this
    public const float  PublishMinDelta      = 0.1f;  // require at least this change for floats

    // Short explanations used in UI tooltips/help text:
    public const string ExpMqttHost   = "Hostname or IP of your MQTT broker (e.g., 192.168.50.10).";
    public const string ExpMqttPort   = "MQTT TCP port. 1883 = standard (no TLS), 8883 = MQTT over TLS.";
    public const string ExpMqttUser   = "Leave blank if your broker allows anonymous connections.";
    public const string ExpMqttPass   = "Leave blank if no password is required.";
    public const string ExpBaseTopic  = "Topic prefix under which all metrics are published (e.g., wavenode/peak_watts/1).";
    public const string ExpRetain     = "If true, broker retains the last value for each topic (dashboards see current state immediately).";
    public const string ExpDirect     = "If on, app tries to find WaveNode’s window and request direct messages instead of listening to broadcasts.";
    public const string ExpUpdateMode = "0 = send all samples; 1 = only send when values change; 2 = only send when specifically requested.";

    public const string ExpMinInterval = "Minimum time between publishes for the same topic (milliseconds). Prevents flooding.";
    public const string ExpMinDelta    = "Minimum numeric change required (floats) before republishing. Filters jitter (e.g., 0.1 W).";
}


public sealed class AppConfig
{
    // Persisted settings:
    public string MqttHost { get; set; } = Defaults.MqttHost;
    public int    MqttPort { get; set; } = Defaults.MqttPort;
    public string MqttUser { get; set; } = Defaults.MqttUser;
    public string MqttPass { get; set; } = Defaults.MqttPass;
    public string BaseTopic { get; set; } = Defaults.BaseTopic;
    public bool   Retain { get; set; } = Defaults.Retain;

    public bool   DirectMode { get; set; } = Defaults.DirectMode;
    /// <summary>0=all, 1=only changes, 2=only on request</summary>
    public int    UpdateMode { get; set; } = Defaults.UpdateMode;
    
// Throttle knobs
    public int    PublishMinIntervalMs { get; set; } = Defaults.PublishMinIntervalMs;
    public float  PublishMinDelta      { get; set; } = Defaults.PublishMinDelta;
    
public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaveNode.Mqtt");

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static AppConfig LoadOrPrompt()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                return ApplyEnvOverrides(loaded);
            }
        }
        catch { /* fall back to prompt */ }

        // First run – show a small dialog with helpful tooltips
        var fresh = new AppConfig();
        using (var dlg = new SetupForm(fresh))
        {
            var result = dlg.ShowDialog();
            if (result != DialogResult.OK)
            {
                MessageBox.Show("Setup cancelled. Exiting.", "WaveNode → MQTT", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Environment.Exit(1);
            }
        }

        Save(fresh);
        return ApplyEnvOverrides(fresh);
    }

    public static void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json, Encoding.UTF8);
    }

    private static AppConfig ApplyEnvOverrides(AppConfig cfg)
    {
        // Env vars override file (useful for quick testing without changing saved config)
        string? e(string k) => Environment.GetEnvironmentVariable(k);

        cfg.MqttHost  = e("MQTT_HOST")        ?? cfg.MqttHost;
        cfg.BaseTopic = e("MQTT_BASE_TOPIC")  ?? cfg.BaseTopic;
        cfg.MqttUser  = e("MQTT_USER")        ?? cfg.MqttUser;
        cfg.MqttPass  = e("MQTT_PASS")        ?? cfg.MqttPass;

        if (int.TryParse(e("MQTT_PORT"), out var p)) cfg.MqttPort = p;
        if (bool.TryParse(e("MQTT_RETAIN"), out var r)) cfg.Retain = r;
        if (bool.TryParse(e("WAVENODE_DIRECT"), out var d)) cfg.DirectMode = d;
        if (int.TryParse(e("WAVENODE_UPDATE_MODE"), out var u)) cfg.UpdateMode = Math.Clamp(u, 0, 2);

        // Throttle envs
        if (int.TryParse(e("WAVENODE_MIN_INTERVAL_MS"), out var ms)) cfg.PublishMinIntervalMs = Math.Max(0, ms);
        if (float.TryParse(e("WAVENODE_MIN_DELTA"), out var delta))  cfg.PublishMinDelta      = Math.Max(0f, delta);
        return cfg;
    }
}

/// <summary>
/// First-run WinForms dialog with defaults, explanations, and DPI-safe layout (no clipped buttons).
/// </summary>
internal sealed class SetupForm : Form
{
    private readonly ToolTip _tt = new() { AutoPopDelay = 16000, InitialDelay = 400, ReshowDelay = 100 };

    private readonly TextBox _host = new() { Width = 240, Text = Defaults.MqttHost };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = Defaults.MqttPort, Width = 140 };
    private readonly TextBox _user = new() { Width = 240, Text = Defaults.MqttUser };
    private readonly TextBox _pass = new() { Width = 240, UseSystemPasswordChar = true, Text = Defaults.MqttPass };
    private readonly TextBox _topic = new() { Width = 240, Text = Defaults.BaseTopic };
    private readonly CheckBox _retain = new() { Text = $"Retain MQTT messages (default: {Defaults.Retain})", Checked = Defaults.Retain };
    private readonly CheckBox _direct = new() { Text = $"Direct Mode (default: {Defaults.DirectMode})", Checked = Defaults.DirectMode };
    private readonly NumericUpDown _update = new() { Minimum = 0, Maximum = 2, Value = Defaults.UpdateMode, Width = 140 };

    // NEW: throttle controls
    private readonly NumericUpDown _minIntervalMs = new()
    {
        Minimum = 0, Maximum = 60000, Value = Defaults.PublishMinIntervalMs, Width = 140, Increment = 50
    };
    private readonly NumericUpDown _minDelta = new()
    {
        Minimum = 0, Maximum = 1000, DecimalPlaces = 3, Value = (decimal)Defaults.PublishMinDelta, Width = 140, Increment = 0.01M
    };


    private readonly Button _ok     = new() { Text = "Save",   DialogResult = DialogResult.OK,     AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14, 6, 14, 6) };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14, 6, 14, 6) };

    private readonly AppConfig _cfg;

    public SetupForm(AppConfig cfg)
    {
        _cfg = cfg;

        Text = "WaveNode → MQTT • First-time Setup";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;

        // DPI-aware autoscaling + sane default font
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont;

        // Let the layout compute size, but avoid being too narrow
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        MinimumSize = new Size(560, 0); // prevents clipping at 125/150/200% scale

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Text = "This app publishes WaveNode meter data to MQTT.\n" +
                   "Defaults usually work for a local broker. Hover any field for help.\n" +
                   "Change these anytime by deleting the config file."
        };

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(4),
            GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };

        void addRow(string label, Control control, string tip)
        {
            int row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var lab = new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Padding = new Padding(0, 6, 0, 0),
                Margin = new Padding(0, 6, 6, 0)
            };
            control.Margin = new Padding(0, 4, 0, 0);
            layout.Controls.Add(lab, 0, row);
            layout.Controls.Add(control, 1, row);
            _tt.SetToolTip(lab, tip);
            _tt.SetToolTip(control, tip);
        }

        addRow("MQTT Host:", _host, Defaults.ExpMqttHost);
        addRow("MQTT Port:", _port, Defaults.ExpMqttPort);
        addRow("MQTT User:", _user, Defaults.ExpMqttUser);
        addRow("MQTT Pass:", _pass, Defaults.ExpMqttPass);
        addRow("Base Topic:", _topic, Defaults.ExpBaseTopic);
        addRow("", _retain, Defaults.ExpRetain);
        addRow("", _direct, Defaults.ExpDirect);
        addRow("Update Mode (0/1/2):", _update, Defaults.ExpUpdateMode);

        // NEW throttle rows
        addRow("Min Publish Interval (ms):", _minIntervalMs, Defaults.ExpMinInterval);
        addRow("Min Publish Delta:", _minDelta, Defaults.ExpMinDelta);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 10, 0, 0), // space above buttons
            Margin = new Padding(0, 10, 0, 0)
        };
        _ok.Margin = new Padding(6, 0, 0, 0);
        _cancel.Margin = new Padding(6, 0, 0, 0);
        buttons.Controls.AddRange(new Control[] { _ok, _cancel });

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(intro, 0, 0);
        root.Controls.Add(layout, 0, 1);
        root.Controls.Add(buttons, 0, 2);

        Controls.Add(root);

        AcceptButton = _ok;
        CancelButton = _cancel;

        _ok.Click += (_, __) =>
        {
            if (string.IsNullOrWhiteSpace(_host.Text))
            {
                MessageBox.Show("Please enter an MQTT host.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            if (string.IsNullOrWhiteSpace(_topic.Text))
            {
                MessageBox.Show("Please enter a base topic.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            _cfg.MqttHost  = _host.Text.Trim();
            _cfg.MqttPort  = (int)_port.Value;
            _cfg.MqttUser  = _user.Text ?? "";
            _cfg.MqttPass  = _pass.Text ?? "";
            _cfg.BaseTopic = _topic.Text.Trim();
            _cfg.Retain    = _retain.Checked;
            _cfg.DirectMode= _direct.Checked;
            _cfg.UpdateMode= (int)_update.Value;

            // NEW: save throttle fields
            _cfg.PublishMinIntervalMs = (int)_minIntervalMs.Value;
            _cfg.PublishMinDelta      = (float)_minDelta.Value;
        };
    }
}

#endregion

public sealed class WaveNodeMqttApp : IDisposable
{
    // ===== Win32 interop =====
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private const string EXPORT_MSG_STR  = "WaveNode RegisterWindowMessage Export Messages";
    private const string RECEIVE_MSG_STR = "WaveNode RegisterWindowMessage Receive Messages";
    private const string REG_KEY_HANDLE  = @"Software\WaveNode\WN\Handle";

    // Commands to send to WaveNode (from WN_InOut.h)
    private const int WAVENODE_USE_THIS_HANDLE = 89;
    private const int WAVENODE_UPDATE_MODE     = 4;  // lParam: 0,1,2

    // Message type IDs (subset; extend as needed)
    private const int WAVENODE_PEAK_WATTS_ONE       = 1;
    private const int WAVENODE_PEAK_WATTS_TWO       = 2;
    private const int WAVENODE_PEAK_WATTS_THREE     = 3;
    private const int WAVENODE_PEAK_WATTS_FOUR      = 4;
    private const int WAVENODE_SWR1                 = 5;
    private const int WAVENODE_SWR2                 = 6;
    private const int WAVENODE_SWR3                 = 7;
    private const int WAVENODE_SWR4                 = 8;
    private const int WAVENODE_AVG_WATTS_ONE        = 9;
    private const int WAVENODE_AVG_WATTS_TWO        = 10;
    private const int WAVENODE_AVG_WATTS_THREE      = 11;
    private const int WAVENODE_AVG_WATTS_FOUR       = 12;
    private const int WAVENODE_DC_VOLTS             = 13;
    private const int WAVENODE_DC_AMPS              = 14;
    private const int WAVENODE_AUX1                 = 15;
    private const int WAVENODE_AUX2                 = 16;
    private const int WAVENODE_ROTATORHEADING_ONE   = 20;
    private const int WAVENODE_ROTATORHEADING_TWO   = 21;
    private const int WAVENODE_ROTATOR_IN_MOTION_1  = 22; // bool
    private const int WAVENODE_DESTINATION_HEADING  = 23;
    private const int WAVENODE_ROTATOR_IN_MOTION_2  = 24; // bool
    private const int WAVENODE_TEST_FLOAT           = 90;
    private const int WAVENODE_MY_HANDLE            = 89; // WaveNode sends its own HWND occasionally

    private readonly uint _exportMsgId;
    private readonly uint _receiveMsgId;

    private readonly AppConfig _cfg;

    private readonly HiddenMessageWindow _window;
    private IMqttClient? _client;
    private MqttClientOptions? _options;

    // Map message IDs to topic suffixes and a value transform (bool/int vs float)
    private readonly Dictionary<int, (string topic, Func<long, string> decode)> _map;

    // === Caches for derived metrics ===
    private readonly Dictionary<int, float> _lastPeakFwd = new();
    private readonly Dictionary<int, float> _lastAvgFwd  = new();
    private readonly Dictionary<int, float> _lastSWR     = new();

    // === per-topic publish throttling caches ===
    private readonly Dictionary<string, (DateTime t, float v)> _lastFloatOut = new();
    private readonly Dictionary<string, DateTime> _lastAnyOut = new();

    public WaveNodeMqttApp(AppConfig cfg)
    {
        _cfg = cfg;

        _exportMsgId  = RegisterWindowMessage(EXPORT_MSG_STR);
        _receiveMsgId = RegisterWindowMessage(RECEIVE_MSG_STR);

        // Create hidden message window
        _window = new HiddenMessageWindow(OnWndMessage);

        // Build map
        _map = new()
        {
            [WAVENODE_PEAK_WATTS_ONE]   = ("peak_watts/1", DecodeFloat),
            [WAVENODE_PEAK_WATTS_TWO]   = ("peak_watts/2", DecodeFloat),
            [WAVENODE_PEAK_WATTS_THREE] = ("peak_watts/3", DecodeFloat),
            [WAVENODE_PEAK_WATTS_FOUR]  = ("peak_watts/4", DecodeFloat),

            [WAVENODE_AVG_WATTS_ONE]    = ("avg_watts/1", DecodeFloat),
            [WAVENODE_AVG_WATTS_TWO]    = ("avg_watts/2", DecodeFloat),
            [WAVENODE_AVG_WATTS_THREE]  = ("avg_watts/3", DecodeFloat),
            [WAVENODE_AVG_WATTS_FOUR]   = ("avg_watts/4", DecodeFloat),

            [WAVENODE_SWR1]             = ("swr1", DecodeFloat),
            [WAVENODE_SWR2]             = ("swr2", DecodeFloat),
            [WAVENODE_SWR3]             = ("swr3", DecodeFloat),
            [WAVENODE_SWR4]             = ("swr4", DecodeFloat),

            [WAVENODE_DC_VOLTS]         = ("dc/volts", DecodeFloat),
            [WAVENODE_DC_AMPS]          = ("dc/amps", DecodeFloat),

            [WAVENODE_AUX1]             = ("aux/1", DecodeFloat),
            [WAVENODE_AUX2]             = ("aux/2", DecodeFloat),

            [WAVENODE_ROTATORHEADING_ONE]  = ("rotator/heading/1", DecodeFloat),
            [WAVENODE_ROTATORHEADING_TWO]  = ("rotator/heading/2", DecodeFloat),
            [WAVENODE_DESTINATION_HEADING] = ("rotator/destination_heading", DecodeFloat),

            [WAVENODE_ROTATOR_IN_MOTION_1] = ("rotator/in_motion/1", DecodeBool),
            [WAVENODE_ROTATOR_IN_MOTION_2] = ("rotator/in_motion/2", DecodeBool),

            [WAVENODE_TEST_FLOAT]          = ("debug/test_float", DecodeFloat),

            [WAVENODE_MY_HANDLE]           = ("debug/wavenode_hwnd", raw => ((IntPtr)raw).ToString())
        };
    }

    public async Task StartAsync()
    {
        // MQTT client setup
        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_cfg.MqttHost, _cfg.MqttPort)
            .WithClientId($"WaveNodeMQTT-{Environment.MachineName}-{Guid.NewGuid():N}".AsSpan(0, 20).ToString());

        if (!string.IsNullOrEmpty(_cfg.MqttUser))
            builder = builder.WithCredentials(_cfg.MqttUser, _cfg.MqttPass);

        _options = builder.Build();

        _client.DisconnectedAsync += async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            try { await _client.ConnectAsync(_options!, CancellationToken.None); } catch { /* retry loop continues */ }
        };

        if (!_client.IsConnected)
        {
            try { await _client.ConnectAsync(_options, CancellationToken.None); }
            catch (Exception ex) { Console.Error.WriteLine("MQTT connect error: " + ex.Message); }
        }

        // Optionally ask WaveNode to send directly to our window handle
        if (_cfg.DirectMode)
        {
            var hn = GetWaveNodeHwnd(); // auto-discovery (registry, process name, window title)
            if (hn != IntPtr.Zero)
            {
                // Tell WaveNode: use our HWND
                PostMessage(hn, _receiveMsgId, (IntPtr)WAVENODE_USE_THIS_HANDLE, _window.Handle);

                // Set update mode (0 all, 1 only changes, 2 only on request)
                PostMessage(hn, _receiveMsgId, (IntPtr)WAVENODE_UPDATE_MODE, (IntPtr)_cfg.UpdateMode);
                Console.WriteLine($"WaveNode HWND set to direct: 0x{hn.ToInt64():X}");
            }
            else
            {
                Console.WriteLine("WaveNode HWND not found; will listen to broadcasts.");
            }
        }

        Console.WriteLine($"ExportMsgId=0x{_exportMsgId:X}, ReceiveMsgId=0x{_receiveMsgId:X}");
        Console.WriteLine("WaveNode → MQTT bridge running. Hidden window handle: " + _window.Handle);
    }

    private void OnWndMessage(ref Message m)
    {
        if (m.Msg != _exportMsgId) return;

        int wParam = m.WParam.ToInt32();
        long rawL  = m.LParam.ToInt64(); // float packed into low 32 bits

        if (_map.TryGetValue(wParam, out var meta))
        {
            var payload = meta.decode(rawL);
            Publish(meta.topic, payload).GetAwaiter().GetResult();

            // --- Derivations for reflected watts / return loss ---
            switch (wParam)
            {
                // Peak forward power
                case WAVENODE_PEAK_WATTS_ONE:
                case WAVENODE_PEAK_WATTS_TWO:
                case WAVENODE_PEAK_WATTS_THREE:
                case WAVENODE_PEAK_WATTS_FOUR:
                {
                    int ch = (wParam - WAVENODE_PEAK_WATTS_ONE) + 1; // 1..4
                    float fwd = ParseFloat(payload);
                    _lastPeakFwd[ch] = fwd;
                    float swr = _lastSWR.TryGetValue(ch, out var s) ? s : float.NaN;
                    PublishReflectedAndRLAsync(ch, fwd, swr, isPeak: true).GetAwaiter().GetResult();
                    break;
                }
                // Average forward power
                case WAVENODE_AVG_WATTS_ONE:
                case WAVENODE_AVG_WATTS_TWO:
                case WAVENODE_AVG_WATTS_THREE:
                case WAVENODE_AVG_WATTS_FOUR:
                {
                    int ch = (wParam - WAVENODE_AVG_WATTS_ONE) + 1; // 1..4
                    float fwd = ParseFloat(payload);
                    _lastAvgFwd[ch] = fwd;
                    float swr = _lastSWR.TryGetValue(ch, out var s) ? s : float.NaN;
                    PublishReflectedAndRLAsync(ch, fwd, swr, isPeak: false).GetAwaiter().GetResult();
                    break;
                }
                // SWR per channel
                case WAVENODE_SWR1:
                case WAVENODE_SWR2:
                case WAVENODE_SWR3:
                case WAVENODE_SWR4:
                {
                    int ch = (wParam - WAVENODE_SWR1) + 1; // 1..4
                    float swr = ParseFloat(payload);
                    _lastSWR[ch] = swr;
                    // Recompute using last-known forward powers
                    if (_lastPeakFwd.TryGetValue(ch, out var peakF))
                        PublishReflectedAndRLAsync(ch, peakF, swr, isPeak: true).GetAwaiter().GetResult();
                    if (_lastAvgFwd.TryGetValue(ch, out var avgF))
                        PublishReflectedAndRLAsync(ch, avgF, swr, isPeak: false).GetAwaiter().GetResult();
                    break;
                }
            }
        }
        else
        {
            // Unknown/unused message: publish to a generic channel for observability
            Publish($"unknown/{wParam}", DecodeFloat(rawL)).GetAwaiter().GetResult();
        }
    }

 // === throttling helpers ===

    private bool ShouldPublishFloat(string fullTopic, float value)
    {
        var now = DateTime.UtcNow;
        if (_lastFloatOut.TryGetValue(fullTopic, out var last))
        {
            if ((now - last.t).TotalMilliseconds < _cfg.PublishMinIntervalMs)
                return false;

            if (float.IsFinite(value) && float.IsFinite(last.v))
            {
                if (Math.Abs(value - last.v) < _cfg.PublishMinDelta)
                    return false;
            }
        }
        _lastFloatOut[fullTopic] = (now, value);
        _lastAnyOut[fullTopic] = now;
        return true;
    }

    private bool ShouldPublishAny(string fullTopic)
    {
        var now = DateTime.UtcNow;
        if (_lastAnyOut.TryGetValue(fullTopic, out var last))
        {
            if ((now - last).TotalMilliseconds < _cfg.PublishMinIntervalMs)
                return false;
        }
        _lastAnyOut[fullTopic] = now;
        return true;
    }


     private async Task Publish(string topicSuffix, string payload)
    {
        if (_client is null || !_client.IsConnected) return;

        var fullTopic = $"{_cfg.BaseTopic}/{topicSuffix}";

        // Try float throttling; fall back to generic interval throttling
        if (float.TryParse(payload, out var f))
        {
            if (!ShouldPublishFloat(fullTopic, f)) return;
        }
        else
        {
            if (!ShouldPublishAny(fullTopic)) return;
        }

        // (Optional) don't retain for fast-changing topics
        bool isFast = topicSuffix.StartsWith("peak_watts/") ||
                      topicSuffix.StartsWith("avg_watts/")  ||
                      topicSuffix.StartsWith("ref_watts/");
        bool retain = _cfg.Retain && !isFast;

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(fullTopic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .Build();

        try { await _client.PublishAsync(message, CancellationToken.None); }
        catch (Exception ex) { Console.Error.WriteLine($"MQTT publish error: {ex.Message}"); }
    }

    // ===== Helpers (including reflected power & return loss) =====

    private static string DecodeFloat(long rawLParam)
    {
        // WaveNode puts a 32-bit float directly into LPARAM.
        // On 64-bit, take the low 32 bits.
        int raw = unchecked((int)rawLParam);
        float f = BitConverter.Int32BitsToSingle(raw);
        return f.ToString("0.###");
    }

    private static string DecodeBool(long rawLParam)
    {
        // Convention: 0 = false, non-zero = true
        int raw = unchecked((int)rawLParam);
        return (raw != 0) ? "1" : "0";
    }

    private static float ParseFloat(string s) => float.TryParse(s, out var f) ? f : float.NaN;

    private static float SwrToGamma(float swr)
    {
        if (swr <= 0f || float.IsNaN(swr)) return float.NaN;
        return (swr - 1f) / (swr + 1f);
    }

    /// <summary>
    /// Publishes derived metrics:
    ///   - Reflected power (watts) at topics: ref_watts/peak/<ch> or ref_watts/avg/<ch>
    ///   - Return loss (dB) at topic: return_loss/<ch>
    /// Uses formulas:
    ///   |Γ| = (SWR-1)/(SWR+1);  Pref = Pfwd * |Γ|^2;  RL(dB) = -20 * log10(|Γ|)
    /// </summary>
    private async Task PublishReflectedAndRLAsync(int ch, float forwardW, float swr, bool isPeak)
    {
        if (forwardW <= 0 || swr <= 0 || float.IsNaN(forwardW) || float.IsNaN(swr)) return;

        var gamma = SwrToGamma(swr);
        if (float.IsNaN(gamma)) return;

        var pref = forwardW * gamma * gamma; // reflected watts
        var rl   = (gamma <= 0) ? float.PositiveInfinity
                                : -20f * (float)Math.Log10(gamma); // return loss in dB

        var basePrefix = isPeak ? "peak" : "avg";
        await Publish($"ref_watts/{basePrefix}/{ch}", pref.ToString("0.###"));
        await Publish($"return_loss/{ch}", float.IsInfinity(rl) ? "inf" : rl.ToString("0.##"));
    }

    private static bool IsWindowTitleMatch(IntPtr hWnd, params string[] needles)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        var title = sb.ToString();
        foreach (var n in needles)
            if (!string.IsNullOrWhiteSpace(n) && title.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    private static IntPtr GetWaveNodeHwnd()
    {
        // 1) Try the registry (if present on your build)
        var fromReg = GetWaveNodeHwndFromRegistry();
        if (fromReg != IntPtr.Zero) return fromReg;

        // 2) Try by process name first (adjust list if needed)
        foreach (var procName in new[] { "WaveNode", "WaveNodePro", "WN", "WaveNodeApp" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(procName))
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                        return p.MainWindowHandle;
                }
            }
            catch { /* ignore */ }
        }

        // 3) Enumerate all top-level windows and look for a plausible title or process
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            if (found != IntPtr.Zero) return false;

            uint pid;
            GetWindowThreadProcessId(h, out pid);
            try
            {
                var p = Process.GetProcessById((int)pid);
                var pname = p.ProcessName?.ToLowerInvariant() ?? "";

                bool procLooksRight  = pname.Contains("wavenode") || pname.Equals("wn");
                bool titleLooksRight = IsWindowTitleMatch(h, "WaveNode", "WaveNode Pro", "WN", "Wave Node");

                if (procLooksRight || titleLooksRight)
                {
                    found = h;
                    return false; // stop
                }
            }
            catch { /* ignore */ }
            return true; // keep going
        }, IntPtr.Zero);

        return found; // may be IntPtr.Zero; caller falls back to broadcast
    }

    private static IntPtr GetWaveNodeHwndFromRegistry()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\WaveNode\WN");
            if (k == null) return IntPtr.Zero;

            var v = k.GetValue("Handle");
            if (v is int i) return new IntPtr(i);
            if (v is long l) return new IntPtr(l);

            // Some builds might store as string
            if (v is string s && long.TryParse(s, out var ls)) return new IntPtr(ls);
        }
        catch { /* ignore */ }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        try { _client?.Dispose(); } catch { }
        _window?.DestroyHandle();
    }

    // Hidden NativeWindow to receive messages
    private sealed class HiddenMessageWindow : NativeWindow
    {
        private readonly ActionRefMessage _callback;

        public HiddenMessageWindow(ActionRefMessage cb)
        {
            _callback = cb;
            var cp = new CreateParams
            {
                Caption = "WaveNode.MQTT.Hidden",
                ClassName = "STATIC",
                X = 0, Y = 0, Width = 0, Height = 0,
                Style = 0
            };
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            _callback(ref m);
            base.WndProc(ref m);
        }
    }

    private delegate void ActionRefMessage(ref Message m);
}
