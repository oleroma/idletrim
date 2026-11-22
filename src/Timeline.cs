using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IdleTrim
{
    // Film strip, action graph and keep/cut band in one control. Click or drag to scrub, click the band to flip a part.
    class Timeline : Control
    {
        Analysis analysis;
        float[] scores;
        bool[] keep;
        sbyte[] overrides;
        double threshold, playhead;
        readonly Dictionary<int, Bitmap> thumbs = new Dictionary<int, Bitmap>();
        bool scrubbing;

        public event EventHandler PlayheadChanged;
        public event Action<int> ToggleRequested;   // step index of the part to flip

        public Timeline()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            repeat.Tick += (o, e) =>
            {
                if (heldKey == Keys.None || !KeyIsDown(heldKey) || !Focused) { StopHold(); return; }
                if ((DateTime.Now - heldSince).TotalMilliseconds >= 350) StepHeld(ShiftIsDown());
            };
        }

        int StripH { get { return Theme.S(54); } }
        int GraphH { get { return Theme.S(38); } }
        int BandH { get { return Theme.S(14); } }
        int Gap { get { return Theme.S(4); } }
        int BandTop { get { return StripH + GraphH + Gap * 2; } }
        public int PreferredHeight { get { return BandTop + BandH + 2; } }

        public double Playhead
        {
            get { return playhead; }
            set { playhead = analysis == null ? 0 : Math.Max(0, Math.Min(analysis.Duration, value)); Invalidate(); }
        }

        public void SetAnalysis(Analysis a)
        {
            foreach (var b in thumbs.Values) b.Dispose();
            thumbs.Clear();
            analysis = a;
            playhead = 0;
            Invalidate();
        }

        public void SetResult(float[] scores, bool[] keep, sbyte[] overrides, double threshold)
        {
            this.scores = scores;
            this.keep = keep;
            this.overrides = overrides;
            this.threshold = threshold;
            Invalidate();
        }

        float X(double seconds) { return analysis == null ? 0 : (float)(seconds / analysis.Duration * (Width - 1)); }
        double TimeAt(int x) { return analysis == null ? 0 : Math.Max(0, Math.Min(analysis.Duration, (double)x / Math.Max(1, Width - 1) * analysis.Duration)); }

        void Scrub(double seconds)
        {
            Playhead = seconds;
            if (PlayheadChanged != null) PlayheadChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (analysis == null || e.Button != MouseButtons.Left) return;
            if (e.Y >= BandTop - Gap)
            {
                if (ToggleRequested != null) ToggleRequested(analysis.StepAt(TimeAt(e.X)));
                return;
            }
            scrubbing = true;
            Scrub(TimeAt(e.X));
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (scrubbing) Scrub(TimeAt(e.X));
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { scrubbing = false; base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { StopHold(); Invalidate(); base.OnLostFocus(e); }

        // ---- keyboard: arrows scrub (hold Shift for 10x, switchable mid-hold), Space flips the part under the playhead ----

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(Keys key);

        internal Func<Keys, bool> KeyIsDown = k => (GetAsyncKeyState(k) & 0x8000) != 0;
        internal Func<bool> ShiftIsDown = () => (ModifierKeys & Keys.Shift) != 0;

        readonly Timer repeat = new Timer { Interval = 33 };
        Keys heldKey = Keys.None;
        DateTime heldSince;

        void StepHeld(bool shift)
        {
            double step = (shift ? 10 : 1) * Analysis.Step;
            Scrub(playhead + (heldKey == Keys.Left ? -step : step));
        }

        void StopHold()
        {
            heldKey = Keys.None;
            repeat.Stop();
        }

        protected override bool IsInputKey(Keys k)
        {
            Keys code = k & Keys.KeyCode;
            return code == Keys.Left || code == Keys.Right || code == Keys.Space || base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (analysis == null) return;
            switch (e.KeyCode)
            {
                case Keys.Left:
                case Keys.Right:
                    if (e.KeyCode == heldKey && KeyIsDown(heldKey)) break;
                    heldKey = e.KeyCode;
                    heldSince = DateTime.Now;
                    StepHeld(e.Shift);
                    repeat.Start();
                    break;
                case Keys.Home: Scrub(0); break;
                case Keys.End: Scrub(analysis.Duration); break;
                case Keys.Space:
                    if (ToggleRequested != null) ToggleRequested(analysis.StepAt(playhead));
                    e.SuppressKeyPress = true;
                    break;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == heldKey) StopHold();
            base.OnKeyUp(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                repeat.Dispose();
                foreach (var b in thumbs.Values) b.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---- painting ----

        Bitmap Thumb(int step)
        {
            Bitmap b;
            if (thumbs.TryGetValue(step, out b)) return b;
            using (var ms = new MemoryStream(analysis.Previews[step]))
            using (var full = new Bitmap(ms))
            {
                b = new Bitmap(full, new Size(StripH * 16 / 9, StripH));
            }
            thumbs[step] = b;
            return b;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int w = Width;
            Theme.Fill(g, Theme.Surface, new RectangleF(0, 0, w - 1, StripH), Theme.S(6));
            Theme.Fill(g, Theme.Surface, new RectangleF(0, StripH + Gap, w - 1, GraphH), Theme.S(6));
            Theme.Fill(g, Theme.Surface, new RectangleF(0, BandTop, w - 1, BandH), BandH / 2f);
            if (analysis == null || analysis.Count == 0)
            {
                Theme.Draw(g, "Load a video to see its timeline", Font, Theme.Faint, new Rectangle(0, 0, w, StripH), TextFormatFlags.HorizontalCenter);
                return;
            }

            // Film strip, cut parts dimmed.
            using (var clip = Theme.Round(new RectangleF(0, 0, w - 1, StripH), Theme.S(6)))
            {
                g.SetClip(clip);
                int tw = StripH * 16 / 9;
                for (int x = 0; x < w; x += tw)
                    g.DrawImageUnscaled(Thumb(analysis.StepAt(TimeAt(x + tw / 2))), x, 0);
                if (keep != null)
                    using (var dim = new SolidBrush(Color.FromArgb(170, Theme.Bg)))
                        ForRuns(false, (a, b) => g.FillRectangle(dim, a, 0, b - a, StripH));
                g.ResetClip();
            }

            // Action graph with the threshold.
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (scores != null && scores.Length > 1)
            {
                float top = StripH + Gap + 3, h = GraphH - 6;
                var pts = new PointF[scores.Length];
                for (int i = 0; i < scores.Length; i++)
                    pts[i] = new PointF(X(analysis.TimeOf(i) + Analysis.Step / 2), top + h - scores[i] * h);
                var area = new List<PointF>(pts);
                area.Add(new PointF(pts[pts.Length - 1].X, top + h));
                area.Add(new PointF(pts[0].X, top + h));
                using (var fill = new SolidBrush(Color.FromArgb(50, Theme.Accent)))
                    g.FillPolygon(fill, area.ToArray());
                using (var line = new Pen(Theme.Accent, Theme.S(2) / 1.5f))
                    g.DrawLines(line, pts);
                float ty = top + h - (float)threshold * h;
                using (var dash = new Pen(Color.FromArgb(160, Theme.Text), 1) { DashStyle = DashStyle.Dash })
                    g.DrawLine(dash, 0, ty, w, ty);
            }

            // Keep/cut band; parts you flipped by hand get a light outline.
            if (keep != null)
            {
                float r = BandH / 2f;
                ForRuns(true, (a, b) => Theme.Fill(g, Theme.Accent, new RectangleF(a, BandTop, Math.Max(BandH, b - a), BandH), r));
                if (overrides != null)
                    using (var pen = new Pen(Theme.Text, 1.5f))
                    {
                        int i = 0;
                        while (i < overrides.Length)
                        {
                            if (overrides[i] == 0) { i++; continue; }
                            int s = i;
                            while (i < overrides.Length && overrides[i] != 0) i++;
                            float a = X(analysis.TimeOf(s)), b = X(analysis.TimeOf(i));
                            g.DrawLine(pen, a + 2, BandTop + BandH + 1, b - 2, BandTop + BandH + 1);
                        }
                    }
            }

            // Playhead.
            float px = X(playhead);
            using (var pen = new Pen(Theme.Text, Theme.S(2)))
                g.DrawLine(pen, px, 0, px, BandTop + BandH);
            float k = Theme.S(5);
            using (var b = new SolidBrush(Theme.Text))
                g.FillPolygon(b, new[] { new PointF(px - k, 0), new PointF(px + k, 0), new PointF(px, k * 1.4f) });

            if (Focused)
                using (var pen = new Pen(Color.FromArgb(110, Theme.Accent), 2))
                using (var path = Theme.Round(new RectangleF(1, 1, w - 3, BandTop + BandH), Theme.S(6)))
                    g.DrawPath(pen, path);
        }

        void ForRuns(bool value, Action<float, float> draw)
        {
            int i = 0;
            while (i < keep.Length)
            {
                if (keep[i] != value) { i++; continue; }
                int s = i;
                while (i < keep.Length && keep[i] == value) i++;
                draw(X(analysis.TimeOf(s)), X(analysis.TimeOf(i)));
            }
        }
    }

    // The frame under the playhead, with a kept/cut badge.
    class Preview : Control
    {
        Image image;
        int shownStep = -1;
        public string Badge, Caption;
        public bool Kept;

        public Preview()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void Show(Analysis a, int step)
        {
            if (a == null) { SetImage(null, -1); return; }
            if (step == shownStep) { Invalidate(); return; }
            using (var ms = new MemoryStream(a.Previews[step]))
                SetImage(new Bitmap(ms), step);
        }

        void SetImage(Image img, int step)
        {
            if (image != null) image.Dispose();
            image = img;
            shownStep = step;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            // Largest 16:9 box that fits.
            int w = Width, h = w * 9 / 16;
            if (h > Height) { h = Height; w = h * 16 / 9; }
            var box = new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
            using (var path = Theme.Round(box, Theme.S(8)))
            {
                using (var b = new SolidBrush(Theme.Surface)) g.FillPath(b, path);
                if (image == null)
                {
                    Theme.Draw(g, "Drop a gameplay video", Font, Theme.Faint, box, TextFormatFlags.HorizontalCenter);
                    return;
                }
                g.SetClip(path);
                g.DrawImage(image, box);
                if (!Kept)
                    using (var dim = new SolidBrush(Color.FromArgb(120, Theme.Bg))) g.FillRectangle(dim, box);
                g.ResetClip();
            }
            if (Badge != null)
            {
                var size = TextRenderer.MeasureText(Badge, Font);
                var r = new RectangleF(box.X + Theme.S(10), box.Y + Theme.S(10), size.Width + Theme.S(14), size.Height + Theme.S(6));
                Theme.Fill(g, Kept ? Theme.Accent : Theme.Raised, r, Theme.S(5));
                Theme.Draw(g, Badge, Font, Kept ? Color.White : Theme.Text, Rectangle.Round(r), TextFormatFlags.HorizontalCenter);
            }
            if (Caption != null)
            {
                var size = TextRenderer.MeasureText(Caption, Font);
                var r = new RectangleF(box.Right - size.Width - Theme.S(24), box.Y + Theme.S(10), size.Width + Theme.S(14), size.Height + Theme.S(6));
                Theme.Fill(g, Color.FromArgb(190, Theme.Bg), r, Theme.S(5));
                Theme.Draw(g, Caption, Font, Theme.Text, Rectangle.Round(r), TextFormatFlags.HorizontalCenter);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && image != null) image.Dispose();
            base.Dispose(disposing);
        }
    }
}
