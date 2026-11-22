using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace IdleTrim
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
        }
    }

    class MainForm : DarkForm
    {
        readonly Btn browse = new Btn("Browse");
        readonly Label fileLabel = Text0(), fileInfo = Text0();
        readonly Card card = new Card { Dock = DockStyle.Fill, Height = Theme.S(68), ColumnCount = 2, RowCount = 2, Padding = new Padding(Theme.S(14), Theme.S(8), Theme.S(12), Theme.S(8)), Margin = new Padding(0, 0, 0, Theme.S(10)) };

        readonly Preview preview = new Preview { Dock = DockStyle.Fill, Margin = new Padding(0, 0, Theme.S(14), 0) };
        readonly Timeline timeline = new Timeline { Dock = DockStyle.Fill, Margin = new Padding(0, Theme.S(10), 0, Theme.S(10)) };
        readonly Segmented model = new Segmented();
        readonly Slider sensitivity = Bar(), padding = Bar();
        readonly Label sensitivityValue = Text0(), paddingValue = Text0(), learnInfo = Text0(), keptInfo = Text0();
        readonly Toggle learn = new Toggle("Learn from my edits") { Checked = true };
        readonly Btn forget = new Btn("Forget learning");

        readonly Slider target = Bar(), res = Bar(), fps = Bar();
        readonly Label targetValue = Text0(), resValue = Text0(), fpsValue = Text0();
        readonly Toggle autoRes = new Toggle("Auto") { Checked = true };
        readonly Toggle mute = new Toggle("Remove audio");
        readonly Segmented encoder = new Segmented();
        readonly Label bitrate = Text0();
        readonly ProgressLine progress = new ProgressLine { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = Theme.S(6) };
        readonly Label status = Text0();
        readonly Btn export = new Btn("Export", true) { Enabled = false };
        readonly Btn cancelButton = new Btn("Cancel") { Enabled = false };
        readonly Btn showFile = new Btn("Show file") { Visible = false };

        string ffmpeg, ffprobe;
        string lastOutput;
        VideoInfo video;
        Analysis analysis;
        Models models;
        Learner learner;
        float[] scores;
        bool[] keep;
        sbyte[] overrides;
        List<Segment> segments = new List<Segment>();
        Plan plan;
        CancelFlag cancel;
        bool updating;
        bool busy;
        double targetMB = 10;
        int manualShort, manualFps;
        readonly string startupFile;

        static Label Text0() { return new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = "" }; }
        static Slider Bar() { return new Slider { Dock = DockStyle.Fill, Height = Theme.S(34), Margin = new Padding(Theme.S(10), 0, Theme.S(10), 0) }; }
        static int S(int x) { return Theme.S(x); }

        public MainForm(string file)
        {
            startupFile = file;
            Text = "IdleTrim";
            ClientSize = new Size(S(980), S(860));
            MinimumSize = new Size(S(820), S(760));

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Padding = new Padding(S(18), S(16), S(18), S(16)), AutoScroll = true };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(170)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(grid);

            // File card
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            fileLabel.Text = "Drop a gameplay video";
            fileLabel.AutoEllipsis = true;
            fileLabel.AutoSize = false;
            fileLabel.Dock = DockStyle.Fill;
            fileLabel.UseMnemonic = false;
            fileLabel.TextAlign = ContentAlignment.BottomLeft;
            fileLabel.Font = new Font("Segoe UI Semibold", 10f);
            fileInfo.Text = "or browse for one. IdleTrim keeps the action and cuts the rest.";
            fileInfo.AutoSize = false;
            fileInfo.Dock = DockStyle.Fill;
            fileInfo.TextAlign = ContentAlignment.TopLeft;
            fileInfo.ForeColor = Theme.Dim;
            card.Controls.Add(fileLabel, 0, 0);
            card.Controls.Add(fileInfo, 0, 1);
            card.Controls.Add(browse, 1, 0);
            card.SetRowSpan(browse, 2);
            browse.Anchor = AnchorStyles.Right;
            grid.Controls.Add(card, 0, 0);
            grid.SetColumnSpan(card, 4);

            // Preview + detection
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Height = S(300), Margin = new Padding(0) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            top.Controls.Add(preview, 0, 0);
            top.Controls.Add(DetectionPanel(), 1, 0);
            grid.Controls.Add(top, 0, 1);
            grid.SetColumnSpan(top, 4);

            timeline.Height = timeline.PreferredHeight + S(20);
            grid.Controls.Add(timeline, 0, 2);
            grid.SetColumnSpan(timeline, 4);

            // Output (same as cap-compress)
            AddRow(grid, 3, "Target size", target, targetValue, null);
            AddRow(grid, 4, "Resolution", res, resValue, autoRes);
            AddRow(grid, 5, "Frame rate", fps, fpsValue, null);
            var encLabel = Dim("Encoder");
            encoder.Dock = DockStyle.Fill;
            encoder.Height = S(34);
            encoder.Margin = new Padding(S(10), S(4), S(10), S(4));
            grid.Controls.Add(encLabel, 0, 6);
            grid.Controls.Add(encoder, 1, 6);
            grid.Controls.Add(mute, 2, 6);
            grid.SetColumnSpan(mute, 2);
            bitrate.Margin = new Padding(S(10), S(6), 0, S(6));
            grid.Controls.Add(Dim("Bitrate"), 0, 7);
            grid.Controls.Add(bitrate, 1, 7);
            grid.SetColumnSpan(bitrate, 3);

            progress.Margin = new Padding(0, S(8), 0, S(2));
            grid.Controls.Add(progress, 0, 8);
            grid.SetColumnSpan(progress, 4);
            status.AutoSize = false;
            status.Dock = DockStyle.Fill;
            status.AutoEllipsis = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.ForeColor = Theme.Dim;
            grid.Controls.Add(status, 0, 9);
            grid.SetColumnSpan(status, 4);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
            buttons.Controls.Add(export);
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(showFile);
            grid.Controls.Add(buttons, 0, 10);
            grid.SetColumnSpan(buttons, 4);

            for (int i = 0; i < 11; i++)
                grid.RowStyles.Add(i == 1 ? new RowStyle(SizeType.Percent, 100) : i == 9 ? new RowStyle(SizeType.Absolute, S(28)) : new RowStyle(SizeType.AutoSize));
            grid.RowCount = 11;

            AcceptButton = export;

            EventHandler changed = (o, e) => { if (!updating) Recalculate(); };
            target.Scroll += (o, e) => { targetMB = target.Value / 10.0; Recalculate(); };
            res.Scroll += (o, e) => { manualShort = plan.Rungs[res.Value]; Recalculate(); };
            fps.Scroll += (o, e) => { manualFps = fps.Value; Recalculate(); };
            autoRes.CheckedChanged += (o, e) => { if (plan != null) manualShort = plan.Short; changed(o, e); };
            mute.CheckedChanged += changed;
            encoder.SelectedIndexChanged += changed;

            model.SelectedIndexChanged += (o, e) => { if (!updating) Rescore(); };
            sensitivity.Scroll += (o, e) => Rescore();
            padding.Scroll += (o, e) => Rescore();
            timeline.PlayheadChanged += (o, e) => ShowPlayhead();
            timeline.ToggleRequested += Toggle;
            forget.Click += (o, e) => ForgetLearning();

            browse.Click += (o, e) => Browse();
            export.Click += (o, e) => StartExport();
            cancelButton.Click += (o, e) => { if (cancel != null) { cancel.Cancel(); status.Text = "Cancelling..."; } };
            showFile.Click += (o, e) => { if (lastOutput != null) Process.Start("explorer.exe", "/select,\"" + lastOutput + "\""); };
            AcceptDrops(this);
            Shown += (o, e) => { Retrain(); InitTools(); };
            FormClosing += (o, e) => { if (cancel != null) cancel.Cancel(); };

            SetInputs();
        }

        Control DetectionPanel()
        {
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0) };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(60)));

            model.Items.Add("SigLIP 2");
            model.Items.Add("DINOv2");
            model.Items.Add("Both");
            model.SelectedIndex = 2;
            model.Dock = DockStyle.Fill;
            model.Height = S(34);
            model.Margin = new Padding(S(10), S(4), 0, S(4));
            p.Controls.Add(Dim("Detection"), 0, 0);
            p.Controls.Add(model, 1, 0);
            p.SetColumnSpan(model, 2);

            sensitivity.SetRange(0, 100);
            sensitivity.Value = 50;
            p.Controls.Add(Dim("Sensitivity"), 0, 1);
            p.Controls.Add(sensitivity, 1, 1);
            p.Controls.Add(sensitivityValue, 2, 1);

            padding.SetRange(0, 40);
            padding.Value = 15;
            padding.LargeChange = 5;
            p.Controls.Add(Dim("Padding"), 0, 2);
            p.Controls.Add(padding, 1, 2);
            p.Controls.Add(paddingValue, 2, 2);

            learn.Margin = new Padding(0, S(8), 0, S(4));
            p.Controls.Add(learn, 0, 3);
            p.SetColumnSpan(learn, 3);
            learnInfo.ForeColor = Theme.Dim;
            learnInfo.MaximumSize = new Size(S(360), 0);
            learnInfo.Margin = new Padding(0, S(2), 0, S(6));
            p.Controls.Add(learnInfo, 0, 4);
            p.SetColumnSpan(learnInfo, 3);
            forget.Anchor = AnchorStyles.Left;
            p.Controls.Add(forget, 0, 5);
            p.SetColumnSpan(forget, 3);

            keptInfo.Font = new Font("Segoe UI Semibold", 10f);
            keptInfo.Margin = new Padding(0, S(12), 0, 0);
            keptInfo.MaximumSize = new Size(S(360), 0);
            p.Controls.Add(keptInfo, 0, 6);
            p.SetColumnSpan(keptInfo, 3);

            for (int i = 0; i < 7; i++) p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            p.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            p.RowCount = 8;
            return p;
        }

        static Label Dim(string text)
        {
            var l = Text0();
            l.Text = text;
            l.ForeColor = Theme.Dim;
            return l;
        }

        void AcceptDrops(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (o, e) =>
            {
                if (busy || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                e.Effect = DragDropEffects.Copy;
                card.Highlight = true;
            };
            c.DragLeave += (o, e) => card.Highlight = false;
            c.DragDrop += (o, e) =>
            {
                card.Highlight = false;
                var f = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (f != null && f.Length > 0) LoadVideo(f[0]);
            };
            foreach (Control child in c.Controls) AcceptDrops(child);
        }

        void AddRow(TableLayoutPanel grid, int row, string name, Control bar, Label value, CheckBox box)
        {
            grid.Controls.Add(Dim(name), 0, row);
            grid.Controls.Add(bar, 1, row);
            grid.Controls.Add(value, 2, row);
            if (box != null) grid.Controls.Add(box, 3, row);
            else grid.SetColumnSpan(value, 2);
        }

        void Ui(Action action)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // Runs work on a background thread with the progress bar and Cancel wired up; done runs on the UI thread.
        void RunBusy(string label, Action<CancelFlag, Action<double, string>> work, Action<string> done)
        {
            cancel = new CancelFlag();
            var flag = cancel;
            progress.Value = 0;
            status.Text = label + "...";
            SetBusy(true);
            var worker = new Thread(() =>
            {
                string error = null;
                try
                {
                    work(flag, (f, text) => Ui(() =>
                    {
                        progress.Value = (int)(f * 1000);
                        status.Text = string.Format("{0}... {1:0}%", text ?? label, f * 100);
                    }));
                }
                catch (CancelledException) { error = "Cancelled."; }
                catch (Exception ex) { error = (ex.InnerException ?? ex).Message; }
                Ui(() =>
                {
                    cancel = null;
                    SetBusy(false);
                    if (error != null) progress.Value = 0;
                    done(error);
                });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        // ---- ffmpeg setup ----

        void InitTools()
        {
            ffmpeg = Engine.FindTool("ffmpeg");
            ffprobe = Engine.FindTool("ffprobe");
            if (ffmpeg != null && ffprobe != null) { ToolsReady(); return; }

            bool download = ThemedDialog.Ask(this, "ffmpeg needed", new string[,]
            {
                { null, "IdleTrim needs ffmpeg, which wasn't found." },
                { null, "Download it now (about 100 MB) from gyan.dev into a folder next to this app?" }
            }, "Download", "Not now");
            if (!download)
            {
                status.Text = "ffmpeg is required. Put ffmpeg.exe and ffprobe.exe next to this app and restart it.";
                browse.Enabled = false;
                return;
            }
            string dest = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");
            RunBusy("Downloading ffmpeg", (flag, report) => Engine.DownloadFfmpeg(dest, p => report(p / 100.0, "Downloading ffmpeg"), flag), error =>
            {
                if (error != null) { status.Text = "Couldn't download ffmpeg: " + error; browse.Enabled = false; return; }
                ffmpeg = Path.Combine(dest, "ffmpeg.exe");
                ffprobe = Path.Combine(dest, "ffprobe.exe");
                ToolsReady();
            });
        }

        void ToolsReady()
        {
            updating = true;
            encoder.Items.Clear();
            encoder.Items.Add("x264 · best quality");
            if (Engine.HasEncoder(ffmpeg, "h264_nvenc")) encoder.Items.Add("NVENC · fast");
            encoder.SelectedIndex = 0;
            updating = false;
            status.Text = "Ready.";
            if (startupFile != null) LoadVideo(startupFile);
        }

        // ---- learning ----

        void Retrain()
        {
            var worker = new Thread(() =>
            {
                Learner l;
                try { l = Learner.Train(); }
                catch (Exception) { l = new Learner(); }
                Ui(() => { learner = l; UpdateLearnInfo(); Rescore(); });
            });
            worker.IsBackground = true;
            worker.Start();
        }

        void UpdateLearnInfo()
        {
            if (learner == null) { learnInfo.Text = "Loading what it has learned..."; return; }
            if (learner.Trained)
                learnInfo.Text = string.Format("Learned from {0} video{1} ({2} of action, {3} idle). Both models use what they learned.",
                    learner.Videos, learner.Videos == 1 ? "" : "s", Time(learner.Kept * Analysis.Step), Time(learner.Cut * Analysis.Step));
            else
                learnInfo.Text = "Not trained yet: SigLIP 2 judges frames with text prompts and DINOv2 uses motion. Fix the timeline and export a few videos to teach both.";
            forget.Enabled = !busy && learner.Videos > 0;
        }

        void ForgetLearning()
        {
            if (!ThemedDialog.Ask(this, "Forget learning", new string[,] { { null, "Delete everything IdleTrim learned from your edits?" } }, "Forget", "Keep"))
                return;
            Learner.Forget();
            Retrain();
        }

        // ---- input ----

        void Browse()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.ts;*.m4v;*.wmv|All files|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK) LoadVideo(dialog.FileName);
            }
        }

        void LoadVideo(string path)
        {
            if (busy || ffprobe == null) return;
            try
            {
                video = Engine.Probe(ffprobe, path);
            }
            catch (Exception ex)
            {
                video = null;
                plan = null;
                status.Text = ex.Message;
                SetInputs();
                return;
            }

            fileLabel.Text = Path.GetFileName(path);
            fileInfo.Text = string.Format("{0}x{1}  ·  {2:0.##} fps  ·  {3}  ·  {4}",
                video.Width, video.Height, video.Fps, Time(video.Duration), SizeText(video.SizeBytes));
            updating = true;
            autoRes.Checked = true;
            mute.Checked = false;
            updating = false;
            manualShort = video.ShortSide;
            manualFps = Math.Min(24, video.MaxFps);
            targetMB = 10;
            showFile.Visible = false;
            SetAnalysis(null);

            var cached = Analysis.Load(Analysis.KeyFor(path));
            if (cached != null) { SetAnalysis(cached); status.Text = "Loaded the earlier analysis of this video."; return; }
            Analyze();
        }

        void Analyze()
        {
            if (!Models.Present())
            {
                bool download = ThemedDialog.Ask(this, "Models needed", new string[,]
                {
                    { null, "IdleTrim finds action with two image models that run on your PC:" },
                    { "SigLIP 2", "Google, Apache 2.0, 178 MB" },
                    { "DINOv2", "Meta, Apache 2.0, 85 MB" },
                    { null, "Download them from Hugging Face into a folder next to this app?" }
                }, "Download", "Not now");
                if (!download) { status.Text = "The models are needed to find the action."; return; }
                RunBusy("Downloading models", (flag, report) => Models.Download((p, name) => report(p / 100.0, "Downloading " + name), flag), error =>
                {
                    if (error != null) { status.Text = "Couldn't download the models: " + error; return; }
                    Analyze();
                });
                return;
            }

            var source = video;
            Analysis result = null;
            RunBusy("Finding the action", (flag, report) =>
            {
                if (models == null)
                {
                    report(0, "Loading models");
                    models = new Models();
                }
                result = Analyzer.Run(ffmpeg, source, models, f => report(f, "Finding the action"), flag);
                result.Save();
            }, error =>
            {
                if (error != null) { status.Text = "Couldn't analyse the video: " + error.Replace('\n', ' '); return; }
                if (video != source) return;
                SetAnalysis(result);
                status.Text = string.Format("Analysed {0} on the {1}.", Time(result.Duration), models.OnGpu ? "GPU" : "CPU");
                progress.Value = 0;
            });
        }

        void SetAnalysis(Analysis a)
        {
            analysis = a;
            overrides = a == null ? null : new sbyte[a.Count];
            timeline.SetAnalysis(a);
            Rescore();
        }

        // ---- detection ----

        double Threshold { get { return 1 - sensitivity.Value / 100.0; } }
        double PadSeconds { get { return padding.Value / 10.0; } }

        void Rescore()
        {
            sensitivityValue.Text = sensitivity.Value + "%";
            paddingValue.Text = string.Format("{0:0.0} s", PadSeconds);
            if (analysis == null)
            {
                scores = null;
                keep = null;
                segments = new List<Segment>();
                timeline.SetResult(null, null, null, Threshold);
                keptInfo.Text = "";
                ShowPlayhead();
                Recalculate();
                return;
            }
            var source = model.SelectedIndex == 0 ? Source.Siglip : model.SelectedIndex == 1 ? Source.Dino : Source.Both;
            scores = Scoring.Scores(analysis, source, learner);
            keep = Scoring.Mask(scores, Threshold, PadSeconds, overrides);
            segments = Scoring.Segments(keep, analysis.Duration);
            timeline.SetResult(scores, keep, overrides, Threshold);
            double kept = Segment.Total(segments);
            keptInfo.Text = segments.Count == 0
                ? "Nothing kept. Raise the sensitivity or click the band under the graph."
                : string.Format("Keeps {0} of {1} in {2} part{3}", Time(kept), Time(analysis.Duration), segments.Count, segments.Count == 1 ? "" : "s");
            ShowPlayhead();
            Recalculate();
        }

        // Click a part to flip it; click a part you flipped to hand it back to detection.
        void Toggle(int step)
        {
            if (analysis == null || keep == null || busy) return;
            if (overrides[step] != 0)
            {
                sbyte was = overrides[step];
                for (int i = step; i >= 0 && overrides[i] == was; i--) overrides[i] = 0;
                for (int i = step + 1; i < overrides.Length && overrides[i] == was; i++) overrides[i] = 0;
            }
            else
            {
                int start, end;
                Scoring.RunAt(keep, step, out start, out end);
                sbyte value = keep[step] ? (sbyte)-1 : (sbyte)1;
                for (int i = start; i <= end; i++) overrides[i] = value;
            }
            Rescore();
        }

        void ShowPlayhead()
        {
            if (analysis == null) { preview.Badge = preview.Caption = null; preview.Kept = true; preview.Show(null, -1); return; }
            int step = analysis.StepAt(timeline.Playhead);
            preview.Kept = keep == null || keep[step];
            preview.Badge = keep == null ? null : keep[step] ? "KEPT" : "CUT";
            preview.Caption = string.Format("{0} / {1}", Time(timeline.Playhead), Time(analysis.Duration));
            preview.Show(analysis, step);
        }

        // ---- output ----

        Settings ReadSettings()
        {
            return new Settings
            {
                TargetMB = targetMB,
                Segments = new List<Segment>(segments),
                AutoRes = autoRes.Checked,
                AutoFps = false,
                Short = manualShort,
                Fps = manualFps,
                Nvenc = encoder.SelectedIndex == 1,
                Mute = mute.Checked
            };
        }

        // Rebuilds the plan and pushes its limits back into every control.
        void Recalculate()
        {
            if (video == null || segments.Count == 0) { plan = null; ClearOutputText(); SetInputs(); return; }
            plan = Engine.MakePlan(video, ReadSettings());
            manualShort = plan.Short;
            manualFps = plan.Fps;

            updating = true;
            if (plan.CanReduce)
            {
                target.SetRange((int)Math.Round(plan.MinMB * 10), (int)Math.Round(plan.MaxMB * 10));
                target.Value = Math.Max(target.Minimum, Math.Min(target.Maximum, (int)Math.Round(plan.TargetMB * 10)));
                target.LargeChange = Math.Max(1, (target.Maximum - target.Minimum) / 10);
            }
            res.SetRange(0, plan.Rungs.Count - 1);
            res.Value = plan.Rungs.IndexOf(plan.Short);
            fps.SetRange(plan.MinFps, video.MaxFps);
            fps.Value = plan.Fps;
            updating = false;

            targetValue.Text = plan.CanReduce ? string.Format("{0:0.0} MB (max {1:0.0})", plan.TargetMB, plan.MaxMB) : "not possible";
            resValue.Text = string.Format("{0}x{1}  (max {2}p)", plan.OutLong, plan.Short, plan.ResCap);
            fpsValue.Text = string.Format("{0} fps  (max {1})", plan.Fps, plan.FpsCap);
            bitrate.Text = plan.AudioKbps > 0
                ? string.Format("Video {0} kbps, audio {1} kbps", plan.VideoKbps, plan.AudioKbps)
                : string.Format("Video {0} kbps, no audio", plan.VideoKbps);
            SetInputs();
        }

        void ClearOutputText()
        {
            targetValue.Text = resValue.Text = fpsValue.Text = bitrate.Text = "";
        }

        void SetInputs()
        {
            bool loaded = video != null && !busy;
            bool ready = loaded && analysis != null;
            bool canExport = ready && plan != null && plan.CanReduce && segments.Count > 0;
            browse.Enabled = !busy;
            model.Enabled = sensitivity.Enabled = padding.Enabled = ready;
            timeline.Enabled = ready;
            learn.Enabled = !busy;
            forget.Enabled = !busy && learner != null && learner.Videos > 0;
            target.Enabled = canExport;
            autoRes.Enabled = canExport;
            res.Enabled = canExport && !autoRes.Checked && plan.Rungs.Count > 1;
            fps.Enabled = canExport && video.MaxFps > plan.MinFps;
            mute.Enabled = loaded && video.HasAudio;
            encoder.Enabled = loaded;
            export.Enabled = canExport;
            cancelButton.Enabled = busy;
        }

        void SetBusy(bool value)
        {
            busy = value;
            SetInputs();
        }

        void StartExport()
        {
            if (video == null || analysis == null || plan == null || !plan.CanReduce || segments.Count == 0 || busy) return;
            var settings = ReadSettings();
            var runPlan = plan;
            var source = video;
            var learned = analysis;
            var choice = (bool[])keep.Clone();
            bool teach = learn.Checked;
            string output = Engine.OutputPath(source.Path);
            string tool = ffmpeg;
            long size = 0;
            showFile.Visible = false;
            RunBusy("Exporting", (flag, report) =>
            {
                size = Engine.Run(tool, source, settings, runPlan, output, (f, text) => report(f, text), flag);
            }, error =>
            {
                if (error != null) { status.Text = error.Replace('\n', ' '); return; }
                progress.Value = 1000;
                lastOutput = output;
                showFile.Visible = true;
                status.Text = string.Format("Done: {0} of action, {1} (was {2}).", Time(runPlan.Kept), SizeText(size), SizeText(source.SizeBytes));
                if (teach)
                {
                    try { Learner.AddExample(learned, choice); Retrain(); }
                    catch (Exception ex) { status.Text += " Couldn't save what it learned: " + ex.Message; }
                }
            });
        }

        static string Time(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1
                ? string.Format("{0}:{1:00}:{2:00.0}", (int)t.TotalHours, t.Minutes, t.Seconds + t.Milliseconds / 1000.0)
                : string.Format("{0}:{1:00.0}", t.Minutes, t.Seconds + t.Milliseconds / 1000.0);
        }

        static string SizeText(long bytes)
        {
            return string.Format("{0:0.0} MB", bytes / 1048576.0);
        }
    }
}
