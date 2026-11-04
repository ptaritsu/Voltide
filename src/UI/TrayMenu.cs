using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Voltide.UI
{
    internal enum MenuRowKind
    {
        Level,      // name + bar + percentage
        Asleep,     // name + grey bar + 休眠中
        Autostart,  // 开机启动 with a toggle
        Quit        // 退出应用
    }

    internal class MenuRow
    {
        public string Name;
        public string Value;
        public int? Percent;
        public bool GreyValue;   // sleeping row: last level shown dimmed
        public MenuRowKind Kind;
    }

    /// <summary>
    /// The tray menu, built strictly to the layered artboards:
    ///
    ///   header   270 x 19   (top corners only)
    ///   gap        9
    ///   row      270 x 30   (top and bottom hairline)
    ///   gap        9
    ///   ...
    ///   bottom gap 9
    ///
    /// so a one-device menu is 19 + 9 + 30 + 9 + 30 + 9 = 106 tall, and every extra
    /// device adds 39.
    /// </summary>
    internal class MenuPanel : Control
    {
        private const float W = 270f;
        private const float HeaderH = 19f;
        private const float RowH = 30f;
        private const float Gap = 9f;
        private const float Corner = 8f;

        private const float TextX = 26.22f;
        private const float ValueX = 199.87f;
        private const float RuleLeft = 26.81f;
        private const float RuleRight = 200.06f;
        private const float RuleW = 0.75f;

        private const float BarX = 131.11f;
        private const float BarY = 12.03f;
        private const float BarW = 57.51f;
        private const float BarH = 5.95f;

        private const float LevelBaseline = 21.95f;   // rows with a percentage
        private const float CjkBaseline = 20.42f;     // 休眠中 / 退出应用

        private static readonly Color Bg = Color.FromArgb(15, 15, 15);
        private static readonly Color Rule = Color.FromArgb(30, 30, 30);
        private static readonly Color NameCol = Color.FromArgb(229, 203, 179);
        private static readonly Color Muted = Color.FromArgb(135, 135, 135);
        private static readonly Color Soft = Color.FromArgb(204, 204, 204);
        private static readonly Color Accent = Color.FromArgb(116, 221, 124);
        private static readonly Color BarBack = Color.FromArgb(79, 79, 79);

        private static readonly float[] GradPos = { 0f, 0.21f, 0.42f, 0.64f, 0.86f, 1f };
        private static readonly Color[] GradCol = new Color[]
        {
            Color.FromArgb(64, 58, 132),
            Color.FromArgb(61, 56, 127),
            Color.FromArgb(55, 50, 115),
            Color.FromArgb(45, 41, 94),
            Color.FromArgb(32, 29, 65),
            Color.FromArgb(21, 19, 43)
        };

        private readonly List<MenuRow> _rows;
        private readonly float _scale;

        private readonly Font _nameFont;
        private readonly Font _valueFont;
        private readonly Font _cnFont;
        private readonly Font _markFont;

        private readonly float[] _hover;
        private System.Windows.Forms.Timer _anim;
        private int _hot = -1;

        public event EventHandler QuitRequested;
        public event EventHandler AutostartToggled;
        public event EventHandler LogToggled;
        public event EventHandler OpenLogRequested;

        private bool _autostartOn;
        private float _switchT;                                  // 0 = off, 1 = on (animated)
        private bool _logOn;
        private float _logT;
        private System.Windows.Forms.Timer _switchAnim;
        private bool _autostartEnabled = true;

        /// <summary>Current switch position, read back by the host when it saves.</summary>
        public bool AutostartOn
        {
            get { return _autostartOn; }
            set { _autostartOn = value; _switchT = value ? 1f : 0f; }
        }

        /// <summary>Refresh-log switch; host persists it with the other settings.</summary>
        public bool LogOn
        {
            get { return _logOn; }
            set { _logOn = value; _logT = value ? 1f : 0f; }
        }

        /// <summary>False when the process is not a real Voltide.exe (in-memory dev build),
        /// where writing a Run entry would point at powershell.exe instead.</summary>
        public bool AutostartEnabled { get { return _autostartEnabled; } set { _autostartEnabled = value; } }

        private bool _showSettingsRows = true;

        /// <summary>False for the hover peek: device rows only, no settings rows.</summary>
        public bool ShowSettingsRows
        {
            get { return _showSettingsRows; }
            set
            {
                if (_showSettingsRows == value) return;
                _showSettingsRows = value;
                // Height depends on LineCount; the ctor already sized for settings on.
                Size = new Size((int)Math.Round(W * _scale),
                                (int)Math.Round(TotalHeight() * _scale));
            }
        }

        public int LineCount
        {
            get { return _rows.Count + (_showSettingsRows ? 4 : 0); }
        }

        public MenuPanel(List<MenuRow> rows, float scale,
                         Font nameFont, Font valueFont, Font cnFont, Font markFont)
        {
            _rows = rows;
            _scale = scale;
            _nameFont = nameFont;
            _valueFont = valueFont;
            _cnFont = cnFont;
            _markFont = markFont;
            _hover = new float[rows.Count + 4];

            SetStyle(ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.Selectable, false);

            Margin = Padding.Empty;
            Size = new Size((int)Math.Round(W * scale), (int)Math.Round(TotalHeight() * scale));
            BackColor = Bg;
        }

        /// <summary>19 + 9 + n*39.</summary>
        public float TotalHeight()
        {
            return HeaderH + Gap + LineCount * (RowH + Gap);
        }

        public int ScaledCorner { get { return (int)Math.Round(Corner * _scale); } }

        private float RowTop(int index)
        {
            return HeaderH + Gap + index * (RowH + Gap);
        }

        // --------------------------------------------------------------- input

        private int RowAt(int y)
        {
            for (int i = 0; i < LineCount; i++)
            {
                float top = RowTop(i) * _scale;
                float bottom = top + RowH * _scale;
                if (y >= top && y < bottom) return i;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hit = RowAt(e.Y);
            if (hit != _hot)
            {
                _hot = hit;
                Cursor = (hit >= _rows.Count && hit <= _rows.Count + 3) ? Cursors.Hand : Cursors.Default;
                StartAnim();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hot = -1;
            Cursor = Cursors.Default;
            StartAnim();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) { base.OnMouseClick(e); return; }

            int hit = RowAt(e.Y);

            if (hit == _rows.Count && _autostartEnabled)
            {
                // Autostart row: flip the switch and let the host write the Run entry.
                _autostartOn = !_autostartOn;
                StartSwitchAnim();
                if (AutostartToggled != null) AutostartToggled(this, EventArgs.Empty);
            }
            else if (hit == _rows.Count + 1)
            {
                // Refresh-log row.
                _logOn = !_logOn;
                StartSwitchAnim();
                if (LogToggled != null) LogToggled(this, EventArgs.Empty);
            }
            else if (hit == _rows.Count + 2)
            {
                // Open the log folder in Explorer.
                if (OpenLogRequested != null) OpenLogRequested(this, EventArgs.Empty);
            }
            else if (hit == _rows.Count + 3)
            {
                if (QuitRequested != null) QuitRequested(this, EventArgs.Empty);
            }

            base.OnMouseClick(e);
        }

        private void StartAnim()
        {
            if (_anim == null)
            {
                _anim = new System.Windows.Forms.Timer();
                _anim.Interval = 16;
                _anim.Tick += delegate
                {
                    bool moving = false;

                    for (int i = 0; i < _hover.Length; i++)
                    {
                        float target = (i == _hot) ? 1f : 0f;
                        float delta = target - _hover[i];

                        if (Math.Abs(delta) < 0.01f)
                        {
                            _hover[i] = target;
                            continue;
                        }

                        _hover[i] += delta * 0.35f;
                        moving = true;
                    }

                    Invalidate();
                    if (!moving) _anim.Stop();
                };
            }

            if (!_anim.Enabled) _anim.Start();
        }

        // -------------------------------------------------------------- paint

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            float s = _scale;

            using (SolidBrush bg = new SolidBrush(Bg))
            using (GraphicsPath body = Round(new RectangleF(0f, 0f, Width, Height), Corner * s))
            {
                g.FillPath(bg, body);
            }

            DrawHeader(g, s);
            DrawRules(g, s);

            for (int i = 0; i < LineCount; i++)
            {
                DrawRow(g, s, i);
            }
        }

        private void DrawHeader(Graphics g, float s)
        {
            float h = HeaderH * s;
            float r = Corner * s;

            // Only the top corners are rounded; the bottom edge meets the panel body.
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(0f, 0f, r * 2f, r * 2f, 180f, 90f);
                path.AddArc(Width - r * 2f, 0f, r * 2f, r * 2f, 270f, 90f);
                path.AddLine(Width, h, 0f, h);
                path.CloseFigure();

                using (LinearGradientBrush grad = new LinearGradientBrush(
                    new RectangleF(0f, 0f, Width, h),
                    GradCol[0], GradCol[GradCol.Length - 1], LinearGradientMode.Horizontal))
                {
                    ColorBlend blend = new ColorBlend();
                    blend.Positions = GradPos;
                    blend.Colors = GradCol;
                    grad.InterpolationColors = blend;

                    g.FillPath(grad, path);
                }
            }

            using (SolidBrush brush = new SolidBrush(Soft))
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                float baseline = 13.22f * s;
                g.DrawString("VOLTIDE", _markFont, brush,
                             ValueX * s, baseline - FontManager.Ascent(_markFont), sf);
            }
        }

        private void DrawRules(Graphics g, float s)
        {
            float top = (HeaderH - 0.15f) * s;
            float bottom = Height - 1f * s;
            float w = Math.Max(1f, RuleW * s);

            using (SolidBrush brush = new SolidBrush(Rule))
            {
                g.FillRectangle(brush, RuleLeft * s, top, w, bottom - top);
                g.FillRectangle(brush, RuleRight * s, top, w, bottom - top);
            }
        }

        private void DrawRow(Graphics g, float s, int index)
        {
            float top = RowTop(index) * s;
            float h = RowH * s;

            // Row order: devices..., autostart, log, open log, quit.
            bool isAutostart = index == _rows.Count;
            bool isLog = index == _rows.Count + 1;
            bool isOpenLog = index == _rows.Count + 2;
            bool isQuit = index == _rows.Count + 3;
            MenuRow row = (isQuit || isAutostart || isLog || isOpenLog) ? null : _rows[index];

            // hover wash, kept faint so the grid stays intact
            float hover = _hover[index];
            if (hover > 0.01f)
            {
                int alpha = (int)(16f * hover);
                using (SolidBrush wash = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
                {
                    g.FillRectangle(wash, 0f, top, Width, h);
                }
            }

            int lift = (int)(20f * hover);

            // hairlines at the top and bottom of the row
            using (SolidBrush line = new SolidBrush(Rule))
            {
                float lw = Math.Max(1f, RuleW * s);
                g.FillRectangle(line, 0f, top, Width, lw);
                g.FillRectangle(line, 0f, top + h - lw, Width, lw);
            }

            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;

                if (isOpenLog)
                {
                    using (SolidBrush brush = new SolidBrush(Lift(Soft, lift)))
                    {
                        g.DrawString("打开日志", _cnFont, brush, TextX * s,
                                     top + CjkBaseline * s - FontManager.Ascent(_cnFont), sf);
                    }
                    return;
                }

                if (isQuit)
                {
                    using (SolidBrush brush = new SolidBrush(Lift(Soft, lift)))
                    {
                        g.DrawString("退出应用", _cnFont, brush, TextX * s,
                                     top + CjkBaseline * s - FontManager.Ascent(_cnFont), sf);
                    }
                    return;
                }

                if (isAutostart)
                {
                    // Label dims slightly when the host cannot write a Run entry
                    // (dev build running under powershell.exe).
                    Color label = _autostartEnabled ? Soft : Color.FromArgb(110, 110, 110);
                    using (SolidBrush brush = new SolidBrush(Lift(label, lift)))
                    {
                        g.DrawString("开机启动", _cnFont, brush, TextX * s,
                                     top + CjkBaseline * s - FontManager.Ascent(_cnFont), sf);
                    }

                    DrawSwitch(g, s, top, lift, _autostartOn, _switchT, _autostartEnabled);
                    return;
                }

                if (isLog)
                {
                    using (SolidBrush brush = new SolidBrush(Lift(Soft, lift)))
                    {
                        g.DrawString("记录日志", _cnFont, brush, TextX * s,
                                     top + CjkBaseline * s - FontManager.Ascent(_cnFont), sf);
                    }

                    DrawSwitch(g, s, top, lift, _logOn, _logT, true);
                    return;
                }

                Color nameColor = row.Kind == MenuRowKind.Asleep ? Muted : NameCol;
                Font nameFont = FitName(g, row.Name, (BarX - TextX - 4f) * s);
                using (SolidBrush brush = new SolidBrush(Lift(nameColor, lift)))
                {
                    g.DrawString(row.Name, nameFont, brush, TextX * s,
                                 top + LevelBaseline * s - FontManager.Ascent(nameFont), sf);
                }

                // Numeric values always use the Latin face, including the remembered
                // level on a sleeping row — otherwise "48%" came out in 汉仪旗黑 and
                // did not match the same digits above it. Only Chinese words belong
                // to the CJK face.
                bool numeric = row.Kind == MenuRowKind.Level
                               || (row.GreyValue && row.Percent.HasValue);

                if (numeric)
                {
                    using (SolidBrush brush = new SolidBrush(Lift(Tone(row), lift)))
                    {
                        g.DrawString(row.Value, _valueFont, brush, ValueX * s,
                                     top + LevelBaseline * s - FontManager.Ascent(_valueFont), sf);
                    }
                }
                else
                {
                    using (SolidBrush brush = new SolidBrush(Lift(Muted, lift)))
                    {
                        g.DrawString(row.Value, _cnFont, brush, ValueX * s,
                                     top + CjkBaseline * s - FontManager.Ascent(_cnFont), sf);
                    }
                }
            }

            float barX = BarX * s;
            float barY = top + BarY * s;
            float barW = BarW * s;
            float barH = BarH * s;
            float radius = barH / 2f;

            using (GraphicsPath back = Round(new RectangleF(barX, barY, barW, barH), radius))
            using (SolidBrush backBrush = new SolidBrush(BarBack))
            {
                g.FillPath(backBrush, back);
            }

            if (row.Kind == MenuRowKind.Level && row.Percent.HasValue)
            {
                float fill = Math.Max(barH, barW * (row.Percent.Value / 100f));
                using (GraphicsPath front = Round(new RectangleF(barX, barY, fill, barH), radius))
                using (SolidBrush fillBrush = new SolidBrush(Lift(Tone(row), lift)))
                {
                    g.FillPath(fillBrush, front);
                }
            }
        }

        /// <summary>
        /// Drives the pill: knob slides and the track cross-fades grey -> green over
        /// ~150 ms. Eased, not linear, so the knob settles instead of stopping dead.
        /// </summary>
        private void StartSwitchAnim()
        {
            if (_switchAnim == null)
            {
                _switchAnim = new System.Windows.Forms.Timer();
                _switchAnim.Interval = 16;
                _switchAnim.Tick += delegate
                {
                    bool moving = false;

                    float targetA = _autostartOn ? 1f : 0f;
                    float deltaA = targetA - _switchT;
                    if (Math.Abs(deltaA) < 0.02f) _switchT = targetA;
                    else { _switchT += deltaA * 0.32f; moving = true; }

                    float targetL = _logOn ? 1f : 0f;
                    float deltaL = targetL - _logT;
                    if (Math.Abs(deltaL) < 0.02f) _logT = targetL;
                    else { _logT += deltaL * 0.32f; moving = true; }

                    if (!moving) _switchAnim.Stop();
                    Invalidate();
                };
            }

            if (!_switchAnim.Enabled) _switchAnim.Start();
        }

        /// <summary>
        /// The autostart pill: 28.7 x 9.02 with r=4.51, knob 4.51 at cy 15.
        /// Off keeps the same geometry with a grey track and the knob on the left.
        /// </summary>
        private void DrawSwitch(Graphics g, float s, float rowTop, int lift,
                                bool on, float rawT, bool enabled)
        {
            const float TrackX = 210.81f;
            const float TrackY = 10.49f;
            const float TrackW = 28.7f;
            const float TrackH = 9.02f;
            const float KnobR = 4.51f;
            const float KnobOnX = 235.01f;
            const float KnobOffX = TrackX + KnobR;

            float x = TrackX * s;
            float y = rowTop + TrackY * s;
            float w = TrackW * s;
            float h = TrackH * s;
            float radius = TrackH * s / 2f;

            // Ease the raw progress so the knob decelerates into place.
            float t = rawT;
            float eased = 1f - (1f - t) * (1f - t);

            Color track;
            if (!enabled) track = Color.FromArgb(58, 58, 58);
            else track = Mix(BarBack, Lift(Accent, lift), eased);

            using (GraphicsPath path = Round(new RectangleF(x, y, w, h), radius))
            using (SolidBrush brush = new SolidBrush(track))
            {
                g.FillPath(brush, path);
            }

            float knobR = KnobR * s;
            float cx = (KnobOffX + (KnobOnX - KnobOffX) * eased) * s;
            float cy = y + h / 2f;
            Color knobColor = enabled ? Color.White : Color.FromArgb(140, 140, 140);

            using (SolidBrush knob = new SolidBrush(knobColor))
            {
                g.FillEllipse(knob, cx - knobR, cy - knobR, knobR * 2f, knobR * 2f);
            }
        }

        private static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        /// <summary>
        /// Names sit between the left rule and the progress bar. A long one shrinks
        /// rather than sliding under the bar, and CJK names switch to the Chinese face.
        /// </summary>
        private Font FitName(Graphics g, string text, float avail)
        {
            bool cjk = CardRenderer.HasCjk(text);
            FontKind kind = cjk ? FontKind.Chinese : FontKind.MontserratMedium;

            float px = 20f * _scale;
            Font font = FontManager.Get(kind, px);
            if (string.IsNullOrEmpty(text)) return font;

            float width;
            using (StringFormat sf = cjk
                       ? new StringFormat()
                       : new StringFormat(StringFormat.GenericTypographic))
            {
                width = g.MeasureString(text, font, int.MaxValue, sf).Width;
            }

            if (width > avail && width > 0f)
            {
                font = FontManager.Get(kind, Math.Max(13f * _scale, px * (avail / width)));
            }

            return font;
        }

        // ------------------------------------------------------------ helpers

        private static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;

            if (d <= 0f)
            {
                p.AddRectangle(r);
                return p;
            }

            p.AddArc(r.Left, r.Top, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Top, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();

            return p;
        }

        /// <summary>Menu percentages follow the same green/amber/red tiers as the cards.</summary>
        private static Color Tone(MenuRow row)
        {
            if (row == null) return Accent;
            if (row.GreyValue) return Muted;
            return row.Percent.HasValue ? CardRenderer.PrimaryFor(row.Percent.Value) : Accent;
        }

        private static Color Lift(Color c, int amount)
        {
            return Color.FromArgb(
                Math.Min(255, c.R + amount),
                Math.Min(255, c.G + amount),
                Math.Min(255, c.B + amount));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _anim != null)
            {
                _anim.Stop();
                _anim.Dispose();
                _anim = null;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Borderless popup that hosts the panel.
    ///
    /// A ContextMenuStrip was the obvious host but it lays its items out with its own
    /// padding (left the dark strip down two edges) and, when shown manually, did not
    /// always close on an outside click. Owning the window fixes both: exact client
    /// size, no system chrome, and Deactivate does the dismissing.
    /// </summary>
    internal class TrayMenuForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public MenuPanel Panel { get; set; }

        /// <summary>Hover peek stays until the pointer leaves; the click menu still dismisses on Deactivate.</summary>
        public bool CloseOnDeactivate = true;

        private bool _armed;
        private System.Windows.Forms.Timer _armTimer;

        public TrayMenuForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(15, 15, 15);
            Padding = Padding.Empty;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW: keep it out of Alt+Tab
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // Corners come from DWM, not SetWindowRgn. A region is a 1-bit mask, so the
            // arc is stair-stepped at every DPI; DWM rounds during composition and the
            // edge is antialiased.
            try
            {
                int preference = 2;   // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */,
                                      ref preference, sizeof(int));
            }
            catch
            {
            }

            // Ignore the Deactivate that fires from the click that opened us.
            // Reused rather than recreated per show: a popup dismissed inside 150 ms
            // used to leave its timer running until the first tick.
            _armed = false;

            if (_armTimer == null)
            {
                _armTimer = new System.Windows.Forms.Timer();
                _armTimer.Interval = 150;
                _armTimer.Tick += delegate
                {
                    _armTimer.Stop();
                    _armed = true;
                };
            }

            _armTimer.Stop();
            _armTimer.Start();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (CloseOnDeactivate && _armed) Close();
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _armTimer != null)
            {
                _armTimer.Stop();
                _armTimer.Dispose();
                _armTimer = null;
            }
            base.Dispose(disposing);
        }
    }
}
