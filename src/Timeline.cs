using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IdleTrim
{
    // Film strip, action graph and keep/cut band in one control. Click or drag the strip to scrub. On the band, drag from a cut
    // area to add a kept part (it merges with any part it touches), drag a kept part to move its nearer edge, right-drag to
    // cut, right-click a part to remove it, double-click a part to split it. S splits a part at the playhead, I and O mark a range for K (keep) or X (cut), Ctrl+Z undoes.
    class Timeline : Control
    {
        Analysis analysis;
        float[] scores;
        bool[] keep;
        sbyte[] overrides;
        HashSet<int> splits = new HashSet<int>();
        double threshold, playhead;
        double markIn = double.NaN, markOut = double.NaN;
        int edgeStart = -1, edgeEnd, edgeFrom, edgeX;   // grabbing a kept part: its steps [start, end), the edge that moves, where the press was
        bool edgeRight;
        double viewStart, viewLen;   // the part of the video on screen; viewLen 0 means all of it
        public const double MinView = 5;
        int bandFrom = -1, bandX;     // a drag across the band: where it started
        bool bandKeep, bandMoved;
        readonly Dictionary<int, Bitmap> thumbs = new Dictionary<int, Bitmap>();
        bool scrubbing;

        public event EventHandler PlayheadChanged;
        public event Action<int> RemoveRequested;   // step index inside the part to remove
        public event Action<int, int, bool, bool> RangeDragged;   // first step, end step (exclusive), keep, first move of the drag
        public event Action UndoRequested;
        public event Action PlayRequested;
        public event Action<int> SplitRequested;    // step index where the new part starts
        public event Action<int, int, bool> RangeRequested;   // first step, end step (exclusive), keep
        public event Action<int, int, bool, int, bool> EdgeDragged;   // part's first step, end step (exclusive), right edge?, where that edge is now, first move

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
        int BarTop { get { return BandTop + BandH + Theme.S(5); } }
        int BarH { get { return Theme.S(4); } }
        public int PreferredHeight { get { return BarTop + BarH + 1; } }

        public double Playhead
        {
            get { return playhead; }
            set { playhead = analysis == null ? 0 : Math.Max(0, Math.Min(analysis.Duration, value)); FollowPlayhead(); Invalidate(); }
        }

        public void SetAnalysis(Analysis a)
        {
            foreach (var b in thumbs.Values) b.Dispose();
            thumbs.Clear();
            analysis = a;
            playhead = 0;
            viewStart = viewLen = 0;
            ClearMarks();
            Invalidate();
        }

        public void SetResult(float[] scores, bool[] keep, sbyte[] overrides, HashSet<int> splits, double threshold)
        {
            this.scores = scores;
            this.keep = keep;
            this.overrides = overrides;
            this.splits = splits ?? new HashSet<int>();
            this.threshold = threshold;
            Invalidate();
        }

        public bool Scrubbing { get { return scrubbing; } }   // the mouse is down on the strip
        public double MarkIn { get { return markIn; } }
        public double MarkOut { get { return markOut; } }
        public bool HasRange { get { return !double.IsNaN(markIn) && !double.IsNaN(markOut) && Math.Abs(markOut - markIn) >= Analysis.Step / 2; } }

        public void ClearMarks()
        {
            markIn = markOut = double.NaN;
            Invalidate();
        }

        int StepEdge(double seconds) { return analysis == null ? 0 : Math.Max(0, Math.Min(analysis.Count, (int)Math.Round(seconds / Analysis.Step))); }

        // ---- view: Ctrl+wheel zooms around the pointer, the wheel scrolls ----

        public double ViewStart { get { return viewStart; } }
        public double ViewLength { get { return analysis == null ? 0 : viewLen > 0 ? viewLen : analysis.Duration; } }
        public bool Zoomed { get { return analysis != null && ViewLength < analysis.Duration - 1e-6; } }

        internal Func<bool> CtrlIsDown = () => (ModifierKeys & Keys.Control) != 0;

        public void SetView(double start, double length)
        {
            if (analysis == null) return;
            double d = analysis.Duration;
            viewLen = Math.Max(Math.Min(MinView, d), Math.Min(d, length));
            viewStart = Math.Max(0, Math.Min(d - viewLen, start));
            if (viewLen >= d - 1e-6) { viewLen = 0; viewStart = 0; }
            Invalidate();
        }

        // Pages the view along when the playhead leaves it, as during playback or arrow scrubbing.
        void FollowPlayhead()
        {
            if (!Zoomed || scrubbing) return;
            double len = ViewLength;
            if (playhead < viewStart || playhead > viewStart + len)
                SetView(playhead - len * 0.1, len);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (analysis == null) { base.OnMouseWheel(e); return; }
            double notches = e.Delta / 120.0;
            double len = ViewLength;
            if (CtrlIsDown())
            {
                double anchor = TimeAt(e.X), frac = (double)e.X / Math.Max(1, Width - 1);
                double newLen = len * Math.Pow(0.8, notches);
                SetView(anchor - frac * Math.Max(Math.Min(MinView, analysis.Duration), Math.Min(analysis.Duration, newLen)), newLen);
            }
            else if (Zoomed)
                SetView(viewStart - notches * len * 0.15, len);
            else { base.OnMouseWheel(e); return; }
            var h = e as HandledMouseEventArgs;
            if (h != null) h.Handled = true;   // don't also scroll the window
            if (scrubbing) Scrub(TimeAt(e.X));
        }

        float X(double seconds) { return analysis == null ? 0 : (float)((seconds - viewStart) / ViewLength * (Width - 1)); }
        double TimeAt(int x) { return analysis == null ? 0 : Math.Max(0, Math.Min(analysis.Duration, viewStart + (double)x / Math.Max(1, Width - 1) * ViewLength)); }

        // Repaints at once: Windows only paints when no input is waiting, so while the mouse keeps moving an ordinary
        // Invalidate would leave the playhead trailing behind the pointer. Drawing a step takes a few ms.
        void Scrub(double seconds)
        {
            Playhead = seconds;
            if (PlayheadChanged != null) PlayheadChanged(this, EventArgs.Empty);
            Update();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            // Keep the mouse for the whole drag, so moves and the release still arrive once the pointer leaves the strip,
            // e.g. past its left end to reach the very start.
            if (UseCapture) Capture = true;
            if (analysis == null || (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right)) return;
            if (e.Y >= BandTop - Gap && keep != null)
            {
                int at = analysis.StepAt(TimeAt(e.X));
                if (e.Button == MouseButtons.Left && keep[at])
                {
                    // Pressed on a kept part: the edge on that side of its middle follows the drag, by exactly as much as the pointer moves.
                    int a, b;
                    Scoring.RunAt(keep, at, splits, out a, out b);
                    edgeStart = a;
                    edgeEnd = b + 1;
                    edgeRight = TimeAt(e.X) >= analysis.TimeOf(a) + (edgeEnd - a) * Analysis.Step / 2;
                    edgeFrom = edgeRight ? edgeEnd : edgeStart;
                    edgeX = e.X;
                    bandMoved = false;
                    return;
                }
                bandFrom = analysis.StepAt(TimeAt(e.X));
                bandX = e.X;
                bandKeep = e.Button == MouseButtons.Left;
                bandMoved = false;
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            scrubbing = true;
            Scrub(TimeAt(e.X));
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // A drag only goes on while its button is held; if the release was missed somewhere, end it here.
            if ((scrubbing || edgeStart >= 0 || bandFrom >= 0) && (e.Button & (MouseButtons.Left | MouseButtons.Right)) == 0) EndDrag();
            if (scrubbing) Scrub(TimeAt(e.X));
            else if (edgeStart >= 0)
            {
                if (!bandMoved && Math.Abs(e.X - edgeX) < Theme.S(4)) return;
                double moved = (double)(e.X - edgeX) / Math.Max(1, Width - 1) * ViewLength;
                int to = edgeFrom + (int)Math.Round(moved / Analysis.Step);
                to = edgeRight ? Math.Max(edgeStart + 1, Math.Min(analysis.Count, to)) : Math.Max(0, Math.Min(edgeEnd - 1, to));
                if (EdgeDragged != null) EdgeDragged(edgeStart, edgeEnd, edgeRight, to, !bandMoved);
                bandMoved = true;
            }
            else if (bandFrom >= 0)
            {
                if (!bandMoved && Math.Abs(e.X - bandX) < Theme.S(4)) return;
                int to = analysis.StepAt(TimeAt(e.X));
                if (RangeDragged != null) RangeDragged(Math.Min(bandFrom, to), Math.Max(bandFrom, to) + 1, bandKeep, !bandMoved);
                bandMoved = true;
            }
            base.OnMouseMove(e);
        }

        // Tests drive the control without a real mouse and must not take the real one.
        internal static bool UseCapture = true;

        void EndDrag()
        {
            bandFrom = -1;
            edgeStart = -1;
            scrubbing = false;
            if (UseCapture) Capture = false;
        }

        // Losing the mouse to another window (Alt+Tab, a dialog) ends the drag where it is.
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (UseCapture && !Capture) { bandFrom = -1; edgeStart = -1; scrubbing = false; }
            base.OnMouseCaptureChanged(e);
        }

        // Double-click on the band: split the part there (or remove the split if there is one).
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (analysis != null && keep != null && e.Button == MouseButtons.Left && e.Y >= BandTop - Gap && SplitRequested != null)
                SplitRequested(StepEdge(TimeAt(e.X)));
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            // A click on the band without dragging: left moves the playhead there, right removes the part under it.
            if (bandFrom >= 0 && !bandMoved)
            {
                if (bandKeep) Scrub(TimeAt(e.X));
                else if (RemoveRequested != null) RemoveRequested(bandFrom);
            }
            if (edgeStart >= 0 && !bandMoved) Scrub(TimeAt(e.X));
            EndDrag();
            base.OnMouseUp(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { StopHold(); Invalidate(); base.OnLostFocus(e); }

        // ---- keyboard: arrows scrub (hold Shift for 10x, switchable mid-hold), Space plays, Delete removes the part under the playhead ----

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
            return code == Keys.Left || code == Keys.Right || code == Keys.Space || code == Keys.Escape || base.IsInputKey(k);
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
                    if (PlayRequested != null) PlayRequested();
                    e.SuppressKeyPress = true;
                    break;
                case Keys.Z:
                    if (e.Control && UndoRequested != null) UndoRequested();
                    break;
                case Keys.S:
                    if (SplitRequested != null) SplitRequested(StepEdge(playhead));
                    break;
                case Keys.I: markIn = playhead; Invalidate(); break;
                case Keys.O: markOut = playhead; Invalidate(); break;
                case Keys.Escape: ClearMarks(); break;
                case Keys.K:
                case Keys.X:
                case Keys.Delete:
                    if (HasRange && RangeRequested != null)
                    {
                        RangeRequested(StepEdge(Math.Min(markIn, markOut)), StepEdge(Math.Max(markIn, markOut)), e.KeyCode == Keys.K);
                        ClearMarks();
                    }
                    else if (e.KeyCode == Keys.Delete && RemoveRequested != null) RemoveRequested(analysis.StepAt(playhead));
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
            using (var track = new SolidBrush(Theme.Surface)) g.FillRectangle(track, 0, BandTop, w - 1, BandH);
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
                double tileT = tw / Math.Max(1.0, w - 1) * ViewLength;
                for (double t = Math.Floor(viewStart / tileT) * tileT; t < viewStart + ViewLength; t += tileT)
                    g.DrawImageUnscaled(Thumb(analysis.StepAt(Math.Min(analysis.Duration, t + tileT / 2))), (int)Math.Floor(X(t)), 0);
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
                int i0 = Math.Max(0, analysis.StepAt(viewStart) - 1), i1 = Math.Min(scores.Length - 1, analysis.StepAt(viewStart + ViewLength) + 1);
                var pts = new PointF[Math.Max(2, i1 - i0 + 1)];
                for (int i = 0; i < pts.Length; i++)
                {
                    int j = Math.Min(scores.Length - 1, i0 + i);
                    pts[i] = new PointF(X(analysis.TimeOf(j) + Analysis.Step / 2), top + h - scores[j] * h);
                }
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

            // Marked range.
            if (!double.IsNaN(markIn) || !double.IsNaN(markOut))
            {
                double a0 = double.IsNaN(markIn) ? markOut : markIn, b0 = double.IsNaN(markOut) ? markIn : markOut;
                float ra = X(Math.Min(a0, b0)), rb = X(Math.Max(a0, b0));
                using (var fill = new SolidBrush(Color.FromArgb(45, Theme.Text)))
                    g.FillRectangle(fill, ra, 0, Math.Max(2, rb - ra), BandTop - Gap);
                using (var pen = new Pen(Theme.Text, 1))
                {
                    if (!double.IsNaN(markIn)) g.DrawLine(pen, X(markIn), 0, X(markIn), BandTop - Gap);
                    if (!double.IsNaN(markOut)) g.DrawLine(pen, X(markOut), 0, X(markOut), BandTop - Gap);
                }
            }

            // Keep/cut band; splits get a notch.
            if (keep != null)
            {
                g.SmoothingMode = SmoothingMode.None;
                using (var part = new SolidBrush(Theme.Accent))
                    ForRuns(true, (a, b) => g.FillRectangle(part, a, BandTop, Math.Max(2, b - a), BandH));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var notch = new Pen(BackColor, Theme.S(3)))
                    foreach (int sp in splits)
                        if (sp > 0 && sp < keep.Length) g.DrawLine(notch, X(analysis.TimeOf(sp)), BandTop - 1, X(analysis.TimeOf(sp)), BandTop + BandH + 1);
            }

            // Where the view sits in the whole video, when zoomed.
            if (Zoomed)
            {
                float bx = (float)(viewStart / analysis.Duration * (w - 1)), bw = Math.Max(Theme.S(12), (float)(ViewLength / analysis.Duration * (w - 1)));
                Theme.Fill(g, Theme.Surface, new RectangleF(0, BarTop, w - 1, BarH), BarH / 2f);
                Theme.Fill(g, Theme.Faint, new RectangleF(bx, BarTop, bw, BarH), BarH / 2f);
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
                float x0 = X(analysis.TimeOf(s)), x1 = X(analysis.TimeOf(i));
                if (x1 >= -BandH && x0 <= Width + BandH) draw(Math.Max(-BandH, x0), Math.Min(Width + BandH, x1));
            }
        }
    }

    // The frame under the playhead, with a kept/cut badge.
    class Preview : Control
    {
        Image image;
        Bitmap live;   // reused for playback frames
        bool playing;
        int shownStep = -1;
        public string Badge, Caption;
        public bool Kept;

        public Preview()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        // A playback frame: packed BGR, w x h.
        public void ShowFrame(byte[] bgr, int w, int h)
        {
            if (live == null || live.Width != w || live.Height != h)
            {
                if (live != null) live.Dispose();
                live = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            }
            var data = live.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, live.PixelFormat);
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(bgr, y * w * 3, data.Scan0 + y * data.Stride, w * 3);
            live.UnlockBits(data);
            playing = true;
            Invalidate();
        }

        public bool Playing { get { return playing; } }

        public void Show(Analysis a, int step)
        {
            playing = false;
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
                var shown = playing ? live : image;
                if (shown == null)
                {
                    Theme.Draw(g, "Drop a gameplay video", Font, Theme.Faint, box, TextFormatFlags.HorizontalCenter);
                    return;
                }
                g.SetClip(path);
                g.DrawImage(shown, box);
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
            if (disposing && live != null) live.Dispose();
            base.Dispose(disposing);
        }
    }
}
