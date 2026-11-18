using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AudioSwitch;

static class Program
{
    const int AttachParentProcess = -1;

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test")
            Environment.Exit(RunSelfTest(args.Length > 1 ? args[1] : null));

        if (args.Length > 0 && args[0] == "--preview")
        {
            var path = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "audioswitch-preview.png");
            try
            {
                ApplicationConfiguration.Initialize();
                using var popup = new SwitchPopup();
                popup.SavePreview(path, forceDark: null);
                var opposite = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(path)) ?? Path.GetTempPath(),
                    Path.GetFileNameWithoutExtension(path) + "-alt.png");
                popup.SavePreview(opposite, forceDark: !Theme.Load().Dark);
            }
            catch (Exception ex)
            {
                File.WriteAllText(path + ".error.txt", ex.ToString());
                Environment.Exit(1);
            }

            return;
        }

        using var mutex = new Mutex(true, @"Local\AudioSwitch.Tray", out var created);
        if (!created)
            return;

        ApplicationConfiguration.Initialize();
        using var tray = new TrayApplication();
        Application.Run();
        GC.KeepAlive(mutex);
    }

    static int RunSelfTest(string? reportPath)
    {
        if (!string.IsNullOrEmpty(reportPath))
        {
            using var report = new StreamWriter(reportPath) { AutoFlush = true };
            return AudioDevices.SelfTest(report);
        }

        AttachConsole(AttachParentProcess);
        StreamWriter? writer = null;
        try
        {
            var stream = new FileStream("CONOUT$", FileMode.Open, FileAccess.Write);
            writer = new StreamWriter(stream) { AutoFlush = true };
            Console.SetOut(writer);
        }
        catch (IOException)
        {
        }

        var code = AudioDevices.SelfTest(Console.Out);
        writer?.Flush();
        return code;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int processId);
}

sealed class TrayApplication : IDisposable
{
    readonly NotifyIcon _icon;
    readonly SwitchPopup _popup;
    readonly IntPtr _iconHandle;
    DateTime _hiddenAt;

    public TrayApplication()
    {
        var icon = CreateIcon(out _iconHandle);
        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = "Audio Switch",
            Visible = true,
        };

        _popup = new SwitchPopup();
        _popup.DefaultsChanged += UpdateTooltip;
        _popup.ExitRequested += () =>
        {
            _icon.Visible = false;
            Application.Exit();
        };
        _popup.VisibleChanged += (_, _) =>
        {
            if (!_popup.Visible)
                _hiddenAt = DateTime.UtcNow;
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button is not (MouseButtons.Left or MouseButtons.Right))
                return;

            // The tray click deactivates the popup before MouseUp. Ignore that
            // click so the icon closes the popup instead of opening it again.
            if ((DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 200)
                return;

            if (_popup.Visible)
                _popup.Hide();
            else
                _popup.Present(Cursor.Position);
        };

        UpdateTooltip();
    }

    void UpdateTooltip()
    {
        try
        {
            var speakers = NameOfDefault(AudioFlow.Render);
            var microphone = NameOfDefault(AudioFlow.Capture);
            var text = $"Speakers: {speakers}\nMicrophone: {microphone}";
            _icon.Text = text.Length <= 127 ? text : string.Concat(text.AsSpan(0, 124), "...");
        }
        catch
        {
            _icon.Text = "Audio Switch";
        }
    }

    static string NameOfDefault(AudioFlow flow)
    {
        var match = AudioDevices.List(flow).FirstOrDefault(device => device.IsDefault);
        return match?.Name ?? "None";
    }

    static Icon CreateIcon(out IntPtr handle)
    {
        var accent = Theme.Load().Accent;
        var glyph = Theme.Luma(accent) > 0.62f ? Color.FromArgb(28, 28, 28) : Color.White;
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(accent);
            SwitchPopup.FillRound(graphics, new RectangleF(0.5f, 0.5f, 31f, 31f), 8f, brush);
            SwitchPopup.DrawSpeaker(graphics, new RectangleF(7.5f, 7.5f, 17f, 17f), glyph);
        }

        handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _popup.Dispose();
        DestroyIcon(_iconHandle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr handle);
}

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "AudioSwitch";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return string.Equals(key?.GetValue(ValueName) as string, QuotedPath(), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Couldn't open the Windows startup settings.");
        if (enabled)
            key.SetValue(ValueName, QuotedPath());
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    static string QuotedPath() => $"\"{Application.ExecutablePath}\"";
}
