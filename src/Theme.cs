using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IdleTrim
{
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(18, 19, 24);
        public static readonly Color Surface = Color.FromArgb(30, 32, 40);
        public static readonly Color Raised = Color.FromArgb(48, 51, 62);
        public static readonly Color RaisedHover = Color.FromArgb(62, 66, 80);
        public static readonly Color Text = Color.FromArgb(236, 238, 243);
        public static readonly Color Dim = Color.FromArgb(150, 155, 170);
        public static readonly Color Faint = Color.FromArgb(92, 96, 110);
        public static readonly Color Accent = Color.FromArgb(104, 130, 255);
        public static readonly Color AccentHover = Color.FromArgb(128, 151, 255);

        static float scale;
        public static int S(int x)
        {
            if (scale == 0)
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            return (int)(x * scale);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void Fill(Graphics g, Color color, RectangleF r, float radius)
        {
            using (var b = new SolidBrush(color))
            using (var p = Round(r, radius))
                g.FillPath(b, p);
        }

        public static void Smooth(Control c)
        {
            typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(c, true, null);
        }

        public static void Draw(Graphics g, string text, Font font, Color color, Rectangle r, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text, font, r, color, flags | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
        }
    }

    // Dark title bar: attribute 20 on Windows 10 20H1+, attribute 35 sets the exact colour on Windows 11.
    class DarkForm : Form
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // Tests open windows off-screen without taking focus.
        internal static bool Quiet = false;
        protected override bool ShowWithoutActivation { get { return Quiet || base.ShowWithoutActivation; } }

        public DarkForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1;
                DwmSetWindowAttribute(Handle, 20, ref on, 4);
                int caption = ColorTranslator.ToWin32(Theme.Bg);
                DwmSetWindowAttribute(Handle, 35, ref caption, 4);
            }
            catch (DllNotFoundException) { }
        }
    }

    class Slider : Control
    {
        int min, max = 100, value;
        bool hover, dragging;

        public int LargeChange = 10;
        public event EventHandler Scroll;

        public Slider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
        }

        public int Minimum { get { return min; } }
        public int Maximum { get { return max; } }

        public int Value
        {
            get { return value; }
            set { this.value = Math.Max(min, Math.Min(max, value)); Invalidate(); }
        }

        public void SetRange(int lo, int hi)
        {
            min = lo;
            max = Math.Max(lo, hi);
            Value = value;
        }

        // Same path as dragging: moves the thumb and tells listeners.
        public void UserSet(int v)
        {
            int before = value;
            Value = v;
            if (value != before && Scroll != null) Scroll(this, EventArgs.Empty);
        }

        int ThumbRadius { get { return Theme.S(hover || dragging ? 8 : 7); } }
        int Inset { get { return Theme.S(9); } }

        void SetFromX(int x)
        {
            int width = Math.Max(1, Width - Inset * 2);
            UserSet(min + (int)Math.Round((double)(x - Inset) / width * (max - min)));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left) { dragging = true; SetFromX(e.X); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging) SetFromX(e.X);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { dragging = false; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: UserSet(value - 1); break;
                case Keys.Right: case Keys.Up: UserSet(value + 1); break;
                case Keys.PageDown: UserSet(value - LargeChange); break;
                case Keys.PageUp: UserSet(value + LargeChange); break;
                case Keys.Home: UserSet(min); break;
                case Keys.End: UserSet(max); break;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float width = Width - Inset * 2;
            float h = Theme.S(4), y = (Height - h) / 2;
            float x = Inset + (max > min ? (float)(value - min) / (max - min) * width : 0);
            Theme.Fill(g, Theme.Raised, new RectangleF(Inset, y, width, h), h / 2);
            Theme.Fill(g, Enabled ? Theme.Accent : Theme.Faint, new RectangleF(Inset, y, Math.Max(h, x - Inset), h), h / 2);
            float r = ThumbRadius;
            if (Focused && Enabled)
                using (var halo = new SolidBrush(Color.FromArgb(70, Theme.Accent)))
                    g.FillEllipse(halo, x - r - 3, Height / 2f - r - 3, (r + 3) * 2, (r + 3) * 2);
            using (var thumb = new SolidBrush(Enabled ? Theme.Text : Theme.Dim))
                g.FillEllipse(thumb, x - r, Height / 2f - r, r * 2, r * 2);
        }
    }

    // One track with two handles; the kept part between them is highlighted.
    class RangeSlider : Control
    {
        int max = 100, low, high = 100;
        bool hover, dragging, activeHigh;

        public int MinGap;
        public event EventHandler Scroll;

        public RangeSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
        }

        public int Maximum { get { return max; } }
        public int Low { get { return low; } }
        public int High { get { return high; } }

        // Sets the range and puts the handles at both ends.
        public void Reset(int maximum, int minGap)
        {
            max = Math.Max(0, maximum);
            MinGap = minGap;
            low = 0;
            high = max;
            activeHigh = false;
            Invalidate();
        }

        // Same path as dragging: the handle stops at MinGap from the other one.
        public void UserSet(bool highHandle, int v)
        {
            int oldLow = low, oldHigh = high;
            if (highHandle) high = Math.Max(low + MinGap, Math.Min(max, v));
            else low = Math.Min(high - MinGap, Math.Max(0, v));
            Invalidate();
            if ((low != oldLow || high != oldHigh) && Scroll != null) Scroll(this, EventArgs.Empty);
        }

        int Inset { get { return Theme.S(9); } }
        float X(int v) { return Inset + (max > 0 ? (float)v / max * (Width - Inset * 2) : 0); }
        int ValueAt(int x) { return (int)Math.Round((double)(x - Inset) / Math.Max(1, Width - Inset * 2) * max); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                float toLow = Math.Abs(e.X - X(low)), toHigh = Math.Abs(e.X - X(high));
                activeHigh = toHigh < toLow || (toHigh == toLow && e.X > X(high));
                dragging = true;
                UserSet(activeHigh, ValueAt(e.X));
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging) UserSet(activeHigh, ValueAt(e.X));
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { dragging = false; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }

        protected override bool IsInputKey(Keys k)
        {
            Keys code = k & Keys.KeyCode;   // Shift+arrow arrives with the Shift bit set
            return code == Keys.Left || code == Keys.Right || code == Keys.Up || code == Keys.Down || base.IsInputKey(k);
        }

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(Keys key);

        static bool IsDown(Keys key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }

        // Physical key state; tests swap these out.
        internal Func<Keys, bool> KeyIsDown = IsDown;
        internal Func<bool> ShiftIsDown = () => (ModifierKeys & Keys.Shift) != 0;

        // Steps per tick while Shift is held.
        public int ShiftStep = 10;

        // Windows stops repeating an arrow once Shift is pressed, so holding is driven by a timer
        // that rereads Shift on every tick: speed can change mid-hold as often as wanted.
        readonly Timer repeat = new Timer { Interval = 33 };
        Keys heldKey = Keys.None;
        DateTime heldSince;
        const int RepeatDelayMs = 350;

        void StepHeld(bool shift)
        {
            int step = shift ? ShiftStep : 1;
            int current = activeHigh ? high : low;
            UserSet(activeHigh, current + (heldKey == Keys.Left ? -step : step));
        }

        void StopHold()
        {
            heldKey = Keys.None;
            repeat.Stop();
        }

        protected override void OnCreateControl()
        {
            base.OnCreateControl();
            repeat.Tick += (o, e) =>
            {
                if (heldKey == Keys.None || !KeyIsDown(heldKey) || !Focused) { StopHold(); return; }
                if ((DateTime.Now - heldSince).TotalMilliseconds >= RepeatDelayMs) StepHeld(ShiftIsDown());
            };
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == heldKey) StopHold();
            base.OnKeyUp(e);
        }

        protected override void OnLostFocus(EventArgs e) { StopHold(); Invalidate(); base.OnLostFocus(e); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) repeat.Dispose();
            base.Dispose(disposing);
        }

        // Left/right move the active handle (faster with Shift); up/down choose which one is active.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            int current = activeHigh ? high : low;
            switch (e.KeyCode)
            {
                case Keys.Left:
                case Keys.Right:
                    if (e.KeyCode == heldKey && KeyIsDown(heldKey)) break;   // Windows' own repeat; the timer handles it
                    heldKey = e.KeyCode;
                    heldSince = DateTime.Now;
                    StepHeld(e.Shift);
                    repeat.Start();
                    break;
                case Keys.Up: activeHigh = true; Invalidate(); break;
                case Keys.Down: activeHigh = false; Invalidate(); break;
                case Keys.PageDown: UserSet(activeHigh, current - Math.Max(1, max / 10)); break;
                case Keys.PageUp: UserSet(activeHigh, current + Math.Max(1, max / 10)); break;
                case Keys.Home: UserSet(activeHigh, 0); break;
                case Keys.End: UserSet(activeHigh, max); break;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float width = Width - Inset * 2;
            float h = Theme.S(4), y = (Height - h) / 2, a = X(low), b = X(high);
            Theme.Fill(g, Theme.Raised, new RectangleF(Inset, y, width, h), h / 2);
            Theme.Fill(g, Enabled ? Theme.Accent : Theme.Faint, new RectangleF(a, y, Math.Max(h, b - a), h), h / 2);
            DrawHandle(g, a, !activeHigh);
            DrawHandle(g, b, activeHigh);
        }

        void DrawHandle(Graphics g, float x, bool active)
        {
            float r = Theme.S((hover || dragging) && active ? 8 : 7);
            if (Focused && Enabled && active)
                using (var halo = new SolidBrush(Color.FromArgb(70, Theme.Accent)))
                    g.FillEllipse(halo, x - r - 3, Height / 2f - r - 3, (r + 3) * 2, (r + 3) * 2);
            using (var thumb = new SolidBrush(Enabled ? Theme.Text : Theme.Dim))
                g.FillEllipse(thumb, x - r, Height / 2f - r, r * 2, r * 2);
        }
    }

    class Toggle : CheckBox
    {
        bool hover;

        public Toggle(string text)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Text = text;
            AutoSize = false;
            Anchor = AnchorStyles.Left;
            Size = new Size(Theme.S(38) + TextRenderer.MeasureText(text, new Font("Segoe UI", 9f)).Width, Theme.S(26));
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = Theme.S(30), h = Theme.S(16), y = (Height - h) / 2, pad = Theme.S(3), knob = h - pad * 2;
            Color track = !Enabled ? Theme.Raised : Checked ? (hover ? Theme.AccentHover : Theme.Accent) : (hover ? Theme.RaisedHover : Theme.Raised);
            Theme.Fill(g, track, new RectangleF(0, y, w, h), h / 2);
            using (var b = new SolidBrush(Enabled ? Theme.Text : Theme.Faint))
                g.FillEllipse(b, Checked ? w - knob - pad : pad, y + pad, knob, knob);
            var text = new Rectangle((int)w + Theme.S(6), 0, Width - (int)w - Theme.S(6), Height);
            Theme.Draw(g, Text, Font, Enabled ? Theme.Text : Theme.Faint, text, TextFormatFlags.Left);
        }
    }

    class Btn : Button
    {
        bool hover, down;
        readonly bool primary;

        public Btn(string text, bool primary = false)
        {
            this.primary = primary;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Text = text;
            Size = new Size(TextRenderer.MeasureText(text, new Font("Segoe UI", 9f)).Width + Theme.S(32), Theme.S(32));
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { hover = down = false; Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = !Enabled ? Theme.Surface
                : primary ? (hover && !down ? Theme.AccentHover : Theme.Accent)
                : (hover && !down ? Theme.RaisedHover : Theme.Raised);
            Theme.Fill(g, fill, new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(7));
            Color text = !Enabled ? Theme.Faint : primary ? Color.White : Theme.Text;
            Theme.Draw(g, Text, Font, text, ClientRectangle, TextFormatFlags.HorizontalCenter);
        }
    }

    class Segmented : Control
    {
        int selected;
        int hover = -1;

        public readonly List<string> Items = new List<string>();
        public event EventHandler SelectedIndexChanged;

        public Segmented()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                bool changed = selected != value;
                selected = value;
                Invalidate();
                if (changed && SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        int At(int x) { return Items.Count == 0 ? -1 : Math.Max(0, Math.Min(Items.Count - 1, x * Items.Count / Math.Max(1, Width))); }

        protected override void OnMouseDown(MouseEventArgs e) { Focus(); if (At(e.X) >= 0) SelectedIndex = At(e.X); base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (hover != At(e.X)) { hover = At(e.X); Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left && selected > 0) SelectedIndex = selected - 1;
            if (e.KeyCode == Keys.Right && selected < Items.Count - 1) SelectedIndex = selected + 1;
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.Fill(g, Theme.Surface, new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(7));
            if (Items.Count == 0) return;
            float w = (float)Width / Items.Count;
            int pad = Theme.S(3);
            for (int i = 0; i < Items.Count; i++)
            {
                var r = new RectangleF(i * w + pad, pad, w - pad * 2 - 1, Height - pad * 2 - 1);
                if (i == selected) Theme.Fill(g, Enabled ? Theme.Accent : Theme.Raised, r, Theme.S(5));
                else if (i == hover && Enabled) Theme.Fill(g, Theme.Raised, r, Theme.S(5));
                Color text = !Enabled ? Theme.Faint : i == selected ? Color.White : Theme.Dim;
                Theme.Draw(g, Items[i], Font, text, Rectangle.Round(r), TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }

    class ProgressLine : Control
    {
        int value;

        public ProgressLine()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public int Value   // 0 to 1000
        {
            get { return value; }
            set { this.value = Math.Max(0, Math.Min(1000, value)); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = Height - 1;
            Theme.Fill(g, Theme.Surface, new RectangleF(0, 0, Width - 1, h), h / 2);
            if (value > 0)
                Theme.Fill(g, Theme.Accent, new RectangleF(0, 0, Math.Max(h, (Width - 1) * value / 1000f), h), h / 2);
        }
    }

    // Rounded panel for the file area; lights up while a file is dragged over the window.
    class Card : TableLayoutPanel
    {
        bool highlight;

        public Card()
        {
            BackColor = Theme.Surface;
            Theme.Smooth(this);
        }

        public bool Highlight
        {
            get { return highlight; }
            set { highlight = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
            Theme.Fill(g, BackColor, r, Theme.S(9));
            using (var pen = new Pen(highlight ? Theme.Accent : Theme.Raised, highlight ? 2f : 1f))
            using (var path = Theme.Round(r, Theme.S(9)))
            {
                pen.DashStyle = DashStyle.Dash;
                g.DrawPath(pen, path);
            }
        }
    }

    // Replaces MessageBox. Rows are label/value pairs; a null label makes a plain paragraph.
    class ThemedDialog : DarkForm
    {
        public readonly string Content;
        public readonly Toggle Option;   // an optional tick box under the text, e.g. "Don't ask again"

        public ThemedDialog(string title, string[,] rows, string okText, string cancelText, string optionText = null)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(Theme.S(18)), AutoSizeMode = AutoSizeMode.GrowAndShrink };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var all = new System.Text.StringBuilder();
            int count = rows.GetLength(0);
            for (int i = 0; i < count; i++)
            {
                string label = rows[i, 0], value = rows[i, 1];
                all.AppendLine(label + ": " + value);
                var v = new Label { AutoSize = true, Text = value, UseMnemonic = false, MaximumSize = new Size(Theme.S(label == null ? 420 : 340), 0), Margin = new Padding(0, Theme.S(3), 0, Theme.S(3)) };
                if (label == null)
                {
                    grid.Controls.Add(v, 0, i);
                    grid.SetColumnSpan(v, 2);
                    continue;
                }
                grid.Controls.Add(new Label { AutoSize = true, Text = label, ForeColor = Theme.Dim, Margin = new Padding(0, Theme.S(3), Theme.S(18), Theme.S(3)) }, 0, i);
                grid.Controls.Add(v, 1, i);
            }
            Content = all.ToString();
            int extra = 0;
            if (optionText != null)
            {
                Option = new Toggle(optionText) { Margin = new Padding(0, Theme.S(10), 0, 0) };
                grid.Controls.Add(Option, 0, count);
                grid.SetColumnSpan(Option, 2);
                extra = 1;
            }

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, Theme.S(14), 0, 0), AutoSizeMode = AutoSizeMode.GrowAndShrink };
            var ok = new Btn(okText, true) { DialogResult = DialogResult.OK };
            var no = new Btn(cancelText) { DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(no);
            grid.Controls.Add(buttons, 0, count + extra);
            grid.SetColumnSpan(buttons, 2);
            Controls.Add(grid);
            AcceptButton = ok;
            CancelButton = no;
        }

        public static bool Ask(IWin32Window owner, string title, string[,] rows, string okText, string cancelText)
        {
            using (var d = new ThemedDialog(title, rows, okText, cancelText))
                return d.ShowDialog(owner) == DialogResult.OK;
        }
    }

    // A dark drop-down list. Items are shown with ToString.
    class Dropdown : ComboBox
    {
        public Dropdown()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            ItemHeight = Theme.S(22);
            Font = new Font("Segoe UI", 9.5f);
        }

        // Windows draws the closed box with a light arrow button; paint that part over in the theme.
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            bool print = m.Msg == 0x0317 || m.Msg == 0x0318;   // WM_PRINT / WM_PRINTCLIENT: screenshots
            if (m.Msg != 0x000F && !print) return;            // WM_PAINT
            using (var g = print ? Graphics.FromHdc(m.WParam) : CreateGraphics())
            {
                int bw = Theme.S(26);
                var button = new Rectangle(Width - bw - 1, 1, bw, Height - 2);
                using (var b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, button);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float cx = button.X + button.Width / 2f, cy = Height / 2f, k = Theme.S(4);
                using (var pen = new Pen(Enabled ? Theme.Dim : Theme.Faint, Theme.S(2) / 1.4f))
                    g.DrawLines(pen, new[] { new PointF(cx - k, cy - k / 2), new PointF(cx, cy + k / 2), new PointF(cx + k, cy - k / 2) });
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                using (var pen = new Pen(Focused ? Theme.Accent : Theme.Raised)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(selected ? Theme.Raised : Theme.Surface)) e.Graphics.FillRectangle(b, e.Bounds);
            if (e.Index >= 0)
            {
                var r = new Rectangle(e.Bounds.X + Theme.S(6), e.Bounds.Y, e.Bounds.Width - Theme.S(8), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, Items[e.Index].ToString(), Font, r, Enabled ? Theme.Text : Theme.Faint,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }

    // Asks for one line of text.
    class InputDialog : DarkForm
    {
        public readonly TextBox Box = new TextBox { BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Surface, ForeColor = Theme.Text, Font = new Font("Segoe UI", 10f) };

        public InputDialog(string title, string text, string okText)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(Theme.S(18)), AutoSizeMode = AutoSizeMode.GrowAndShrink };
            grid.Controls.Add(new Label { AutoSize = true, Text = text, MaximumSize = new Size(Theme.S(440), 0), UseMnemonic = false, Margin = new Padding(0, 0, 0, Theme.S(8)) }, 0, 0);
            Box.Width = Theme.S(440);
            grid.Controls.Add(Box, 0, 1);
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, Theme.S(14), 0, 0) };
            var ok = new Btn(okText, true) { DialogResult = DialogResult.OK };
            var no = new Btn("Cancel") { DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(no);
            grid.Controls.Add(buttons, 0, 2);
            Controls.Add(grid);
            AcceptButton = ok;
            CancelButton = no;
        }
    }
}
