using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ModeSwitcher;

internal enum DesktopMode { Work, Game }

internal static class Program
{
    private static readonly string DataDirectory = Environment.GetEnvironmentVariable("MODESWITCHER_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModeSwitcher");
    private static readonly string StatePath = Path.Combine(DataDirectory, "state.txt");
    private static readonly string LogPath = Path.Combine(DataDirectory, "ModeSwitcher.log");

    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(DataDirectory);
        using var mutex = new Mutex(true, @"Local\ModeSwitcher.SingleInstance", out var ownsMutex);
        if (!ownsMutex) return 0;

        try { return RunAsync(args).GetAwaiter().GetResult(); }
        finally { mutex.ReleaseMutex(); }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Any(a => a.Equals("--notification-test", StringComparison.OrdinalIgnoreCase)))
        {
            ModeNotification.Show("Game mode on");
            return 0;
        }

        var dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
        try
        {
            var previous = ReadState();
            var target = previous == DesktopMode.Game ? DesktopMode.Work : DesktopMode.Game;
            Log($"Switch requested: {previous} -> {target}{(dryRun ? " (dry run)" : "")}");

            if (dryRun)
            {
                Log($"HDR-capable active displays: {HdrController.GetHdrCapableDisplayCount()}");
                Log($"Do not disturb: {DoNotDisturbController.DescribeCurrentState()}");
                Log($"Skydimo endpoint: {(await SkydimoController.FindEndpointAsync(TimeSpan.FromMilliseconds(600)) is null ? "not found" : "found")}");
                return 0;
            }

            var errors = new List<string>();
            Try("HDR", () => HdrController.SetHdr(target == DesktopMode.Game), errors);
            Try("Do not disturb", () => DoNotDisturbController.SetEnabled(target == DesktopMode.Game), errors);

            try
            {
                await SkydimoController.SetLightingAsync(target == DesktopMode.Game, Log);
            }
            catch (Exception ex)
            {
                errors.Add($"Skydimo: {ex.Message}");
            }

            if (target == DesktopMode.Game)
                Try("Steam", SteamController.Open, errors);

            File.WriteAllText(StatePath, target.ToString(), Encoding.UTF8);
            Log($"Mode saved as {target}. " + (errors.Count == 0 ? "All settings applied." : string.Join(" | ", errors)));
            ModeNotification.Show($"{target} mode on");
            return errors.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log("Fatal: " + ex);
            return 2;
        }
    }

    private static DesktopMode ReadState()
    {
        if (File.Exists(StatePath) && Enum.TryParse<DesktopMode>(File.ReadAllText(StatePath).Trim(), true, out var mode))
            return mode;
        return DesktopMode.Work; // The first launch enters Game mode.
    }

    private static void Try(string name, Action action, List<string> errors)
    {
        try { action(); }
        catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
    }

    private static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {message}{Environment.NewLine}"); }
        catch { }
    }
}

internal static class ModeNotification
{
    public static void Show(string message)
    {
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.Run(new ModePopup(message));
            }
            catch { }
        })
        {
            IsBackground = false,
            Name = "ModeSwitcher notification"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private sealed class ModePopup : Form
    {
        private readonly System.Windows.Forms.Timer lifetimeTimer = new() { Interval = 10_000 };
        private readonly System.Windows.Forms.Timer animationTimer = new() { Interval = 25 };
        private bool closing;

        public ModePopup(string message)
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(30, 32, 38);
            ClientSize = new Size(360, 96);
            ControlBox = false;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            Opacity = 0;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;

            var iconBox = new PictureBox
            {
                BackColor = Color.Transparent,
                Location = new Point(18, 20),
                Size = new Size(56, 56),
                SizeMode = PictureBoxSizeMode.Zoom
            };
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ModeSwitcher.Icon.png");
                if (stream is not null)
                {
                    using var source = new Bitmap(stream);
                    iconBox.Image = new Bitmap(source);
                }
                else
                {
                    using var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
                    iconBox.Image = icon?.ToBitmap();
                }
            }
            catch { }

            var heading = new Label
            {
                AutoSize = true,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                ForeColor = Color.FromArgb(174, 178, 188),
                Location = new Point(92, 19),
                Text = "MODE SWITCHER"
            };

            var body = new Label
            {
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(88, 42),
                Size = new Size(250, 36),
                Text = message,
                TextAlign = ContentAlignment.MiddleLeft
            };

            Controls.Add(iconBox);
            Controls.Add(heading);
            Controls.Add(body);

            WireDismiss(this);
            lifetimeTimer.Tick += (_, _) => BeginClose();
            animationTimer.Tick += (_, _) => Animate();
            Shown += (_, _) =>
            {
                var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
                Location = new Point(workArea.Right - Width - 18, workArea.Bottom - Height - 18);
                Region = RoundedRegion(ClientRectangle, 16);
                lifetimeTimer.Start();
                animationTimer.Start();
            };
            FormClosed += (_, _) => iconBox.Image?.Dispose();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int CsDropShadow = 0x00020000;
                const int WsExToolWindow = 0x00000080;
                const int WsExNoActivate = 0x08000000;
                var parameters = base.CreateParams;
                parameters.ClassStyle |= CsDropShadow;
                parameters.ExStyle |= WsExToolWindow | WsExNoActivate;
                return parameters;
            }
        }

        private void WireDismiss(Control control)
        {
            control.Cursor = Cursors.Hand;
            control.Click += (_, _) => BeginClose();
            foreach (Control child in control.Controls) WireDismiss(child);
        }

        private void BeginClose()
        {
            closing = true;
            lifetimeTimer.Stop();
            animationTimer.Start();
        }

        private void Animate()
        {
            if (!closing)
            {
                Opacity = Math.Min(1, Opacity + 0.12);
                if (Opacity >= 1) animationTimer.Stop();
                return;
            }

            Opacity = Math.Max(0, Opacity - 0.14);
            if (Opacity <= 0) Close();
        }

        private static Region RoundedRegion(Rectangle bounds, int radius)
        {
            var diameter = radius * 2;
            using var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return new Region(path);
        }
    }
}

internal static class DoNotDisturbController
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string ValueName = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";
    private const string Unrestricted = "Microsoft.QuietHoursProfile.Unrestricted";
    private const string PriorityOnly = "Microsoft.QuietHoursProfile.PriorityOnly";
    private const uint ClsctxLocalServer = 0x4;
    private static readonly Guid ClassId = new("f53321fa-34f8-4b7f-b9a3-361877cb94cf");
    private static readonly Guid InterfaceId = new("6bff4732-81ec-4ffb-ae67-b6c1bc29631f");

    public static void SetEnabled(bool enabled)
    {
        // Keep the master notifications switch enabled. DND controls whether notifications interrupt the user.
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true)
            ?? throw new InvalidOperationException("Could not open the Windows notification settings key.");
        key.SetValue(ValueName, 1, RegistryValueKind.DWord);

        WithSettings(settings =>
        {
            var profile = enabled ? PriorityOnly : Unrestricted;
            var result = settings.SetUserSelectedProfile(profile);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
        });
    }

    public static string DescribeCurrentState()
    {
        try
        {
            string? profile = null;
            WithSettings(settings =>
            {
                var result = settings.GetUserSelectedProfile(out var value);
                if (result < 0) Marshal.ThrowExceptionForHR(result);
                try { profile = Marshal.PtrToStringUni(value); }
                finally { if (value != IntPtr.Zero) Marshal.FreeCoTaskMem(value); }
            });
            return profile switch
            {
                Unrestricted => "off",
                PriorityOnly => "on (priority only)",
                "Microsoft.QuietHoursProfile.AlarmsOnly" => "on (alarms only)",
                _ => profile ?? "unknown"
            };
        }
        catch (Exception ex) { return "unavailable: " + ex.Message; }
    }

    private static void WithSettings(Action<IQuietHoursSettings> action)
    {
        var classId = ClassId;
        var interfaceId = InterfaceId;
        var result = CoCreateInstance(ref classId, IntPtr.Zero, ClsctxLocalServer, ref interfaceId, out var pointer);
        if (result < 0) Marshal.ThrowExceptionForHR(result);
        object? instance = null;
        try
        {
            instance = Marshal.GetObjectForIUnknown(pointer);
            Marshal.Release(pointer);
            pointer = IntPtr.Zero;
            action((IQuietHoursSettings)instance);
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
            if (pointer != IntPtr.Zero) Marshal.Release(pointer);
        }
    }

    [ComImport, Guid("6bff4732-81ec-4ffb-ae67-b6c1bc29631f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IQuietHoursSettings
    {
        [PreserveSig] int GetUserSelectedProfile(out IntPtr profileId);
        [PreserveSig] int SetUserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] string profileId);
        [PreserveSig] int GetProfile([MarshalAs(UnmanagedType.LPWStr)] string profileId, out IntPtr profile);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid classId, IntPtr outer, uint context,
        ref Guid interfaceId, out IntPtr instance);
}

internal static class SteamController
{
    public static void Open()
    {
        var candidates = new[]
        {
            Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamExe") as string,
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") is string p
                ? Path.Combine(p, "steam.exe") : null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe")
        };
        var steam = candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        if (steam is null) throw new FileNotFoundException("Steam is not installed or could not be located.");
        Process.Start(new ProcessStartInfo(steam) { UseShellExecute = true });
    }
}

internal static class SkydimoController
{
    public static async Task SetLightingAsync(bool on, Action<string> log)
    {
        var endpoint = await FindEndpointAsync(TimeSpan.FromMilliseconds(900));
        if (endpoint is null)
        {
            var launcher = @"C:\Program Files\Skydimo\launcher.exe";
            if (!File.Exists(launcher)) throw new FileNotFoundException("Skydimo is not installed.");
            Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = true });
            for (var i = 0; i < 12 && endpoint is null; i++)
            {
                await Task.Delay(500);
                endpoint = await FindEndpointAsync(TimeSpan.FromMilliseconds(900));
            }
        }
        if (endpoint is null) throw new InvalidOperationException("Skydimo started, but its local API was not found.");

        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await socket.ConnectAsync(endpoint, timeout.Token);
        var devices = await RpcAsync(socket, "get_devices", null, 100, timeout.Token);
        if (devices.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Skydimo returned an invalid device list.");

        var count = 0;
        foreach (var device in devices.EnumerateArray())
        {
            if (!device.TryGetProperty("port", out var portElement)) continue;
            var port = portElement.GetString();
            if (string.IsNullOrWhiteSpace(port)) continue;
            await RpcAsync(socket, "set_scope_power", new { port, off = !on }, 101 + count, timeout.Token);
            count++;
        }
        if (count == 0) throw new InvalidOperationException("No Skydimo lighting devices were detected.");
        log($"Skydimo lighting set {(on ? "on" : "off")} for {count} device(s).");
    }

    public static async Task<Uri?> FindEndpointAsync(TimeSpan timeoutPerPort)
    {
        int[] ports;
        try
        {
            ports = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Where(e => e.Address.ToString() is "127.0.0.1" or "::1" or "0.0.0.0" or "::")
                .Select(e => e.Port).Distinct().ToArray();
        }
        catch { return null; }

        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var tasks = ports.Select(port => ProbeAsync(port, timeoutPerPort, overall.Token)).ToList();
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);
            var result = await done;
            if (result is not null)
            {
                overall.Cancel();
                await Task.WhenAll(tasks);
                return result;
            }
        }
        return null;
    }

    private static async Task<Uri?> ProbeAsync(int port, TimeSpan timeoutPerPort, CancellationToken overall)
    {
        var uri = new Uri($"ws://127.0.0.1:{port}/");
        using var socket = new ClientWebSocket();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(overall);
        timeout.CancelAfter(timeoutPerPort);
        try
        {
            await socket.ConnectAsync(uri, timeout.Token);
            _ = await RpcAsync(socket, "get_system_info", null, port, timeout.Token);
            return uri;
        }
        catch { return null; }
    }

    private static async Task<JsonElement> RpcAsync(ClientWebSocket socket, string method, object? parameters, int id, CancellationToken token)
    {
        var request = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", method, @params = parameters, id });
        await socket.SendAsync(request, WebSocketMessageType.Text, true, token);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult receive;
            do
            {
                receive = await socket.ReceiveAsync(buffer, token);
                if (receive.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Skydimo closed the connection.");
                stream.Write(buffer, 0, receive.Count);
            } while (!receive.EndOfMessage);

            using var document = JsonDocument.Parse(stream.ToArray());
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
            if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.ToString());
            return root.GetProperty("result").Clone();
        }
    }
}

internal static class HdrController
{
    private const uint QdcOnlyActivePaths = 2;
    private const int ErrorSuccess = 0;
    private const int GetAdvancedColorInfo = 9;
    private const int SetAdvancedColorState = 10;
    private const int GetAdvancedColorInfo2 = 15;
    private const int SetHdrState = 16;
    private const int HdrMode = 2;

    public static int GetHdrCapableDisplayCount() => GetTargets().Count(t => Query(t).Supported);

    public static void SetHdr(bool enabled)
    {
        var targets = GetTargets();
        var capable = 0;
        var failures = new List<string>();
        foreach (var target in targets)
        {
            var status = Query(target);
            if (!status.Supported) continue;
            capable++;
            if (status.Enabled == enabled) continue;

            var result = status.Modern
                ? SetModern(target, enabled)
                : SetLegacy(target, enabled);
            if (result != ErrorSuccess) failures.Add($"display {target.Id} returned Windows error {result}");
        }
        if (capable == 0) throw new InvalidOperationException("No active HDR-capable display was found.");
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(", ", failures));
    }

    private static (bool Supported, bool Enabled, bool Modern) Query(Target target)
    {
        var modern = new AdvancedColorInfo2
        {
            Header = Header(GetAdvancedColorInfo2, (uint)Marshal.SizeOf<AdvancedColorInfo2>(), target)
        };
        if (DisplayConfigGetDeviceInfo(ref modern) == ErrorSuccess)
        {
            // Windows 11 24H2+: bit 4 is HDR support and bit 3 is the policy limitation.
            var supported = (modern.Flags & (1U << 4)) != 0 && (modern.Flags & (1U << 3)) == 0;
            return (supported || modern.ActiveColorMode == HdrMode, modern.ActiveColorMode == HdrMode, true);
        }

        var legacy = new AdvancedColorInfo
        {
            Header = Header(GetAdvancedColorInfo, (uint)Marshal.SizeOf<AdvancedColorInfo>(), target)
        };
        if (DisplayConfigGetDeviceInfo(ref legacy) == ErrorSuccess)
        {
            var flags = legacy.Flags;
            return ((flags & 1) != 0 && (flags & 8) == 0, (flags & 2) != 0, false);
        }
        return (false, false, false);
    }

    private static int SetModern(Target target, bool enabled)
    {
        var value = new SetColorState
        {
            Header = Header(SetHdrState, (uint)Marshal.SizeOf<SetColorState>(), target),
            Value = enabled ? 1U : 0U
        };
        return DisplayConfigSetDeviceInfo(ref value);
    }

    private static int SetLegacy(Target target, bool enabled)
    {
        var value = new SetColorState
        {
            Header = Header(SetAdvancedColorState, (uint)Marshal.SizeOf<SetColorState>(), target),
            Value = enabled ? 1U : 0U
        };
        return DisplayConfigSetDeviceInfo(ref value);
    }

    private static DeviceInfoHeader Header(int type, uint size, Target target) => new()
    {
        Type = type, Size = size, AdapterId = target.AdapterId, Id = target.Id
    };

    private static List<Target> GetTargets()
    {
        ValidateLayouts();
        var result = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount);
        if (result != ErrorSuccess) throw new InvalidOperationException($"Could not enumerate displays (Windows error {result}).");
        var paths = new DisplayPathInfo[pathCount];
        var modes = new DisplayModeInfo[modeCount];
        result = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (result != ErrorSuccess) throw new InvalidOperationException($"Could not query displays (Windows error {result}).");
        return paths.Take((int)pathCount).Select(p => new Target(p.TargetInfo.AdapterId, p.TargetInfo.Id)).Distinct().ToList();
    }

    private static void ValidateLayouts()
    {
        var layouts = new (string Name, int Actual, int Expected)[]
        {
            (nameof(DeviceInfoHeader), Marshal.SizeOf<DeviceInfoHeader>(), 20),
            (nameof(PathSourceInfo), Marshal.SizeOf<PathSourceInfo>(), 20),
            (nameof(PathTargetInfo), Marshal.SizeOf<PathTargetInfo>(), 48),
            (nameof(DisplayPathInfo), Marshal.SizeOf<DisplayPathInfo>(), 72),
            (nameof(DisplayModeInfo), Marshal.SizeOf<DisplayModeInfo>(), 64),
            (nameof(AdvancedColorInfo), Marshal.SizeOf<AdvancedColorInfo>(), 32),
            (nameof(AdvancedColorInfo2), Marshal.SizeOf<AdvancedColorInfo2>(), 36),
            (nameof(SetColorState), Marshal.SizeOf<SetColorState>(), 24)
        };
        var mismatch = layouts.FirstOrDefault(x => x.Actual != x.Expected);
        if (mismatch.Name is not null)
            throw new InvalidOperationException($"Native layout mismatch for {mismatch.Name}: {mismatch.Actual}, expected {mismatch.Expected}.");
    }

    private readonly record struct Target(Luid AdapterId, uint Id);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint OutputTechnology;
        public uint Rotation; public uint Scaling; public Rational RefreshRate; public uint ScanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayPathInfo { public PathSourceInfo SourceInfo; public PathTargetInfo TargetInfo; public uint Flags; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DisplayModeInfo
    {
        [FieldOffset(0)] public uint InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid AdapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader { public int Type; public uint Size; public Luid AdapterId; public uint Id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo
    {
        public DeviceInfoHeader Header; public uint Flags; public int ColorEncoding; public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo2
    {
        public DeviceInfoHeader Header; public uint Flags; public int ColorEncoding; public uint BitsPerColorChannel;
        public int ActiveColorMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SetColorState { public DeviceInfoHeader Header; public uint Value; }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements,
        [Out] DisplayPathInfo[] pathArray, ref uint numModeInfoArrayElements, [Out] DisplayModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo packet);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo2 packet);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
    private static extern int DisplayConfigSetDeviceInfo(ref SetColorState packet);
}
