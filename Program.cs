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

internal enum DesktopMode { Work, Game, Music }

internal static class Program
{
    private static readonly string DataDirectory = Environment.GetEnvironmentVariable("MODESWITCHER_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModeSwitcher");
    private static readonly string StatePath = Path.Combine(DataDirectory, "state.txt");
    private static readonly string LogPath = Path.Combine(DataDirectory, "ModeSwitcher.log");

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Directory.CreateDirectory(DataDirectory);
        using var mutex = new Mutex(true, @"Local\ModeSwitcher.SingleInstance", out var ownsMutex);
        if (!ownsMutex) return 0;

        try { return RunAsync(args).GetAwaiter().GetResult(); }
        finally { mutex.ReleaseMutex(); }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var renderIcon = args.FirstOrDefault(a => a.StartsWith("--render-icon=", StringComparison.OrdinalIgnoreCase));
        if (renderIcon is not null)
        {
            AppArtwork.Render(renderIcon[14..]);
            return 0;
        }

        var renderUi = args.FirstOrDefault(a => a.StartsWith("--render-ui=", StringComparison.OrdinalIgnoreCase));
        if (renderUi is not null)
        {
            using var selector = new ModeSelectorForm(ReadState());
            selector.RenderSnapshot(renderUi[12..]);
            return 0;
        }

        if (args.Any(a => a.Equals("--notification-test", StringComparison.OrdinalIgnoreCase)))
        {
            var testMode = ParseModeArgument(args) ?? DesktopMode.Music;
            ModeNotification.Show($"{testMode} mode on");
            return 0;
        }

        var dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
        try
        {
            var previous = ReadState();
            var target = ParseModeArgument(args);

            if (!dryRun && target is null)
            {
                using var selector = new ModeSelectorForm(previous);
                Application.Run(selector);
                target = selector.SelectedMode;
                if (target is null) return 0;
            }

            target ??= previous;
            Log($"Switch requested: {previous} -> {target}{(dryRun ? " (dry run)" : "")}");

            if (dryRun)
            {
                Log(target switch
                {
                    DesktopMode.Game => "Plan: HDR on; DND on; all Skydimo devices on with ScreenMirror; open Steam.",
                    DesktopMode.Music => "Plan: leave HDR unchanged; DND off; all Skydimo devices on with AudioSine; open and play Spotify.",
                    _ => "Plan: HDR off; DND off; all Skydimo devices off."
                });
                Log($"HDR-capable active displays: {HdrController.GetHdrCapableDisplayCount()}");
                Log($"Do not disturb: {DoNotDisturbController.DescribeCurrentState()}");
                Log($"Skydimo endpoint: {(await SkydimoController.FindEndpointAsync(TimeSpan.FromMilliseconds(600)) is null ? "not found" : "found")}");
                return 0;
            }

            var errors = new List<string>();
            if (target != DesktopMode.Music)
                Try("HDR", () => HdrController.SetHdr(target == DesktopMode.Game), errors);
            Try("Do not disturb", () => DoNotDisturbController.SetEnabled(target == DesktopMode.Game), errors);

            try
            {
                var effect = target switch
                {
                    DesktopMode.Game => "ScreenMirror",
                    DesktopMode.Music => "AudioSine",
                    _ => null
                };
                await SkydimoController.SetLightingAsync(target != DesktopMode.Work, effect, Log);
            }
            catch (Exception ex)
            {
                errors.Add($"Skydimo: {ex.Message}");
            }

            if (target == DesktopMode.Game)
                Try("Steam", SteamController.Open, errors);
            else if (target == DesktopMode.Music)
                Try("Spotify", SpotifyController.OpenAndPlay, errors);

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

    private static DesktopMode? ParseModeArgument(IEnumerable<string> args)
    {
        var value = args.FirstOrDefault(a => a.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase))?[7..];
        return Enum.TryParse<DesktopMode>(value, true, out var mode) ? mode : null;
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

internal sealed class ModeSelectorForm : Form
{
    public DesktopMode? SelectedMode { get; private set; }

    public ModeSelectorForm(DesktopMode currentMode)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = ThemeColors.Window;
        ClientSize = new Size(414, 174);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Mode Switcher";
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        var close = new Button
        {
            AccessibleName = "Close",
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = ThemeColors.Window,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 13F, FontStyle.Regular),
            ForeColor = ThemeColors.Text,
            Location = new Point(ClientSize.Width - 38, 4),
            Size = new Size(34, 30),
            TabStop = false,
            Text = "×"
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Close();
        Controls.Add(close);

        var title = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
            ForeColor = ThemeColors.Text,
            Location = new Point(15, 10),
            Text = "Choose a mode"
        };
        Controls.Add(title);

        var modes = new[] { DesktopMode.Work, DesktopMode.Game, DesktopMode.Music };
        for (var index = 0; index < modes.Length; index++)
        {
            var button = new ModeButton(modes[index])
            {
                Location = new Point(15 + index * 133, 48),
                Size = new Size(118, 108)
            };
            if (modes[index] == currentMode) button.SetCurrent();
            button.Click += (_, _) =>
            {
                SelectedMode = button.Mode;
                Close();
            };
            Controls.Add(button);
        }

        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) BeginDrag(); };
        title.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) BeginDrag(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(ThemeColors.Border);
        e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    public void RenderSnapshot(string outputPath)
    {
        using var bitmap = new Bitmap(ClientSize.Width, ClientSize.Height);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-10_000, -10_000);
        ShowInTaskbar = false;
        Show();
        Application.DoEvents();
        DrawToBitmap(bitmap, ClientRectangle);
        Hide();
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        bitmap.Save(fullPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private void BeginDrag()
    {
        ReleaseCapture();
        SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);
}

internal sealed class ModeButton : Button
{
    public DesktopMode Mode { get; }

    public ModeButton(DesktopMode mode)
    {
        Mode = mode;
        AccessibleName = $"{mode} mode";
        BackColor = ThemeColors.Control;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderColor = ThemeColors.Border;
        FlatAppearance.MouseDownBackColor = ThemeColors.Accent;
        FlatAppearance.MouseOverBackColor = ThemeColors.Hover;
        Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
        ForeColor = ThemeColors.Text;
        Text = mode.ToString();
        TextAlign = ContentAlignment.BottomCenter;
        Padding = new Padding(0, 0, 0, 10);
        UseVisualStyleBackColor = false;
    }

    public void SetCurrent()
    {
        FlatAppearance.BorderColor = ThemeColors.Accent;
        FlatAppearance.BorderSize = 2;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        IconGlyph.Draw(e.Graphics, Mode, new Rectangle(37, 18, 44, 44), ForeColor);
    }
}

internal static class ThemeColors
{
    private static readonly bool Dark = ReadDarkPreference();
    public static Color Window => Dark ? Color.FromArgb(32, 32, 32) : SystemColors.Window;
    public static Color Control => Dark ? Color.FromArgb(45, 45, 45) : SystemColors.Control;
    public static Color Hover => Dark ? Color.FromArgb(58, 58, 58) : SystemColors.ControlLight;
    public static Color Border => Dark ? Color.FromArgb(92, 92, 92) : SystemColors.ControlDark;
    public static Color Text => Dark ? Color.FromArgb(245, 245, 245) : SystemColors.WindowText;
    public static Color MutedText => Dark ? Color.FromArgb(180, 180, 180) : SystemColors.GrayText;
    public static Color Accent => SystemColors.Highlight;

    private static bool ReadDarkPreference()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }
}

internal static class IconGlyph
{
    public static void Draw(Graphics graphics, DesktopMode mode, Rectangle bounds, Color color)
    {
        var pathData = mode switch
        {
            DesktopMode.Work => "M304 112 L192 112 C183.2 112 176 119.2 176 128 L176 512 C176 520.8 183.2 528 192 528 L448 528 C456.8 528 464 520.8 464 512 L464 272 L376 272 C336.2 272 304 239.8 304 200 L304 112 z M444.1 224 L352 131.9 L352 200 C352 213.3 362.7 224 376 224 L444.1 224 z M128 128 C128 92.7 156.7 64 192 64 L325.5 64 C342.5 64 358.8 70.7 370.8 82.7 L493.3 205.3 C505.3 217.3 512 233.6 512 250.6 L512 512 C512 547.3 483.3 576 448 576 L192 576 C156.7 576 128 547.3 128 512 L128 128 z",
            DesktopMode.Game => "M448 128 C554 128 640 214 640 320 C640 426 554 512 448 512 L192 512 C86 512 0 426 0 320 C0 214 86 128 192 128 L448 128 z M192 240 C178.7 240 168 250.7 168 264 L168 296 L136 296 C122.7 296 112 306.7 112 320 C112 333.3 122.7 344 136 344 L168 344 L168 376 C168 389.3 178.7 400 192 400 C205.3 400 216 389.3 216 376 L216 344 L248 344 C261.3 344 272 333.3 272 320 C272 306.7 261.3 296 248 296 L216 296 L216 264 C216 250.7 205.3 240 192 240 z M432 336 C414.3 336 400 350.3 400 368 C400 385.7 414.3 400 432 400 C449.7 400 464 385.7 464 368 C464 350.3 449.7 336 432 336 z M496 240 C478.3 240 464 254.3 464 272 C464 289.7 478.3 304 496 304 C513.7 304 528 289.7 528 272 C528 254.3 513.7 240 496 240 z",
            _ => "M532 71 C539.6 77.1 544 86.3 544 96 L544 400 C544 444.2 501 480 448 480 C395 480 352 444.2 352 400 C352 355.8 395 320 448 320 C459.2 320 470 321.6 480 324.6 L480 207.9 L256 257.7 L256 464 C256 508.2 213 544 160 544 C107 544 64 508.2 64 464 C64 419.8 107 384 160 384 C171.2 384 182 385.6 192 388.6 L192 160 C192 145 202.4 132 217.1 128.8 L505.1 64.8 C514.6 62.7 524.5 65 532.1 71.1 z"
        };

        using var path = ParsePath(pathData);
        using var matrix = new Matrix();
        var sourceWidth = mode == DesktopMode.Game ? 640F : 640F;
        matrix.Translate(bounds.Left, bounds.Top);
        matrix.Scale(bounds.Width / sourceWidth, bounds.Height / 640F);
        path.Transform(matrix);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
    }

    private static GraphicsPath ParsePath(string data)
    {
        var tokens = System.Text.RegularExpressions.Regex.Matches(data, @"[A-Za-z]|-?\d+(?:\.\d+)?")
            .Select(match => match.Value).ToArray();
        var path = new GraphicsPath(FillMode.Winding);
        var index = 0;
        var current = PointF.Empty;
        var start = PointF.Empty;
        while (index < tokens.Length)
        {
            var command = tokens[index++][0];
            switch (command)
            {
                case 'M':
                    current = ReadPoint(tokens, ref index); start = current; path.StartFigure();
                    break;
                case 'L':
                {
                    var next = ReadPoint(tokens, ref index); path.AddLine(current, next); current = next; break;
                }
                case 'C':
                {
                    var control1 = ReadPoint(tokens, ref index);
                    var control2 = ReadPoint(tokens, ref index);
                    var next = ReadPoint(tokens, ref index);
                    path.AddBezier(current, control1, control2, next); current = next; break;
                }
                case 'z': case 'Z':
                    path.CloseFigure(); current = start; break;
                default: throw new InvalidOperationException($"Unsupported SVG command {command}.");
            }
        }
        return path;
    }

    private static PointF ReadPoint(string[] tokens, ref int index) =>
        new(float.Parse(tokens[index++], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(tokens[index++], System.Globalization.CultureInfo.InvariantCulture));
}

internal static class AppArtwork
{
    public static void Render(string outputPath)
    {
        using var bitmap = new Bitmap(1024, 1024, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        using var backgroundPath = RoundedRectangle(new Rectangle(20, 20, 984, 984), 190);
        using var background = new LinearGradientBrush(new Point(80, 60), new Point(940, 980),
            Color.FromArgb(5, 23, 61), Color.FromArgb(13, 71, 128));
        graphics.FillPath(background, backgroundPath);

        var glyphColor = Color.FromArgb(250, 252, 255);
        IconGlyph.Draw(graphics, DesktopMode.Work, new Rectangle(347, 55, 330, 330), glyphColor);
        IconGlyph.Draw(graphics, DesktopMode.Game, new Rectangle(75, 620, 330, 330), glyphColor);
        IconGlyph.Draw(graphics, DesktopMode.Music, new Rectangle(619, 620, 330, 330), glyphColor);

        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        bitmap.Save(fullPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
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
            BackColor = ThemeColors.Window;
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
                ForeColor = ThemeColors.MutedText,
                Location = new Point(92, 19),
                Text = "MODE SWITCHER"
            };

            var body = new Label
            {
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold),
                ForeColor = ThemeColors.Text,
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

internal static class SpotifyController
{
    private const int WmAppCommand = 0x0319;
    private const int AppCommandMediaPlay = 46;

    public static void OpenAndPlay()
    {
        var spotifyProcesses = Process.GetProcessesByName("Spotify");
        bool alreadyPlaying;
        try { alreadyPlaying = spotifyProcesses.Any(IsLikelyPlaying); }
        finally { foreach (var process in spotifyProcesses) process.Dispose(); }
        Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
        if (alreadyPlaying) return;

        Thread.Sleep(1200);
        _ = PostMessage(new IntPtr(0xffff), WmAppCommand, IntPtr.Zero,
            new IntPtr(AppCommandMediaPlay << 16));
    }

    private static bool IsLikelyPlaying(Process process)
    {
        try
        {
            process.Refresh();
            var title = process.MainWindowTitle.Trim();
            return title.Length > 0
                && !title.Equals("Spotify", StringComparison.OrdinalIgnoreCase)
                && !title.Equals("Spotify Free", StringComparison.OrdinalIgnoreCase)
                && !title.Equals("Spotify Premium", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}

internal static class SkydimoController
{
    public static async Task SetLightingAsync(bool on, string? effectId, Action<string> log)
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await socket.ConnectAsync(endpoint, timeout.Token);
        var devices = await RpcAsync(socket, "get_devices", null, 100, timeout.Token);
        if (devices.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Skydimo returned an invalid device list.");

        var count = 0;
        foreach (var device in devices.EnumerateArray())
        {
            if (!device.TryGetProperty("port", out var portElement)) continue;
            var port = portElement.GetString();
            if (string.IsNullOrWhiteSpace(port)) continue;
            // Skydimo's RPC uses `off`; `turn_off` is an internal/config name and is ignored here.
            await RpcAsync(socket, "set_scope_power", new { port, off = !on }, 101 + count, timeout.Token);
            if (on && effectId is not null)
                // The RPC contract uses effectId; effect_id is only the persisted-config field name.
                await RpcAsync(socket, "set_scope_effect", new { port, effectId }, 10_101 + count, timeout.Token);
            count++;
        }
        if (count == 0) throw new InvalidOperationException("No Skydimo lighting devices were detected.");
        log($"Skydimo lighting set {(on ? $"on with {effectId}" : "off")} for {count} device(s).");
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
