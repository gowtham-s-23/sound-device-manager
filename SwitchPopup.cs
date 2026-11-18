using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AudioSwitch;

sealed class Theme
{
    public required bool Dark { get; init; }
    public required Color Back { get; init; }
    public required Color Border { get; init; }
    public required Color Text { get; init; }
    public required Color Muted { get; init; }
    public required Color Hover { get; init; }
    public required Color Selected { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentInk { get; init; }
    public required Color Knob { get; init; }
    public required Color TrackOff { get; init; }

    public static Theme Load(bool? forceDark = null)
    {
        var accent = ReadAccent();
        var dark = forceDark ?? !AppsUseLightTheme();
        var back = dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(255, 255, 255);
        var ink = AccentInkColor(accent, dark);
        return new Theme
        {
            Dark = dark,
            Back = back,
            Border = dark ? Color.FromArgb(68, 68, 68) : Color.FromArgb(225, 225, 225),
            Text = dark ? Color.FromArgb(245, 245, 245) : Color.FromArgb(28, 28, 28),
            Muted = dark ? Color.FromArgb(158, 158, 158) : Color.FromArgb(96, 96, 96),
            Hover = dark ? Color.FromArgb(58, 58, 58) : Color.FromArgb(243, 243, 243),
            Selected = Blend(back, accent, dark ? 0.22f : 0.14f),
            Accent = accent,
            AccentInk = ink,
            Knob = Luma(accent) > 0.72f ? Color.FromArgb(32, 32, 32) : Color.White,
            TrackOff = dark ? Color.FromArgb(92, 92, 92) : Color.FromArgb(198, 198, 198),
        };
    }

    public static float Luma(Color color) => (0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B) / 255f;

    static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value != 0;
            return true;
        }
        catch
        {
            return true;
        }
    }

    static Color ReadAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                return Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
            }
        }
        catch
        {
            // Fall through to the default Windows blue.
        }

        return Color.FromArgb(0, 120, 212);
    }

    static Color AccentInkColor(Color accent, bool dark)
    {
        if (!dark && Luma(accent) > 0.62f)
            return Darken(accent, 0.35f);
        if (dark && Luma(accent) < 0.28f)
            return Lighten(accent, 0.45f);
        return accent;
    }

    public static Color Blend(Color back, Color fore, float amount)
    {
        return Color.FromArgb(
            Mix(back.R, fore.R, amount),
            Mix(back.G, fore.G, amount),
            Mix(back.B, fore.B, amount));
    }

    public static Color Darken(Color color, float amount) => Blend(color, Color.Black, amount);

    public static Color Lighten(Color color, float amount) => Blend(color, Color.White, amount);

    static int Mix(int from, int to, float amount) => (int)Math.Round(from + (to - from) * amount);
}

sealed class SwitchPopup : Form
{
    const int MinWidthDip = 280;
    const int MaxWidthDip = 480;

    readonly System.Windows.Forms.Timer _anim;
    readonly List<Row> _rows = [];

    Theme _theme;
    Font? _rowFont;
    Font? _sectionFont;
    float _fontScale = -1;
    float _scale = 1;
    float _toggle;
    float _toggleTarget;
    int _hover = -1;
    int _pressed = -1;
    bool _suspendClose;
    int _scroll;
    int _scrollMax;
    int _listViewHeight;
    int _footerTop;
    Point _anchor;

    public SwitchPopup()
    {
        _theme = Theme.Load();
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = _theme.Back;
        _anim = new System.Windows.Forms.Timer { Interval = 15 };
        _anim.Tick += (_, _) => Animate();
    }

    public event Action? DefaultsChanged;

    public event Action? ExitRequested;

    public void Present(Point cursor)
    {
        _anchor = cursor;
        var screen = Screen.FromPoint(cursor);
        if (!IsHandleCreated)
            CreateHandle();

        Location = screen.WorkingArea.Location;
        _theme = Theme.Load();
        BackColor = _theme.Back;
        _toggle = _toggleTarget = Startup.IsEnabled() ? 1f : 0f;
        _hover = -1;
        _pressed = -1;
        _scroll = 0;
        ApplyDwm();
        Relayout();
        Position(cursor);
        Show();
        Activate();
    }

    public void SavePreview(string path, bool? forceDark)
    {
        if (!IsHandleCreated)
            CreateHandle();

        _anchor = Cursor.Position;
        _theme = Theme.Load(forceDark);
        BackColor = _theme.Back;
        _toggle = _toggleTarget = Startup.IsEnabled() ? 1f : 0f;
        _hover = -1;
        _scroll = 0;
        Relayout();

        using var bitmap = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height), PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using (var clip = RoundRect(new RectangleF(0, 0, bitmap.Width, bitmap.Height), Px(8)))
                graphics.SetClip(clip);
            graphics.Clear(_theme.Back);
            PaintSurface(graphics);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x00020000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_APPWINDOW = 0x00040000;
            var parameters = base.CreateParams;
            parameters.ClassStyle |= CS_DROPSHADOW;
            parameters.ExStyle |= WS_EX_TOOLWINDOW;
            parameters.ExStyle &= ~WS_EX_APPWINDOW;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDwm();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (!Visible)
            return;
        Relayout();
        Position(_anchor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_rowFont == null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;
        e.Graphics.Clear(_theme.Back);
        PaintSurface(e.Graphics);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHover(HitTest(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_pressed < 0)
            SetHover(-1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;
        _pressed = HitTest(e.Location);
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;

        var pressed = _pressed;
        _pressed = -1;
        Capture = false;
        if (IsDisposed)
            return;

        if (pressed >= 0 && pressed == HitTest(e.Location))
            ActivateRow(_rows[pressed]);

        if (!IsDisposed)
            Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_scrollMax == 0)
            return;
        _scroll = Math.Clamp(_scroll - Math.Sign(e.Delta) * Px(48), 0, _scrollMax);
        SetHover(HitTest(e.Location));
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode != Keys.Escape)
            return;
        Hide();
        e.Handled = true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_suspendClose && Visible)
            Hide();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Dispose();
            _rowFont?.Dispose();
            _sectionFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    void ActivateRow(Row row)
    {
        try
        {
            switch (row.Kind)
            {
                case RowKind.Device:
                    AudioDevices.SetDefault(row.Id!);
                    DefaultsChanged?.Invoke();
                    var scroll = _scroll;
                    Relayout();
                    _scroll = Math.Min(scroll, _scrollMax);
                    break;
                case RowKind.Startup:
                    Startup.SetEnabled(!Startup.IsEnabled());
                    _toggleTarget = Startup.IsEnabled() ? 1f : 0f;
                    _anim.Start();
                    break;
                case RowKind.Exit:
                    ExitRequested?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            _suspendClose = true;
            try
            {
                MessageBox.Show(this, ex.Message, "Audio Switch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _suspendClose = false;
                if (!IsDisposed)
                    Activate();
            }
        }
    }

    void Relayout()
    {
        _scale = Math.Max(1f, DeviceDpi / 96f);
        EnsureFonts();

        var speakers = ReadDevices(AudioFlow.Render);
        var microphones = ReadDevices(AudioFlow.Capture);
        var working = Screen.FromPoint(_anchor).WorkingArea;
        var width = MeasureWidth(speakers, microphones, working.Width);
        _rows.Clear();

        var y = Px(10);
        y = PlaceDevices("Speakers", mic: false, speakers, y);
        y += Px(8);
        y = PlaceDevices("Microphones", mic: true, microphones, y);
        y += Px(8);

        var listHeight = y;
        var footerHeight = Px(8) + 1 + Px(8) + Px(44) + Px(36) + Px(8);
        var height = Math.Min(listHeight + footerHeight, Math.Max(Px(220), working.Height - Px(20)));
        _listViewHeight = height - footerHeight;
        _scrollMax = Math.Max(0, listHeight - _listViewHeight);
        _scroll = Math.Clamp(_scroll, 0, _scrollMax);
        _footerTop = _listViewHeight;

        var footerY = _footerTop + Px(8) + 1 + Px(8);
        _rows.Add(new Row(RowKind.Startup, "Start with Windows", null, false, footerY, Px(44), Footer: true));
        footerY += Px(44);
        _rows.Add(new Row(RowKind.Exit, "Exit", null, false, footerY, Px(36), Footer: true));
        ClientSize = new Size(width, height);
    }

    int MeasureWidth(DeviceGroup speakers, DeviceGroup microphones, int workingWidth)
    {
        var text = Measure("Start with Windows");
        text = Math.Max(text, Measure("Microphones"));
        foreach (var device in speakers.Devices.Concat(microphones.Devices))
            text = Math.Max(text, Measure(device.Name));
        if (speakers.Error != null)
            text = Math.Max(text, Measure(speakers.Error));
        if (microphones.Error != null)
            text = Math.Max(text, Measure(microphones.Error));

        var max = Math.Min(Px(MaxWidthDip), Math.Max(Px(220), workingWidth - Px(24)));
        return Math.Clamp(text + Px(96), Px(MinWidthDip), max);
    }

    int PlaceDevices(string title, bool mic, DeviceGroup group, int y)
    {
        _rows.Add(new Row(RowKind.Section, title, null, false, y, Px(26), Footer: false, Mic: mic));
        y += Px(26);

        if (group.Error != null)
        {
            _rows.Add(new Row(RowKind.Message, group.Error, null, false, y, Px(36), Footer: false));
            return y + Px(36);
        }

        if (group.Devices.Count == 0)
        {
            var empty = mic ? "No microphones" : "No speakers";
            _rows.Add(new Row(RowKind.Message, empty, null, false, y, Px(36), Footer: false));
            return y + Px(36);
        }

        foreach (var device in group.Devices)
        {
            _rows.Add(new Row(RowKind.Device, device.Name, device.Id, device.IsDefault, y, Px(36), Footer: false));
            y += Px(36);
        }

        return y;
    }

    void Position(Point cursor)
    {
        var area = Screen.FromPoint(cursor).WorkingArea;
        var x = cursor.X - Width + Px(28);
        var y = cursor.Y > area.Top + area.Height / 2 ? cursor.Y - Height - Px(10) : cursor.Y + Px(10);
        x = Math.Clamp(x, area.Left + Px(8), Math.Max(area.Left + Px(8), area.Right - Width - Px(8)));
        y = Math.Clamp(y, area.Top + Px(8), Math.Max(area.Top + Px(8), area.Bottom - Height - Px(8)));
        Location = new Point(x, y);
    }

    void PaintSurface(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var state = graphics.Save();
        graphics.SetClip(new Rectangle(0, 0, ClientSize.Width, _listViewHeight), CombineMode.Intersect);
        for (var i = 0; i < _rows.Count; i++)
        {
            if (!_rows[i].Footer)
                PaintRow(graphics, i);
        }

        PaintScrollbar(graphics);
        graphics.Restore(state);

        using var back = new SolidBrush(_theme.Back);
        graphics.FillRectangle(back, 0, _footerTop, ClientSize.Width, ClientSize.Height - _footerTop);
        using var line = new Pen(_theme.Border);
        var lineY = _footerTop + Px(8);
        graphics.DrawLine(line, Px(16), lineY, ClientSize.Width - Px(16), lineY);

        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Footer)
                PaintRow(graphics, i);
        }

        using var border = new Pen(_theme.Border);
        using var outline = RoundRect(new RectangleF(0.5f, 0.5f, ClientSize.Width - 1f, ClientSize.Height - 1f), Px(8));
        graphics.DrawPath(border, outline);
    }

    void PaintRow(Graphics graphics, int index)
    {
        var row = _rows[index];
        var bounds = ViewBounds(row);
        if (bounds.Height <= 0 || bounds.Bottom < 0)
            return;

        var box = new Rectangle(Px(8), bounds.Y + Px(2), ClientSize.Width - Px(16), bounds.Height - Px(4));
        var hot = index == _hover || index == _pressed;
        if (row.Kind is RowKind.Device or RowKind.Startup or RowKind.Exit)
        {
            Color? fill = row.Selected ? _theme.Selected : null;
            if (hot)
                fill = row.Selected ? Theme.Blend(_theme.Selected, _theme.Accent, 0.08f) : _theme.Hover;
            if (fill.HasValue)
                FillRound(graphics, box, Px(8), fill.Value);
        }

        if (row.Kind == RowKind.Section)
        {
            var glyph = new RectangleF(Px(16), bounds.Y + (bounds.Height - Px(15)) / 2f, Px(15), Px(15));
            if (row.Mic)
                DrawMic(graphics, glyph, _theme.Muted);
            else
                DrawSpeaker(graphics, glyph, _theme.Muted);
            DrawText(graphics, row.Text, _sectionFont!, new Rectangle(Px(38), bounds.Y, ClientSize.Width - Px(52), bounds.Height), _theme.Muted);
            return;
        }

        var color = row.Kind switch
        {
            RowKind.Message => _theme.Muted,
            RowKind.Exit => hot ? _theme.Text : _theme.Muted,
            _ => _theme.Text,
        };
        var textLeft = Px(38);
        var textWidth = ClientSize.Width - textLeft - (row.Kind == RowKind.Message ? Px(16) : Px(46));
        DrawText(graphics, row.Text, _rowFont!, new Rectangle(textLeft, bounds.Y, textWidth, bounds.Height), color);

        if (row.Selected)
        {
            var check = new Rectangle(ClientSize.Width - Px(30), bounds.Y + (bounds.Height - Px(14)) / 2, Px(14), Px(14));
            DrawCheck(graphics, check, _theme.AccentInk);
        }
        else if (row.Kind == RowKind.Startup)
        {
            var toggle = new RectangleF(ClientSize.Width - Px(58), bounds.Y + (bounds.Height - Px(22)) / 2f, Px(40), Px(22));
            DrawToggle(graphics, toggle, _toggle);
        }
    }

    void PaintScrollbar(Graphics graphics)
    {
        if (_scrollMax <= 0 || _listViewHeight <= 0)
            return;

        var content = _listViewHeight + _scrollMax;
        var thumbHeight = Math.Max(Px(28), _listViewHeight * _listViewHeight / content);
        var travel = Math.Max(1, _listViewHeight - thumbHeight);
        var thumbY = travel * (_scroll / (float)_scrollMax);
        var thumb = new RectangleF(ClientSize.Width - Px(7), thumbY + Px(6), Px(3), Math.Max(Px(12), thumbHeight - Px(12)));
        using var brush = new SolidBrush(Color.FromArgb(_theme.Dark ? 90 : 70, _theme.Text));
        FillRound(graphics, thumb, thumb.Width / 2f, brush);
    }

    void DrawToggle(Graphics graphics, RectangleF track, float amount)
    {
        var color = Theme.Blend(_theme.TrackOff, _theme.Accent, amount);
        FillRound(graphics, track, track.Height / 2f, color);
        var knob = track.Height - Px(6);
        var x = track.X + Px(3) + (track.Width - knob - Px(6)) * amount;
        var y = track.Y + Px(3);
        using var brush = new SolidBrush(_theme.Knob);
        graphics.FillEllipse(brush, x, y, knob, knob);
        using var pen = new Pen(Color.FromArgb(40, Color.Black));
        graphics.DrawEllipse(pen, x, y, knob, knob);
    }

    static void DrawText(Graphics graphics, string text, Font font, Rectangle bounds, Color color)
    {
        TextRenderer.DrawText(
            graphics,
            text,
            font,
            bounds,
            color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    static void DrawCheck(Graphics graphics, Rectangle bounds, Color color)
    {
        using var pen = new Pen(color, Math.Max(1.6f, bounds.Width / 7f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        graphics.DrawLines(pen,
        [
            new PointF(bounds.X + bounds.Width * 0.16f, bounds.Y + bounds.Height * 0.52f),
            new PointF(bounds.X + bounds.Width * 0.40f, bounds.Y + bounds.Height * 0.76f),
            new PointF(bounds.X + bounds.Width * 0.84f, bounds.Y + bounds.Height * 0.26f),
        ]);
    }

    internal static void DrawSpeaker(Graphics graphics, RectangleF box, Color color)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var unit = box.Width / 16f;
        using var brush = new SolidBrush(color);
        graphics.FillRectangle(brush, box.X + unit, box.Y + 5f * unit, 4.2f * unit, 6f * unit);
        graphics.FillPolygon(brush,
        [
            new PointF(box.X + 5.2f * unit, box.Y + 5f * unit),
            new PointF(box.X + 11f * unit, box.Y + 1.4f * unit),
            new PointF(box.X + 11f * unit, box.Y + 14.6f * unit),
            new PointF(box.X + 5.2f * unit, box.Y + 11f * unit),
        ]);
        using var pen = new Pen(color, Math.Max(1.25f, 1.45f * unit))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawArc(pen, box.X + 11f * unit, box.Y + 4f * unit, 4.2f * unit, 8f * unit, -55, 110);
    }

    static void DrawMic(Graphics graphics, RectangleF box, Color color)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var unit = box.Width / 16f;
        using var brush = new SolidBrush(color);
        using (var head = RoundRect(new RectangleF(box.X + 5f * unit, box.Y + unit, 6f * unit, 8.5f * unit), 3f * unit))
            graphics.FillPath(brush, head);
        using var pen = new Pen(color, Math.Max(1.2f, 1.35f * unit))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        graphics.DrawArc(pen, box.X + 3.1f * unit, box.Y + 5.2f * unit, 9.8f * unit, 6.6f * unit, 15, 150);
        graphics.DrawLine(pen, box.X + 8f * unit, box.Y + 12.1f * unit, box.X + 8f * unit, box.Y + 14f * unit);
        graphics.DrawLine(pen, box.X + 5.3f * unit, box.Y + 14.1f * unit, box.X + 10.7f * unit, box.Y + 14.1f * unit);
    }

    int HitTest(Point point)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (row.Kind is not (RowKind.Device or RowKind.Startup or RowKind.Exit))
                continue;
            if (ViewBounds(row).Contains(point))
                return i;
        }

        return -1;
    }

    Rectangle ViewBounds(Row row)
    {
        if (row.Footer)
            return new Rectangle(0, row.Y, ClientSize.Width, row.Height);

        var visible = new Rectangle(0, 0, ClientSize.Width, _listViewHeight);
        var bounds = new Rectangle(0, row.Y - _scroll, ClientSize.Width, row.Height);
        return Rectangle.Intersect(bounds, visible);
    }

    void SetHover(int index)
    {
        if (_hover == index)
            return;
        _hover = index;
        var clickable = index >= 0;
        Cursor = clickable ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    void Animate()
    {
        var delta = _toggleTarget - _toggle;
        if (Math.Abs(delta) < 0.02f)
        {
            _toggle = _toggleTarget;
            _anim.Stop();
        }
        else
        {
            _toggle += delta * 0.42f;
        }

        Invalidate();
    }

    void EnsureFonts()
    {
        if (_rowFont != null && Math.Abs(_fontScale - _scale) < 0.01f)
            return;

        _rowFont?.Dispose();
        _sectionFont?.Dispose();
        _fontScale = _scale;
        _rowFont = new Font("Segoe UI", 14f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _sectionFont = new Font("Segoe UI", 12f * _scale, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    int Measure(string text)
    {
        EnsureFonts();
        return TextRenderer.MeasureText(text, _rowFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
    }

    int Px(int dip) => (int)Math.Round(dip * _scale);

    void ApplyDwm()
    {
        if (!IsHandleCreated)
            return;

        var round = 2;
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        var dark = _theme.Dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        var border = ColorTranslator.ToWin32(_theme.Border);
        DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
    }

    static DeviceGroup ReadDevices(AudioFlow flow)
    {
        try
        {
            return new DeviceGroup(AudioDevices.List(flow).ToList(), null);
        }
        catch (Exception ex)
        {
            return new DeviceGroup([], ex.Message);
        }
    }

    static void FillRound(Graphics graphics, RectangleF rectangle, float radius, Color color)
    {
        using var brush = new SolidBrush(color);
        FillRound(graphics, rectangle, radius, brush);
    }

    internal static void FillRound(Graphics graphics, RectangleF rectangle, float radius, Brush brush)
    {
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
            return;
        using var path = RoundRect(rectangle, radius);
        graphics.FillPath(brush, path);
    }

    static GraphicsPath RoundRect(RectangleF rectangle, float radius)
    {
        radius = Math.Min(radius, Math.Min(rectangle.Width, rectangle.Height) / 2f);
        var diameter = radius * 2f;
        var path = new GraphicsPath();
        if (radius <= 0.1f)
        {
            path.AddRectangle(rectangle);
            return path;
        }

        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    enum RowKind
    {
        Section,
        Device,
        Message,
        Startup,
        Exit,
    }

    sealed record Row(RowKind Kind, string Text, string? Id, bool Selected, int Y, int Height, bool Footer, bool Mic = false);

    sealed record DeviceGroup(List<AudioEndpoint> Devices, string? Error);
}
