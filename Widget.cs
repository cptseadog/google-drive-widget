using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

static class WidgetProgram
{
    public static readonly uint ShowMessage = RegisterWindowMessage("Dev.GoogleDriveWidget.Show.v1");
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string text);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [STAThread] static void Main(string[] args)
    {
        string preview = null, render = null, previewTheme = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--preview" && i + 1 < args.Length) preview = args[++i];
            else if (args[i] == "--render" && i + 1 < args.Length) render = args[++i];
            else if (args[i] == "--theme" && i + 1 < args.Length) previewTheme = args[++i];
        }
        if (render != null && preview == null) return;
        bool first;
        using (var mutex = new Mutex(true, preview == null ? "Local\\Dev.GoogleDriveWidget.v1" : "Local\\Dev.GoogleDriveWidget.Preview." + Process.GetCurrentProcess().Id, out first))
        {
            if (!first) { PostMessage(new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero); return; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                using (var window = new DriveWidget(preview, preview == null ? null : previewTheme))
                {
                    if (render != null) window.RenderPreview(render);
                    else Application.Run(window);
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Google Drive widget", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
}
// Follow the Windows app appearance preference; never change the user's OS setting.
sealed class WidgetTheme
{
    public readonly bool Dark;
    public readonly Color Surface, Ink, Secondary, Accent, Green, Warning, Border, Divider, Hover, Pressed, Tile, Track, Disabled;
    public WidgetTheme(bool dark)
    {
        Dark = dark;
        Surface = dark ? Color.FromArgb(28, 28, 30) : Color.White;
        Ink = dark ? Color.FromArgb(245, 245, 247) : Color.FromArgb(28, 28, 30);
        Secondary = dark ? Color.FromArgb(174, 174, 182) : Color.FromArgb(125, 125, 132);
        Accent = dark ? Color.FromArgb(100, 174, 255) : Color.FromArgb(0, 122, 255);
        Green = dark ? Color.FromArgb(48, 209, 88) : Color.FromArgb(43, 157, 76);
        Warning = dark ? Color.FromArgb(255, 190, 80) : Color.FromArgb(173, 103, 13);
        Border = dark ? Color.FromArgb(64, 64, 69) : Color.FromArgb(226, 228, 233);
        Divider = dark ? Color.FromArgb(52, 52, 56) : Color.FromArgb(239, 240, 243);
        Hover = dark ? Color.FromArgb(44, 44, 48) : Color.FromArgb(245, 247, 250);
        Pressed = dark ? Color.FromArgb(58, 58, 63) : Color.FromArgb(235, 239, 244);
        Tile = dark ? Color.FromArgb(34, 47, 66) : Color.FromArgb(236, 244, 255);
        Track = dark ? Color.FromArgb(75, 75, 82) : Color.FromArgb(220, 222, 226);
        Disabled = dark ? Color.FromArgb(142, 142, 150) : Color.FromArgb(179, 182, 189);
    }
    public static bool ReadDark()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
        }
        catch (System.Security.SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }
}
sealed class ThemeMenuColors : ProfessionalColorTable
{
    readonly WidgetTheme theme;
    public ThemeMenuColors(WidgetTheme value) { theme = value; UseSystemColors = false; }
    public override Color ToolStripDropDownBackground { get { return theme.Surface; } }
    public override Color ImageMarginGradientBegin { get { return theme.Surface; } }
    public override Color ImageMarginGradientMiddle { get { return theme.Surface; } }
    public override Color ImageMarginGradientEnd { get { return theme.Surface; } }
    public override Color MenuBorder { get { return theme.Border; } }
    public override Color MenuItemBorder { get { return theme.Hover; } }
    public override Color MenuItemSelected { get { return theme.Hover; } }
    public override Color MenuItemSelectedGradientBegin { get { return theme.Hover; } }
    public override Color MenuItemSelectedGradientEnd { get { return theme.Hover; } }
    public override Color CheckBackground { get { return theme.Pressed; } }
    public override Color CheckSelectedBackground { get { return theme.Pressed; } }
    public override Color CheckPressedBackground { get { return theme.Pressed; } }
    public override Color SeparatorDark { get { return theme.Divider; } }
    public override Color SeparatorLight { get { return theme.Surface; } }
}
sealed class ThemeButton : Button
{
    public Color DisabledInk = Color.Gray;
    public bool QuietChrome, DrawDots, DrawHideBar;
    bool hovered, pressed;
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; base.OnLostFocus(e); Invalidate(); }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (DrawDots && Width > 0 && Height > 0)
        {
            using (var circle = new GraphicsPath())
            {
                circle.AddEllipse(ClientRectangle);
                Region old = Region; Region = new Region(circle);
                if (old != null) old.Dispose();
            }
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (QuietChrome)
        {
            Graphics g = e.Graphics; g.Clear(BackColor); g.SmoothingMode = SmoothingMode.AntiAlias;
            bool highlighted = hovered || pressed || (Focused && ShowFocusCues);
            if ((!DrawDots && !DrawHideBar) || highlighted)
            {
                Color fill = pressed ? FlatAppearance.MouseDownBackColor : FlatAppearance.MouseOverBackColor;
                float radius = (DrawDots || DrawHideBar) ? Height / 2f : Height / 3f;
                using (var shape = CardDrawing.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), radius - 0.5f))
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, shape);
            }
            if (DrawHideBar)
            {
                using (var pen = new Pen(ForeColor, Math.Max(2, Height / 6f)))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, Width * 0.25f, Height / 2f, Width * 0.75f, Height / 2f);
                }
            }
            else if (DrawDots)
            {
                float size = Math.Max(2, Height / 9f);
                using (var brush = new SolidBrush(ForeColor))
                    for (int i = -1; i <= 1; i++) g.FillEllipse(brush, Width / 2f + i * Width / 4f - size / 2, Height / 2f - size / 2, size, size);
            }
            else
            {
                TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                if (Focused && ShowFocusCues)
                    using (var pen = new Pen(ForeColor, 1)) g.DrawLine(pen, Width * 0.4f, Height - 4, Width * 0.6f, Height - 4);
            }
            return;
        }
        if (Enabled) { base.OnPaint(e); return; }
        // WinForms' default disabled text becomes black on a dark flat button.
        e.Graphics.Clear(BackColor);
        TextRenderer.DrawText(e.Graphics, Text, Font, Rectangle.Inflate(ClientRectangle, -5, 0), DisabledInk,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}
static class CardDrawing
{
    public static GraphicsPath Round(RectangleF rect, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure(); return path;
    }
}
// A fully drawn control avoids native checkbox chrome, while exposing checkbox semantics.
sealed class IosSwitch : Control
{
    public WidgetTheme Theme = new WidgetTheme(false);
    readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer();
    float position, target, rotation;
    bool busy, uncertain;
    public bool Checked { get; private set; }
    public IosSwitch()
    {
        BackColor = Color.White;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        AutoSize = false; Text = ""; TabStop = true;
        AccessibleName = "Google Drive desktop access"; AccessibleRole = AccessibleRole.CheckButton;
        Cursor = Cursors.Hand;
        animation.Interval = 16;
        animation.Tick += delegate
        {
            position += (target - position) * 0.25f;
            if (Math.Abs(position - target) < 0.01f) position = target;
            rotation = (rotation + 7) % 360; Invalidate();
            if (!busy && position == target) animation.Stop();
        };
    }
    public void SetVisual(bool on, bool working, bool unknown, bool interactive)
    {
        bool changed = Checked != on;
        Checked = on; target = on ? 1 : 0; busy = working; uncertain = unknown;
        Enabled = interactive; Cursor = interactive ? Cursors.Hand : Cursors.Default;
        AccessibleDescription = working ? "Please wait for Drive." : unknown ? "Drive status is unavailable." : on ? "On. Activate to disconnect Drive." : "Off. Activate to mount Drive.";
        if (changed) AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        animation.Start(); Invalidate();
    }
    public void FinishAnimation() { position = target; rotation = 45; if (!busy) animation.Stop(); Invalidate(); }
    protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(BackColor); }
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float pad = Math.Max(2, Height / 17f), trackH = Height - pad * 2;
        var track = new RectangleF(pad, pad, Width - 2 * pad, trackH);
        Color tint = busy ? Color.FromArgb(255, 177, 41) : Checked ? (Theme.Dark ? Color.FromArgb(48, 209, 88) : Color.FromArgb(52, 199, 89)) : Theme.Track;
        using (var shape = CardDrawing.Round(track, trackH / 2))
        using (var brush = new SolidBrush(tint)) g.FillPath(brush, shape);
        float inset = Math.Max(2, trackH / 14f), size = trackH - 2 * inset;
        float x = track.Left + inset + (track.Width - size - 2 * inset) * position;
        var knob = new RectangleF(x, track.Top + inset, size, size);
        using (var shadow = new SolidBrush(Color.FromArgb(28, 0, 0, 0))) g.FillEllipse(shadow, knob.X, knob.Y + 1.5f, size, size);
        using (var white = new SolidBrush(Color.White)) g.FillEllipse(white, knob);
        if (busy)
        {
            float margin = size * 0.29f;
            using (var pen = new Pen(Color.FromArgb(164, 112, 26), Math.Max(1.5f, size * 0.07f)))
            { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; g.DrawArc(pen, knob.X + margin, knob.Y + margin, size - margin * 2, size - margin * 2, rotation, 250); }
        }
        else if (uncertain)
        {
            using (var pen = new Pen(Color.FromArgb(165, 169, 177), Math.Max(1.5f, size * 0.07f)))
                g.DrawLine(pen, knob.X + size * 0.34f, knob.Y + size / 2, knob.X + size * 0.66f, knob.Y + size / 2);
        }
        if (Focused && ShowFocusCues)
        {
            using (var outline = CardDrawing.Round(new RectangleF(0.8f, 0.8f, Width - 1.6f, Height - 1.6f), Height / 2))
            using (var pen = new Pen(Theme.Accent, 1.4f)) g.DrawPath(pen, outline);
        }
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { if (Enabled) Focus(); base.OnMouseDown(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)) { OnClick(EventArgs.Empty); e.Handled = true; e.SuppressKeyPress = true; }
        base.OnKeyDown(e);
    }
    protected override AccessibleObject CreateAccessibilityInstance() { return new SwitchAccessibility(this); }
    sealed class SwitchAccessibility : ControlAccessibleObject
    {
        readonly IosSwitch owner;
        public SwitchAccessibility(IosSwitch control) : base(control) { owner = control; }
        public override AccessibleRole Role { get { return AccessibleRole.CheckButton; } }
        public override AccessibleStates State { get { return base.State | (owner.Checked ? AccessibleStates.Checked : AccessibleStates.None); } }
        public override string DefaultAction { get { return "Toggle"; } }
        public override void DoDefaultAction() { if (owner.Enabled) owner.OnClick(EventArgs.Empty); }
    }
    protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
}
sealed class DriveGlyph : Control
{
    public WidgetTheme Theme = new WidgetTheme(false);
    public DriveGlyph() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        WidgetLogo.Draw(e.Graphics, ClientRectangle);
    }
}
sealed class DriveWidget : Form
{
    readonly MountController controller;
    readonly Label nameLabel = new Label(), statusLabel = new Label(), rowLabel = new Label(), hintLabel = new Label(), previewLabel = new Label();
    readonly DriveGlyph glyph = new DriveGlyph();
    readonly IosSwitch toggle = new IosSwitch();
    readonly ThemeButton folder = new ThemeButton(), secondary = new ThemeButton(), more = new ThemeButton(), hideBar = new ThemeButton();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly ToolTip tips = new ToolTip();
    readonly string root, positionFile, preview;
    readonly Font titleFont = new Font("Segoe UI Semibold", 12.5f), rowFont = new Font("Segoe UI Semibold", 10.5f), textFont = new Font("Segoe UI", 9f), actionFont = new Font("Segoe UI", 10f), menuFont = new Font("Segoe UI", 15f);
    WidgetTheme theme;
    readonly string previewTheme;
    string visualState = "Checking";
    bool visualBusy, themeReady;
    Color ink { get { return theme.Ink; } }
    Color secondaryInk { get { return theme.Secondary; } }
    Color blue { get { return theme.Accent; } }
    Color green { get { return theme.Green; } }
    MountSnapshot snapshot;
    DateTime? stopRequested, startRequested;
    bool quitSent, refreshing, exiting, setupPending, layoutReady, toggleQueued;
    DateTime quitSentAt;
    string operationError, statusDetails, trayState;
    float scale = 1;

    public DriveWidget(string previewState, string themeOverride = null)
    {
        previewTheme = previewState == null ? null : themeOverride;
        theme = new WidgetTheme(previewTheme == "dark" || (previewTheme != "light" && WidgetTheme.ReadDark()));
        root = AppDomain.CurrentDomain.BaseDirectory; preview = previewState;
        positionFile = Path.Combine(root, "position.json");
        if (preview == null) controller = new MountController(root);
        setupPending = preview == null && !InstallComplete();
        AutoScaleMode = AutoScaleMode.None; Font = textFont;
        Text = preview == null ? "Google Drive" : "Google Drive — design preview";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; DoubleBuffered = true;
        BackColor = theme.Surface; ClientSize = new Size(320, 202);
        StartPosition = FormStartPosition.Manual; Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        nameLabel.Text = "Google Drive"; nameLabel.Font = titleFont; nameLabel.ForeColor = ink;
        statusLabel.Font = textFont; statusLabel.ForeColor = secondaryInk;
        rowLabel.Text = "Desktop access"; rowLabel.Font = rowFont; rowLabel.ForeColor = ink;
        hintLabel.Font = textFont; hintLabel.ForeColor = secondaryInk;
        previewLabel.Font = textFont; previewLabel.ForeColor = secondaryInk; previewLabel.Text = "Preview"; previewLabel.Visible = preview != null;
        foreach (var label in new[] { nameLabel, statusLabel, rowLabel, hintLabel, previewLabel }) { label.AutoSize = false; label.AutoEllipsis = true; label.UseMnemonic = false; label.TextAlign = ContentAlignment.MiddleLeft; }
        SetupLink(folder, "Open folder  ↗"); SetupLink(secondary, "Retry"); SetupLink(more, "⋯");
        more.QuietChrome = true; more.DrawDots = true;
        SetupLink(hideBar, ""); hideBar.QuietChrome = true; hideBar.DrawHideBar = true; hideBar.AccessibleName = "Hide widget";
        hideBar.Click += delegate { SavePosition(); Hide(); };
        tips.SetToolTip(hideBar, "Hide widget. Click the tray icon to show it again.");
        more.Font = menuFont; more.TextAlign = ContentAlignment.MiddleCenter; more.AccessibleName = "Widget menu";
        folder.AccessibleName = "Open Google Drive folder";
        glyph.TabStop = false;
        toggle.TabIndex = 0; folder.TabIndex = 1; secondary.TabIndex = 2; hideBar.TabIndex = 3; more.TabIndex = 4;
        Controls.AddRange(new Control[] { glyph, nameLabel, statusLabel, rowLabel, hintLabel, toggle, folder, secondary, more, hideBar, previewLabel });
        toggle.Click += async delegate { await Toggle(); };
        folder.Click += delegate
        {
            if (preview != null) return;
            if (snapshot != null && snapshot.State == "On" && !stopRequested.HasValue && !quitSent)
                Process.Start("explorer.exe", "\"" + controller.MountPath + "\"");
        };
        secondary.Click += async delegate
        {
            if (preview != null) return;
            if (setupPending) { ShowDetails(); return; }
            if (refreshing || quitSent) return;
            stopRequested = null; operationError = null; await RefreshState();
        };
        menu.Items.Add("Keep on top", null, delegate(object s, EventArgs e) { TopMost = !TopMost; ((ToolStripMenuItem)s).Checked = TopMost; });
        menu.Items.Add("Status details", null, delegate { ShowDetails(); });
        menu.Items.Add("Help", null, delegate { Process.Start(Path.Combine(root, "README.txt")); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit widget", null, delegate { exiting = true; Close(); });
        more.Click += delegate { menu.Show(more, new Point(more.Width, 0), ToolStripDropDownDirection.AboveLeft); };
        if (preview == null)
        {
            tray.ContextMenuStrip = new ContextMenuStrip();
            tray.ContextMenuStrip.Items.Add("Show widget", null, delegate { Reveal(); });
            tray.ContextMenuStrip.Items.Add("Quit widget", null, delegate { exiting = true; Close(); });
            tray.Icon = MakeIcon(secondaryInk); tray.Visible = true; tray.Text = "Google Drive — checking";
            tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Reveal(); };
        }
        foreach (Control header in new Control[] { glyph, nameLabel, statusLabel }) header.MouseDown += DragHeader;
        timer.Interval = 3000; timer.Tick += async delegate { RefreshTheme(); if (preview == null) await RefreshState(); };
        Shown += async delegate
        {
            RefreshTheme();
            if (preview != null) ApplyPreview(preview);
            else await RefreshState();
            timer.Start();
        };
        FormClosing += delegate(object sender, FormClosingEventArgs e)
        {
            SavePosition();
            if (preview == null && !exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        tips.OwnerDraw = true;
        tips.Draw += delegate(object sender, DrawToolTipEventArgs e)
        {
            using (var brush = new SolidBrush(theme.Surface)) e.Graphics.FillRectangle(brush, e.Bounds);
            using (var pen = new Pen(theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, e.Font, Rectangle.Inflate(e.Bounds, -4, -2), theme.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        themeReady = true; ApplyTheme();
        ApplyVisual("Checking", "Checking Drive…", "One moment", false, true, false);
    }
    void SetupLink(Button button, string text)
    {
        button.Text = text; button.Font = actionFont; button.ForeColor = blue; button.BackColor = theme.Surface;
        button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = theme.Hover;
        button.FlatAppearance.MouseDownBackColor = theme.Pressed;
        button.TextAlign = ContentAlignment.MiddleLeft; button.Cursor = Cursors.Hand; button.UseVisualStyleBackColor = false;
    }
    void RefreshTheme()
    {
        if (!themeReady || IsDisposed) return;
        bool dark = previewTheme == "dark" || (previewTheme != "light" && WidgetTheme.ReadDark());
        if (dark == theme.Dark) return;
        theme = new WidgetTheme(dark); ApplyTheme();
    }
    void ThemeMenu(ContextMenuStrip target)
    {
        if (target == null) return;
        target.BackColor = theme.Surface; target.ForeColor = theme.Ink;
        target.Renderer = new ToolStripProfessionalRenderer(new ThemeMenuColors(theme));
        foreach (ToolStripItem item in target.Items) item.ForeColor = theme.Ink;
    }
    void ApplyTheme()
    {
        BackColor = theme.Surface; ForeColor = ink;
        foreach (var label in new[] { nameLabel, statusLabel, rowLabel, hintLabel, previewLabel }) label.BackColor = theme.Surface;
        nameLabel.ForeColor = rowLabel.ForeColor = ink;
        hintLabel.ForeColor = previewLabel.ForeColor = secondaryInk;
        foreach (var button in new[] { folder, secondary, more, hideBar })
        {
            button.BackColor = theme.Surface; button.ForeColor = blue;
            button.DisabledInk = theme.Disabled;
            button.FlatAppearance.MouseOverBackColor = theme.Hover;
            button.FlatAppearance.MouseDownBackColor = theme.Pressed;
        }
        hideBar.ForeColor = secondaryInk;
        toggle.Theme = theme; toggle.BackColor = theme.Surface;
        glyph.Theme = theme; glyph.BackColor = theme.Surface;
        ThemeMenu(menu); ThemeMenu(tray.ContextMenuStrip);
        tips.BackColor = theme.Surface; tips.ForeColor = theme.Ink;
        statusLabel.ForeColor = visualState == "On" ? green : visualState == "Error" ? theme.Warning : secondaryInk;
        folder.ForeColor = folder.Enabled ? blue : theme.Disabled;
        if (preview == null)
        {
            var previous = tray.Icon;
            tray.Icon = MakeIcon(visualState == "On" ? green : visualBusy ? Color.FromArgb(226, 160, 34) : secondaryInk);
            if (previous != null) previous.Dispose();
        }
        ApplyNativeFrame();
        Invalidate(true);
    }
    bool InstallComplete()
    {
        try
        {
            var values = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(root, "installation-status.json"))) as System.Collections.Generic.IDictionary<string, object>;
            return values != null && values.ContainsKey("State") && (string)values["State"] == "Installed";
        }
        catch { return false; }
    }
    int Px(float logical) { return (int)Math.Round(logical * scale); }
    void Place(Control c, int x, int y, int width, int height) { c.SetBounds(Px(x), Px(y), Px(width), Px(height)); }
    void LayoutCard()
    {
        using (Graphics graphics = CreateGraphics()) scale = graphics.DpiX / 96f;
        ClientSize = new Size(Px(320), Px(202));
        Place(glyph, 20, 20, 36, 36); Place(nameLabel, 68, 17, 198, 25); Place(statusLabel, 68, 41, 200, 20);
        Place(more, 282, 162, 20, 20); Place(rowLabel, 20, 86, 210, 24); Place(hintLabel, 20, 111, 210, 22);
        Place(toggle, 242, 90, 60, 36); Place(folder, 15, 157, 175, 30); Place(secondary, 192, 157, 80, 30); Place(previewLabel, 210, 160, 60, 24);
        Place(hideBar, 278, 6, 28, 14);
        using (var shape = CardDrawing.Round(new RectangleF(0, 0, Width, Height), Px(23)))
        { Region old = Region; Region = new Region(shape); if (old != null) old.Dispose(); }
        layoutReady = true;
    }
    protected override void OnLoad(EventArgs e)
    {
        LayoutCard();
        var area = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(area.Right - Width - Px(24), area.Bottom - Height - Px(28));
        if (preview == null)
        {
            try
            {
                int[] xy = new JavaScriptSerializer().Deserialize<int[]>(File.ReadAllText(positionFile));
                var candidate = new Rectangle(xy[0], xy[1], Width, Height);
                foreach (Screen screen in Screen.AllScreens) if (screen.WorkingArea.Contains(candidate)) { Location = candidate.Location; break; }
            } catch { }
        }
        base.OnLoad(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); if (!layoutReady) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // Keep the antialiased stroke inside the hard window-region clip. Drawing
        // on the clip boundary cuts individual pixels out of the rounded outline.
        const float inset = 1.5f;
        using (var edge = CardDrawing.Round(new RectangleF(inset, inset, Width - 2 * inset, Height - 2 * inset), Px(23) - inset))
        using (var pen = new Pen(theme.Border, 1)) e.Graphics.DrawPath(pen, edge);
        using (var pen = new Pen(theme.Divider, Math.Max(1, scale))) e.Graphics.DrawLine(pen, Px(20), Px(146), Width - Px(20), Px(146));
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    void ApplyNativeFrame()
    {
        if (!IsHandleCreated || theme == null) return;
        // We draw the entire card outline. Suppress Windows' extra light frame,
        // which can otherwise leave bright pixels around a custom window region.
        // Unsupported attributes return an HRESULT; older Windows keeps our outline.
        int noBorder = unchecked((int)0xFFFFFFFE), dark = theme.Dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 34, ref noBorder, sizeof(int)); // DWMWA_BORDER_COLOR
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyNativeFrame();
    }
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    void DragHeader(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); SavePosition(); } }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Y < Px(75)) DragHeader(this, e); base.OnMouseDown(e); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WidgetProgram.ShowMessage) Reveal();
        base.WndProc(ref m);
        // Windows broadcasts appearance changes; the timer also covers missed messages.
        if (m.Msg == 0x001A || m.Msg == 0x031A || m.Msg == 0x0015) RefreshTheme();
    }
    void Reveal() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    void SavePosition() { if (preview != null) return; try { File.WriteAllText(positionFile, new JavaScriptSerializer().Serialize(new int[] { Left, Top })); } catch { } }
    void ShowDetails() { MessageBox.Show(this, statusDetails ?? "Checking Google Drive status.", "Google Drive", MessageBoxButtons.OK, MessageBoxIcon.Information); }

    async Task Toggle()
    {
        if (preview != null) { ApplyPreview(toggle.Checked ? "off" : "on"); return; }
        if (quitSent || setupPending) return;
        if (refreshing) { toggleQueued = true; return; }
        operationError = null;
        if (snapshot != null && snapshot.State == "On") { stopRequested = DateTime.UtcNow; await RefreshState(); return; }
        if (snapshot != null && snapshot.State == "Off")
        {
            refreshing = true; ApplyVisual("Starting", "Connecting…", "Making files available", false, true, false);
            try { await Task.Run((Action)controller.Start); startRequested = DateTime.UtcNow; }
            catch (Exception ex) { operationError = ex.Message; }
            finally { refreshing = false; }
        }
        await RefreshState();
    }
    async Task RefreshState()
    {
        if (refreshing || IsDisposed) return; refreshing = true;
        if (stopRequested.HasValue) secondary.Enabled = false;
        try
        {
            if (setupPending)
            {
                setupPending = !InstallComplete();
                if (setupPending)
                {
                    statusDetails = "The widget is ready. The one-time administrator installation is still pending. Your existing Drive mount has not been changed.";
                    ApplyVisual("Setup", "Setup required", "One-time installation needed", false, false, false);
                    return;
                }
            }
            MountSnapshot next = await Task.Run(() => controller.GetStatus());
            if (IsDisposed) return;
            snapshot = next; statusDetails = next.Detail;
            if (next.State == "On") startRequested = null;
            string activity = next.Detail;
            if (quitSent)
            {
                if (next.State == "Off") { quitSent = false; stopRequested = null; }
                else if ((DateTime.UtcNow - quitSentAt).TotalSeconds > 30) { quitSent = false; stopRequested = null; operationError = "Drive has not finished stopping. Close open files and retry."; }
            }
            else if (stopRequested.HasValue)
            {
                if (next.State == "Off") stopRequested = null;
                else if (next.State == "On")
                {
                    DateTime requested = stopRequested.Value;
                    activity = await Task.Run(() => controller.TryStop(requested));
                    if (activity == "Stopping") { quitSent = true; quitSentAt = DateTime.UtcNow; }
                    else if (activity == "Off")
                    {
                        stopRequested = null;
                        next = new MountSnapshot { State = "Off", Detail = "Google Drive is not mounted.", UploadsQueued = 0, UploadsInProgress = 0 };
                        snapshot = next;
                    }
                }
                else { stopRequested = null; operationError = "File activity could not be verified. Drive was not forcibly disconnected."; }
            }
            if (startRequested.HasValue && (DateTime.UtcNow - startRequested.Value).TotalSeconds > 45)
            { startRequested = null; operationError = "Drive did not become ready. Check your connection and retry."; }
            statusDetails = operationError ?? activity;
            if (operationError != null) ApplyVisual("Error", "Needs attention", "Open details or try again", next.State == "On", false, false);
            else if (quitSent) ApplyVisual("Stopping", "Disconnecting…", "Finishing safely", true, true, false);
            else if (stopRequested.HasValue) ApplyVisual("Waiting", "Finishing file activity…", "Close files; uploads will finish", true, true, false);
            else if (startRequested.HasValue || next.State == "Starting") ApplyVisual("Starting", "Connecting…", "Making files available", false, true, false);
            else if (next.State == "On") ApplyVisual("On", "Connected", "Close files before turning off", true, false, true);
            else if (next.State == "Off") ApplyVisual("Off", "Not mounted", "Off until you need it", false, false, true);
            else ApplyVisual("Error", "Status unavailable", "Open details or try again", false, false, false);
        }
        catch (Exception ex)
        {
            stopRequested = null; quitSent = false; startRequested = null;
            operationError = ex.Message; statusDetails = ex.Message;
            ApplyVisual("Error", "Needs attention", "Open details or try again", snapshot != null && snapshot.State == "On", false, false);
        }
        finally
        {
            refreshing = false;
            if (!IsDisposed)
            {
                secondary.Enabled = true;
                if (toggleQueued) { toggleQueued = false; BeginInvoke((Action)(async delegate { await Toggle(); })); }
            }
        }
    }
    void ApplyVisual(string state, string status, string hint, bool on, bool busy, bool interactive)
    {
        visualState = state; visualBusy = busy;
        statusLabel.Text = status; statusLabel.ForeColor = state == "On" ? green : state == "Error" ? theme.Warning : secondaryInk;
        hintLabel.Text = hint;
        toggle.SetVisual(on, busy, state == "Error" || state == "Setup", interactive);
        folder.Enabled = state == "On"; folder.ForeColor = folder.Enabled ? blue : theme.Disabled;
        secondary.Visible = preview == null && (state == "Error" || state == "Setup" || state == "Waiting");
        secondary.Text = state == "Waiting" ? "Cancel" : state == "Setup" ? "Details" : "Retry";
        tips.SetToolTip(statusLabel, statusDetails ?? status); tips.SetToolTip(toggle, on ? "Save and close Drive files before turning off." : "Make Drive available on this computer.");
        if (trayState != state && preview == null)
        {
            trayState = state; tray.Text = "Google Drive — " + status.TrimEnd('…');
            var previous = tray.Icon; tray.Icon = MakeIcon(state == "On" ? green : busy ? Color.FromArgb(226, 160, 34) : secondaryInk);
            if (previous != null) previous.Dispose();
        }
    }
    void ApplyPreview(string state)
    {
        if (state == "on") ApplyVisual("On", "Connected", "Close files before turning off", true, false, true);
        else if (state == "busy") ApplyVisual("Waiting", "Finishing file activity…", "Close files; uploads will finish", true, true, false);
        else if (state == "error") ApplyVisual("Error", "Status unavailable", "Open details or try again", false, false, false);
        else ApplyVisual("Off", "Not mounted", "Off until you need it", false, false, true);
        toggle.FinishAnimation();
    }
    public void RenderPreview(string path)
    {
        Opacity = 0; ShowInTaskbar = false; Show(); ApplyPreview(preview); PerformLayout();
        using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
        Close();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Stop(); timer.Dispose(); tray.Visible = false;
            var trayIcon = tray.Icon; tray.Dispose(); if (trayIcon != null) trayIcon.Dispose();
            menu.Dispose(); tips.Dispose();
            titleFont.Dispose(); rowFont.Dispose(); textFont.Dispose(); actionFont.Dispose(); menuFont.Dispose();
        }
        base.Dispose(disposing);
    }
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    static Icon MakeIcon(Color color)
    {
        using (var bitmap = new Bitmap(32, 32))
        {
            using (var graphics = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(color))
            using (var pen = new Pen(Color.White, 2.3f))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillEllipse(brush, 1, 1, 30, 30);
                graphics.DrawRectangle(pen, 8, 10, 16, 13); graphics.DrawLine(pen, 10, 18, 22, 18);
            }
            IntPtr native = bitmap.GetHicon();
            try { using (Icon borrowed = Icon.FromHandle(native)) return (Icon)borrowed.Clone(); }
            finally { DestroyIcon(native); }
        }
    }
}
