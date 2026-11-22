using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
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
        readonly Btn promptsButton = new Btn("Prompts");
        readonly Btn resetEdits = new Btn("Reset edits") { Enabled = false };
        readonly Stack<Tuple<sbyte[], HashSet<int>>> undo = new Stack<Tuple<sbyte[], HashSet<int>>>();
        HashSet<int> splitBase;
        readonly Session session = Session.Load();
        Session restore;   // the closed session's video state, applied once that video is open again
        // Asked before another video replaces one you're working on; tests swap it out.
        internal Func<string, bool> ConfirmReplace;
        readonly Btn play = new Btn("Play") { Enabled = false };
        readonly Toggle playAll = new Toggle("Play cut parts too");
        readonly Label hint = Text0();
        readonly System.Windows.Forms.Timer playTimer = new System.Windows.Forms.Timer { Interval = 15 };
        readonly System.Windows.Forms.Timer restartTimer = new System.Windows.Forms.Timer { Interval = 60 };
        Player player;
        bool playing;   // Play is on; player can be null for a moment while it restarts after a seek or an edit
        Player warm;          // started at the playhead and waiting, so Play is instant
        string warmKey;
        readonly System.Windows.Forms.Timer warmTimer = new System.Windows.Forms.Timer { Interval = 120 };

        readonly Slider target = Bar(), res = Bar(), fps = Bar();
        readonly Label targetValue = Text0(), resValue = Text0(), fpsValue = Text0();
        readonly Toggle autoRes = new Toggle("Auto") { Checked = true };
        readonly Toggle mute = new Toggle("Remove audio");
        readonly Toggle copy = new Toggle("No re-encode") { Checked = true };
        readonly List<string> encoderIds = new List<string>();
        bool hasNvenc;
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
        sbyte[] overrides, dragBase;
        HashSet<int> splits = new HashSet<int>();
        List<Segment> segments = new List<Segment>();
        Plan plan;
        CancelFlag cancel;
        bool updating;
        bool busy;
        double targetMB = 10;
        bool targetAtMax = true;   // the target follows the most it can be (the source's quality) until you move it
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
            top.Controls.Add(PreviewPanel(), 0, 0);
            top.Controls.Add(DetectionPanel(), 1, 0);
            grid.Controls.Add(top, 0, 1);
            grid.SetColumnSpan(top, 4);

            timeline.Height = timeline.PreferredHeight + S(20);
            grid.Controls.Add(timeline, 0, 2);
            grid.SetColumnSpan(timeline, 4);

            // Output (same as cap-compress)
            AddRow(grid, 3, "Target size", target, targetValue, copy);
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

            EventHandler changed = (o, e) => { if (!updating) Recalculate(); };
            target.Scroll += (o, e) => { targetMB = target.Value / 10.0; targetAtMax = target.Value >= target.Maximum; Recalculate(); };
            copy.CheckedChanged += changed;
            res.Scroll += (o, e) => { manualShort = plan.Rungs[res.Value]; Recalculate(); };
            fps.Scroll += (o, e) => { manualFps = fps.Value; Recalculate(); };
            autoRes.CheckedChanged += (o, e) => { if (plan != null) manualShort = plan.Short; changed(o, e); };
            mute.CheckedChanged += (o, e) => { changed(o, e); RestartPlayback(); ScheduleWarm(); };
            encoder.SelectedIndexChanged += changed;

            model.SelectedIndexChanged += (o, e) => { if (!updating) Rescore(); };
            sensitivity.Scroll += (o, e) => Rescore();
            padding.Scroll += (o, e) => Rescore();
            timeline.PlayheadChanged += (o, e) => { ShowPlayhead(); RestartPlayback(); ScheduleWarm(); };
            timeline.RemoveRequested += RemovePart;
            timeline.RangeDragged += DragRange;
            timeline.UndoRequested += Undo;
            resetEdits.Click += (o, e) => ResetEdits();
            timeline.PlayRequested += PlayPause;
            timeline.SplitRequested += Split;
            timeline.RangeRequested += SetRange;
            timeline.EdgeDragged += DragEdge;
            play.Click += (o, e) => { PlayPause(); timeline.Focus(); };
            playAll.CheckedChanged += (o, e) => { RestartPlayback(); ScheduleWarm(); };
            playTimer.Tick += (o, e) => PlayTick();
            restartTimer.Tick += (o, e) => { if (timeline.Scrubbing) return; restartTimer.Stop(); if (playing) StartPlayback(); };
            warmTimer.Tick += (o, e) => { warmTimer.Stop(); Warm(); };
            forget.Click += (o, e) => ForgetLearning();
            promptsButton.Click += (o, e) => EditPrompts();

            browse.Click += (o, e) => Browse();
            export.Click += (o, e) => StartExport();
            cancelButton.Click += (o, e) => { if (cancel != null) { cancel.Cancel(); status.Text = "Cancelling..."; } };
            showFile.Click += (o, e) => { if (lastOutput != null) Process.Start("explorer.exe", "/select,\"" + lastOutput + "\""); };
            AcceptDrops(this);
            Shown += (o, e) => { Retrain(); InitTools(); };
            FormClosing += (o, e) => { StopPlayback(); DropWarm(); SaveSession(); if (cancel != null) cancel.Cancel(); };
            ConfirmReplace = AskReplace;

            // Carry on where the last session left off.
            updating = true;
            model.SelectedIndex = Math.Max(0, Math.Min(2, session.Model));
            sensitivity.Value = Math.Max(0, Math.Min(100, session.Sensitivity));
            padding.Value = Math.Max(0, Math.Min(40, session.Padding));
            learn.Checked = session.Learn;
            playAll.Checked = session.PlayAll;
            updating = false;

            SetInputs();
        }

        // ---- session ----

        void SaveEdits()
        {
            if (analysis == null || overrides == null) return;
            try { Edits.Save(analysis.Key, overrides, splits); } catch (Exception) { }
        }

        void SaveSession()
        {
            SaveEdits();
            session.Model = model.SelectedIndex;
            session.Sensitivity = sensitivity.Value;
            session.Padding = padding.Value;
            session.Learn = learn.Checked;
            session.PlayAll = playAll.Checked;
            if (video != null && analysis != null)
            {
                session.Video = video.Path;
                session.Playhead = timeline.Playhead;
                session.ViewStart = timeline.ViewStart;
                session.ViewLength = timeline.Zoomed ? timeline.ViewLength : 0;
                session.Encoder = EncoderId.StartsWith("original:") ? "original" : EncoderId;
                session.Mute = mute.Checked;
                session.Copy = copy.Checked;
                session.TargetAtMax = targetAtMax;
                session.TargetMB = targetMB;
                session.AutoRes = autoRes.Checked;
                session.Short = manualShort;
                session.Fps = manualFps;
            }
            try { session.Save(); } catch (Exception) { }
        }

        // The output settings the closed session had for this video.
        void RestoreOutput(Session r)
        {
            updating = true;
            autoRes.Checked = r.AutoRes;
            mute.Checked = r.Mute && video.HasAudio;
            copy.Checked = r.Copy;
            int index = encoderIds.FindIndex(id => id == r.Encoder || (r.Encoder == "original" && id.StartsWith("original:")));
            if (index >= 0) encoder.SelectedIndex = index;
            updating = false;
            targetAtMax = r.TargetAtMax;
            targetMB = r.TargetMB;
            if (r.Short > 0) manualShort = r.Short;
            if (r.Fps > 0) manualFps = r.Fps;
        }

        bool AskReplace(string path)
        {
            using (var d = new ThemedDialog("Open another video", new string[,]
            {
                { null, "Open " + Path.GetFileName(path) + "?" },
                { null, "Your edits to " + Path.GetFileName(video.Path) + " are saved and come back when you open it again." }
            }, "Open", "Cancel", "Don't ask again"))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                if (d.Option.Checked) session.AskBeforeReplacing = false;
                return true;
            }
        }

        Control PreviewPanel()
        {
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 0, S(14), 0) };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            p.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            preview.Margin = new Padding(0, 0, 0, S(8));
            p.Controls.Add(preview, 0, 0);
            p.SetColumnSpan(preview, 3);
            play.Anchor = AnchorStyles.Left;
            play.MinimumSize = new Size(S(84), 0);
            p.Controls.Add(play, 0, 1);
            playAll.Anchor = AnchorStyles.Left;
            playAll.Margin = new Padding(S(12), 0, 0, 0);
            p.Controls.Add(playAll, 1, 1);
            hint.Text = "Drag band to add · right-click removes · Ctrl+Z undo";
            hint.ForeColor = Theme.Faint;
            hint.AutoEllipsis = true;
            hint.AutoSize = false;
            hint.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            hint.Height = S(20);
            hint.TextAlign = ContentAlignment.MiddleRight;
            p.Controls.Add(hint, 2, 1);
            return p;
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
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
            promptsButton.Margin = new Padding(0, 0, S(8), 0);
            row.Controls.Add(promptsButton);
            forget.Margin = new Padding(0);
            row.Controls.Add(forget);
            resetEdits.Margin = new Padding(S(8), 0, 0, 0);
            row.Controls.Add(resetEdits);
            p.Controls.Add(row, 0, 5);
            p.SetColumnSpan(row, 3);

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
            hasNvenc = Engine.HasEncoder(ffmpeg, "h264_nvenc");
            FillEncoders();
            status.Text = "Ready.";
            if (startupFile != null) LoadVideo(startupFile);
            else if (!string.IsNullOrEmpty(session.Video) && File.Exists(session.Video)) { restore = session; LoadVideo(session.Video); }
        }

        // x264 and NVENC write H.264 MP4; Original keeps the source's codec and container.
        void FillEncoders()
        {
            string was = encoder.SelectedIndex >= 0 && encoder.SelectedIndex < encoderIds.Count ? encoderIds[encoder.SelectedIndex] : null;
            updating = true;
            encoder.Items.Clear();
            encoderIds.Clear();
            encoder.Items.Add("x264 · best quality");
            encoderIds.Add("libx264");
            if (hasNvenc) { encoder.Items.Add("NVENC · fast"); encoderIds.Add("h264_nvenc"); }
            string original = video == null ? null : Engine.OriginalEncoder(ffmpeg, video);
            if (original != null)
            {
                encoder.Items.Add("Original · " + Engine.CodecName(video.VideoCodec) + " " + OriginalExt(video).TrimStart('.').ToUpperInvariant());
                encoderIds.Add("original:" + original);
            }
            int index = was == null ? -1 : encoderIds.FindIndex(id => id == was || (id.StartsWith("original:") && was.StartsWith("original:")));
            encoder.SelectedIndex = Math.Max(0, index);
            updating = false;
        }

        // The source's container when it can hold the codec; otherwise MKV.
        static string OriginalExt(VideoInfo v)
        {
            string e = v.Ext;
            return e == ".mp4" || e == ".mkv" || e == ".mov" || e == ".m4v" ? e : ".mkv";
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
                learnInfo.Text = string.Format("Not trained yet: SigLIP 2 judges frames with {0}, and DINOv2 learns from SigLIP's surest frames. Fix the timeline and export a few videos to teach both.",
                    PromptSet.Current.Custom ? "your prompts" : "the built-in prompts");
            forget.Enabled = !busy && learner.Videos > 0;
        }

        // ---- prompts ----

        void EditPrompts()
        {
            if (busy) return;
            StopPlayback();
            using (var d = new PromptsDialog(PromptSet.Current, learner != null && learner.Trained))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                ApplyPrompts(d.Action, d.Idle, null);
            }
        }

        // Embeds only the prompts that are new, then rescores from the cached frames: no re-analysis needed.
        void ApplyPrompts(List<string> action, List<string> idle, Action<string> done)
        {
            var builtIn = PromptSet.BuiltIn();
            Action<string> finish = error =>
            {
                if (error == null) status.Text = PromptSet.Current.Custom ? "Using your prompts." : "Using the built-in prompts.";
                UpdateLearnInfo();
                Rescore();
                if (done != null) done(error);
            };
            if (action.SequenceEqual(builtIn.Action) && idle.SequenceEqual(builtIn.Idle)) { PromptSet.Reset(); finish(null); return; }

            var known = new Dictionary<string, float[]>();
            foreach (var set in new[] { builtIn, PromptSet.Current })
            {
                for (int i = 0; i < set.Action.Count; i++) known[set.Action[i]] = set.ActionEmb[i];
                for (int i = 0; i < set.Idle.Count; i++) known[set.Idle[i]] = set.IdleEmb[i];
            }
            var missing = action.Concat(idle).Where(t => !known.ContainsKey(t)).Distinct().ToList();
            Action save = () =>
            {
                var set = new PromptSet();
                foreach (var t in action) { set.Action.Add(t); set.ActionEmb.Add(known[t]); }
                foreach (var t in idle) { set.Idle.Add(t); set.IdleEmb.Add(known[t]); }
                PromptSet.Save(set);
            };
            if (missing.Count == 0) { save(); finish(null); return; }

            if (!TextEncoder.Present())
            {
                bool download = ThemedDialog.Ask(this, "Text model needed", new string[,]
                {
                    { null, "New prompts are read by SigLIP 2's text half, which IdleTrim only needs while you edit them." },
                    { "Size", "about 600 MB, from Hugging Face" },
                    { null, "Download it into the models folder next to this app?" }
                }, "Download", "Not now");
                if (!download) { status.Text = "Prompts not changed: the text model is needed to read new ones."; if (done != null) done("no text model"); return; }
                RunBusy("Downloading the text model", (flag, report) => TextEncoder.Download((pct, name) => report(pct / 100.0, "Downloading " + name), flag), error =>
                {
                    if (error != null) { status.Text = "Couldn't download the text model: " + error; if (done != null) done(error); return; }
                    ApplyPrompts(action, idle, done);
                });
                return;
            }

            RunBusy("Reading the prompts", (flag, report) =>
            {
                report(0, "Loading the text model");
                using (var enc = new TextEncoder())
                {
                    var embs = enc.Embed(missing);
                    for (int i = 0; i < missing.Count; i++) known[missing[i]] = embs[i];
                }
                save();
            }, error =>
            {
                if (error != null) status.Text = "Couldn't read the prompts: " + error;
                progress.Value = 0;
                finish(error);
            });
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
            bool another = video != null && analysis != null && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(video.Path), StringComparison.OrdinalIgnoreCase);
            if (another && session.AskBeforeReplacing && !ConfirmReplace(path)) return;
            StopPlayback();
            DropWarm();
            SaveEdits();
            if (restore != null && !string.Equals(restore.Video, path, StringComparison.OrdinalIgnoreCase)) restore = null;
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
            targetAtMax = true;
            FillEncoders();
            if (restore != null) RestoreOutput(restore);
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
            splits = new HashSet<int>();
            undo.Clear();
            sbyte[] savedOverrides;
            HashSet<int> savedSplits;
            if (a != null && Edits.Load(a.Key, a.Count, out savedOverrides, out savedSplits)) { overrides = savedOverrides; splits = savedSplits; }
            timeline.SetAnalysis(a);
            Rescore();
            if (a != null && restore != null)
            {
                timeline.Playhead = Math.Min(restore.Playhead, a.Duration);
                if (restore.ViewLength > 0) timeline.SetView(restore.ViewStart, restore.ViewLength);
                ShowPlayhead();
                ScheduleWarm();
                restore = null;
            }
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
                timeline.SetResult(null, null, null, null, Threshold);
                keptInfo.Text = "";
                ShowPlayhead();
                Recalculate();
                return;
            }
            var source = model.SelectedIndex == 0 ? Source.Siglip : model.SelectedIndex == 1 ? Source.Dino : Source.Both;
            scores = Scoring.Scores(analysis, source, learner);
            keep = Scoring.Mask(scores, Threshold, PadSeconds, overrides);
            segments = Scoring.Segments(keep, analysis.Duration);
            timeline.SetResult(scores, keep, overrides, splits, Threshold);
            double kept = Segment.Total(segments);
            keptInfo.Text = segments.Count == 0
                ? "Nothing kept. Raise the sensitivity or click the band under the graph."
                : string.Format("Keeps {0} of {1} in {2} part{3}", Time(kept), Time(analysis.Duration), segments.Count, segments.Count == 1 ? "" : "s");
            if (!playing || player == null) ShowPlayhead();
            Recalculate();
            RestartPlayback();
            ScheduleWarm();
        }

        // ---- editing; every change can be undone with Ctrl+Z ----

        void PushUndo()
        {
            undo.Push(Tuple.Create((sbyte[])overrides.Clone(), new HashSet<int>(splits)));
            if (undo.Count > 100)
            {
                var keepLast = undo.Take(100).Reverse().ToList();
                undo.Clear();
                foreach (var u in keepLast) undo.Push(u);
            }
        }

        void Undo()
        {
            if (analysis == null || busy || undo.Count == 0) return;
            var u = undo.Pop();
            overrides = u.Item1;
            splits = u.Item2;
            Rescore();
        }

        // Back to exactly what detection chose.
        void ResetEdits()
        {
            if (analysis == null || busy || (overrides.All(o => o == 0) && splits.Count == 0)) return;
            PushUndo();
            overrides = new sbyte[analysis.Count];
            splits = new HashSet<int>();
            Rescore();
        }

        // Right-click or Delete: the whole part under the step is cut.
        void RemovePart(int step)
        {
            if (analysis == null || keep == null || busy || !keep[step]) return;
            PushUndo();
            int start, end;
            Scoring.RunAt(keep, step, splits, out start, out end);
            for (int i = start; i <= end; i++) overrides[i] = -1;
            Rescore();
        }

        // Dragging across the band: kept (left button) or cut (right) from where the drag began to the pointer.
        // A kept range swallows the splits it covers or touches, so it merges with the parts next to it.
        void DragRange(int start, int end, bool keepIt, bool begin)
        {
            if (analysis == null || busy) return;
            if (begin || dragBase == null)
            {
                PushUndo();
                dragBase = (sbyte[])overrides.Clone();
                splitBase = new HashSet<int>(splits);
            }
            Array.Copy(dragBase, overrides, overrides.Length);
            splits = new HashSet<int>(splitBase);
            start = Math.Max(0, start);
            end = Math.Min(end, overrides.Length);
            for (int i = start; i < end; i++) overrides[i] = keepIt ? (sbyte)1 : (sbyte)-1;
            if (keepIt) splits.RemoveWhere(sp => sp >= start && sp <= end);
            Rescore();
        }

        // S: a part splits in two at the playhead, so each half can be removed on its own. Again on a split removes it.
        void Split(int step)
        {
            if (analysis == null || busy || step <= 0 || step >= analysis.Count) return;
            PushUndo();
            if (!splits.Remove(step)) splits.Add(step);
            Rescore();
        }

        // I/O then K or X: keep or cut exactly the marked steps.
        void SetRange(int start, int end, bool keepIt)
        {
            if (analysis == null || busy) return;
            PushUndo();
            for (int i = Math.Max(0, start); i < Math.Min(end, overrides.Length); i++) overrides[i] = keepIt ? (sbyte)1 : (sbyte)-1;
            if (keepIt) splits.RemoveWhere(sp => sp >= start && sp <= end);
            Rescore();
        }

        // Moving the edge at `boundary` to `to` gives the steps it passes over to the side that grows.
        void DragEdge(int boundary, int to, bool leftKept, bool begin)
        {
            if (analysis == null || busy) return;
            if (begin || dragBase == null) { PushUndo(); dragBase = (sbyte[])overrides.Clone(); }
            Array.Copy(dragBase, overrides, overrides.Length);
            sbyte left = leftKept ? (sbyte)1 : (sbyte)-1;
            if (to > boundary) for (int i = boundary; i < to; i++) overrides[i] = left;
            else for (int i = to; i < boundary; i++) overrides[i] = (sbyte)-left;
            Rescore();
        }

        // ---- playback ----

        void PlayPause()
        {
            if (playing) { PausePlayback(); return; }
            if (analysis == null || video == null || busy) return;
            StartPlayback();
        }

        // The parts to play from the playhead on: the kept parts, or everything with Play cut parts too.
        List<Segment> PlayParts(double from)
        {
            var list = new List<Segment>();
            if (playAll.Checked)
            {
                if (from < analysis.Duration - 0.1) list.Add(new Segment(from, analysis.Duration));
                return list;
            }
            foreach (var seg in segments)
                if (seg.End > from + 0.05) list.Add(new Segment(Math.Max(seg.Start, from), seg.End));
            return list;
        }

        // What a player started now would play; a warm player is used only if this hasn't changed.
        string PlayKey()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0}|{1:0.###}|{2}|{3}", video.Path, timeline.Playhead, playAll.Checked, mute.Checked);
            foreach (var seg in segments) sb.AppendFormat(CultureInfo.InvariantCulture, "|{0:0.###}-{1:0.###}", seg.Start, seg.End);
            return sb.ToString();
        }

        void ScheduleWarm()
        {
            if (playing || analysis == null || ffmpeg == null) return;
            warmTimer.Stop();
            warmTimer.Start();
        }

        void Warm()
        {
            if (playing || busy || analysis == null || video == null || ffmpeg == null) return;
            string key = PlayKey();
            if (warm != null && warmKey == key) return;
            DropWarm();
            var parts = PlayParts(timeline.Playhead);
            if (parts.Count == 0) return;
            warm = new Player(ffmpeg, video, parts, !mute.Checked, false);
            warm.Start();
            warmKey = key;
        }

        void DropWarm()
        {
            warmTimer.Stop();
            if (warm != null) { warm.Dispose(); warm = null; }
            warmKey = null;
        }

        void StartPlayback()
        {
            DisposePlayer();
            if (warm != null && warmKey == PlayKey())
            {
                player = warm;
                warm = null;
                warmKey = null;
                player.Arm();
            }
            else
            {
                DropWarm();
                var parts = PlayParts(timeline.Playhead);
                if (parts.Count == 0) { timeline.Playhead = 0; parts = PlayParts(0); }
                if (parts.Count == 0) { status.Text = "Nothing kept to play."; playing = false; play.Text = "Play"; return; }
                player = new Player(ffmpeg, video, parts, !mute.Checked);
                player.Start();
            }
            playing = true;
            play.Text = "Pause";
            playTimer.Start();
        }

        void PlayTick()
        {
            if (player == null) { if (!playing) playTimer.Stop(); return; }
            var f = player.Next();
            if (f != null)
            {
                preview.ShowFrame(f.Bgr, Player.W, Player.H);
                player.Recycle(f);
                timeline.Playhead = f.Source;
                int step = analysis.StepAt(f.Source);
                preview.Kept = keep == null || keep[step];
                preview.Badge = keep == null ? null : keep[step] ? "KEPT" : "CUT";
                preview.Caption = string.Format("{0} / {1}", Time(f.Source), Time(analysis.Duration));
            }
            if (player.Finished) StopPlayback();
        }

        // Scrubbing or editing while playing: the old player stops at once, so it can't pull the playhead back, the
        // frame at the new spot shows, and playback carries on from there as soon as the drag rests.
        void RestartPlayback()
        {
            if (!playing) return;
            DisposePlayer();
            ShowPlayhead();
            restartTimer.Stop();
            restartTimer.Start();
        }

        void DisposePlayer()
        {
            if (player != null) { player.Dispose(); player = null; }
        }

        // Pause keeps the player and its frame on screen; it waits as the warm player, so Play resumes instantly.
        void PausePlayback()
        {
            restartTimer.Stop();
            playTimer.Stop();
            playing = false;
            play.Text = "Play";
            if (player == null) { ShowPlayhead(); ScheduleWarm(); SetInputs(); return; }
            DropWarm();
            player.Pause();
            warm = player;
            player = null;
            warmKey = PlayKey();
            play.Text = "Play";
            SetInputs();
        }

        void StopPlayback()
        {
            restartTimer.Stop();
            playTimer.Stop();
            bool was = playing;
            playing = false;
            DisposePlayer();
            play.Text = "Play";
            if (was) ShowPlayhead();
            ScheduleWarm();
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
                Encoder = EncoderId.StartsWith("original:") ? EncoderId.Substring(9) : EncoderId,
                Ext = CopyOn || EncoderId.StartsWith("original:") ? OriginalExt(video) : ".mp4",
                Copy = CopyOn,
                Mute = mute.Checked
            };
        }

        string EncoderId { get { return encoder.SelectedIndex >= 0 && encoder.SelectedIndex < encoderIds.Count ? encoderIds[encoder.SelectedIndex] : "libx264"; } }

        // Copying applies only at the top of the target range, where nothing has to shrink.
        bool CopyOn { get { return copy.Checked && targetAtMax; } }

        // Rebuilds the plan and pushes its limits back into every control.
        void Recalculate()
        {
            if (video == null || segments.Count == 0) { plan = null; ClearOutputText(); SetInputs(); return; }
            var settings = ReadSettings();
            if (targetAtMax) settings.TargetMB = double.MaxValue;
            plan = Engine.MakePlan(video, settings);
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

            if (CopyOn)
            {
                targetValue.Text = string.Format("about {0:0.0} MB (max)", video.SizeBytes * plan.Kept / video.Duration / 1048576.0);
                updating = true;
                res.Value = res.Maximum;
                fps.Value = fps.Maximum;
                updating = false;
                resValue.Text = string.Format("{0}x{1}  (source)", video.Width, video.Height);
                fpsValue.Text = string.Format("{0:0.##} fps  (source)", video.Fps);
                bitrate.Text = "Copied as is, no quality loss. Each part starts at the keyframe before its cut, so it may start a moment early.";
            }
            else
            {
                targetValue.Text = plan.CanReduce ? string.Format(targetAtMax ? "{0:0.0} MB (max)" : "{0:0.0} MB (max {1:0.0})", plan.TargetMB, plan.MaxMB) : "not possible";
                resValue.Text = string.Format("{0}x{1}  (max {2}p)", plan.OutLong, plan.Short, plan.ResCap);
                fpsValue.Text = string.Format("{0} fps  (max {1})", plan.Fps, plan.FpsCap);
                bitrate.Text = plan.AudioKbps > 0
                    ? string.Format("Video {0} kbps, audio {1} kbps", plan.VideoKbps, plan.AudioKbps)
                    : string.Format("Video {0} kbps, no audio", plan.VideoKbps);
            }
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
            bool canExport = ready && plan != null && (plan.CanReduce || CopyOn) && segments.Count > 0;
            bool encode = canExport && !CopyOn;
            browse.Enabled = !busy;
            play.Enabled = ready && segments.Count > 0 || playing;
            model.Enabled = sensitivity.Enabled = padding.Enabled = ready;
            timeline.Enabled = ready;
            learn.Enabled = !busy;
            forget.Enabled = !busy && learner != null && learner.Videos > 0;
            promptsButton.Enabled = !busy;
            resetEdits.Enabled = ready && overrides != null && (overrides.Any(o => o != 0) || splits.Count > 0);
            target.Enabled = canExport && plan.CanReduce;
            copy.Enabled = canExport && targetAtMax;
            autoRes.Enabled = encode;
            res.Enabled = encode && !autoRes.Checked && plan.Rungs.Count > 1;
            fps.Enabled = encode && video.MaxFps > plan.MinFps;
            mute.Enabled = loaded && video.HasAudio;
            encoder.Enabled = loaded && !CopyOn;
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
            if (video == null || analysis == null || plan == null || !(plan.CanReduce || CopyOn) || segments.Count == 0 || busy) return;
            StopPlayback();
            DropWarm();
            SaveEdits();
            var settings = ReadSettings();
            var runPlan = plan;
            var source = video;
            var learned = analysis;
            var choice = (bool[])keep.Clone();
            bool teach = learn.Checked;
            string output = Engine.OutputPath(source.Path, settings.Ext);
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
