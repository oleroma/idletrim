// Background tests for IdleTrim. Nothing here takes focus or sends input to the desktop.
// Args: [<video> <out dir>]. With a video: real analysis with both models, learning, exports and the window driven in-process.
// Set IDLETRIM_MODELS to a folder with the models; data and cache go to <out dir>\data.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IdleTrim
{
    static class Tests
    {
        static int failed;

        static void Check(bool ok, string name)
        {
            Console.WriteLine((ok ? "ok   " : "FAIL ") + name);
            if (!ok) failed++;
        }

        [STAThread]
        static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            // The app runs with invariant globalization, so decimal commas can't reach ffmpeg.
            Detection();
            if (args.Length >= 3 && args[2] == "player")
            {
                string ff = Engine.FindTool("ffmpeg");
                var pv = Engine.Probe(Engine.FindTool("ffprobe"), args[0]);
                PlayerTest(ff, pv, null);
                PlayerTest(ff, pv, null);
            }
            else if (args.Length >= 3 && args[2] == "window")
            {
                // Quick rerun of the window part with the analysis cached by an earlier full run.
                Environment.SetEnvironmentVariable("IDLETRIM_DATA", Path.Combine(args[1], "data"));
                Window(args[0], args[1], null);
            }
            else if (args.Length >= 2)
            {
                Directory.CreateDirectory(args[1]);
                Environment.SetEnvironmentVariable("IDLETRIM_DATA", Path.Combine(args[1], "data"));
                if (Directory.Exists(Path.Combine(args[1], "data"))) Directory.Delete(Path.Combine(args[1], "data"), true);
                RealVideo(args[0], args[1]);
            }
            Console.WriteLine(failed == 0 ? "ALL PASSED" : failed + " FAILED");
            return failed;
        }

        static float[] Series(params float[] v) { return v; }

        static void Detection()
        {
            // One spike at 10 s with 1.5 s padding keeps about 8.5..12.5 s.
            var s = new float[60];
            s[20] = 1;
            var keep = Scoring.Mask(s, 0.5, 1.5, null);
            var segs = Scoring.Segments(keep, 30);
            Check(segs.Count == 1 && Math.Abs(segs[0].Start - 8.5) < 0.01 && Math.Abs(segs[0].End - 12.5) < 0.01,
                  "padding grows an action: " + string.Join(", ", segs.Select(x => x.Start + "-" + x.End)));

            // Two actions 1.5 s apart join; 6 s apart stay separate.
            s = new float[80];
            s[10] = 1; s[17] = 1; s[40] = 1;
            segs = Scoring.Segments(Scoring.Mask(s, 0.5, 0.5, null), 40);
            Check(segs.Count == 2, "short gaps fill, long gaps stay: " + segs.Count + " parts");

            // Overrides win over the automatic choice.
            var o = new sbyte[80];
            o[60] = 1; o[61] = 1; o[62] = 1;
            o[10] = -1;
            keep = Scoring.Mask(s, 0.5, 0.5, o);
            Check(keep[60] && keep[62] && !keep[10], "manual keep and cut override detection");

            // RunAt finds the whole part.
            int a, b;
            Scoring.RunAt(new[] { false, true, true, true, false }, 2, out a, out b);
            Check(a == 1 && b == 3, "run under the playhead");
            Scoring.RunAt(new[] { false, true, true, true, true, false }, 3, new HashSet<int> { 3 }, out a, out b);
            Check(a == 3 && b == 4, "a split ends the run");

            var spread = Scoring.Spread(Series(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20));
            Check(spread[0] == 0 && spread[19] == 1 && spread[10] > 0.4f && spread[10] < 0.6f, "spread maps the video's own range to 0..1");
            var smooth = Scoring.Smooth(Series(0, 0, 5, 0, 0), 1);
            Check(Math.Abs(smooth[2] - 5 / 3f) < 1e-4 && Math.Abs(smooth[0]) < 1e-6, "smoothing");
            Check(Scoring.Segments(new bool[0], 0).Count == 0, "empty video has no parts");
        }

        static void RealVideo(string path, string outDir)
        {
            string ffmpeg = Engine.FindTool("ffmpeg"), ffprobe = Engine.FindTool("ffprobe");
            var v = Engine.Probe(ffprobe, path);
            Check(Models.Present(), "models found in " + Models.Dir);
            if (!Models.Present()) return;

            TextTest();

            var sw = Stopwatch.StartNew();
            var models = new Models();
            Console.WriteLine(string.Format("models loaded on {0} in {1:0.0}s", models.OnGpu ? "GPU" : "CPU", sw.Elapsed.TotalSeconds));
            Check(models.OnGpu, "models run on the GPU");

            sw.Restart();
            var a = Analyzer.Run(ffmpeg, v, models, f => { }, new CancelFlag());
            double took = sw.Elapsed.TotalSeconds;
            Console.WriteLine(string.Format("analysed {0:0.0}s of video in {1:0.0}s ({2:0.0}x real time), {3} steps", v.Duration, took, v.Duration / took, a.Count));
            Check(Math.Abs(a.Count - v.Duration / Analysis.Step) <= 2, "one step per half second");
            Check(a.Siglip.All(e => Math.Abs(Models.Dot(e, e) - 1) < 1e-3) && a.Dino.All(e => Math.Abs(Models.Dot(e, e) - 1) < 1e-3), "embeddings are unit length");
            Check(a.ZeroShot.Max() - a.ZeroShot.Min() > 0.02, "zero-shot score varies: " + a.ZeroShot.Min().ToString("0.000") + ".." + a.ZeroShot.Max().ToString("0.000"));
            Check(a.Motion.Max() > 0, "motion measured");
            using (var ms = new MemoryStream(a.Previews[a.Count / 2]))
            using (var img = new Bitmap(ms))
                Check(img.Width == 320 && img.Height == 180, "preview frames decode");
            File.WriteAllLines(Path.Combine(outDir, "zero.csv"), a.ZeroShot.Select((z, i) => i + "," + z.ToString(CultureInfo.InvariantCulture) + "," + a.Motion[i].ToString(CultureInfo.InvariantCulture)));

            a.Save();
            var back = Analysis.Load(a.Key);
            Check(back != null && back.Count == a.Count && back.Siglip[5].SequenceEqual(a.Siglip[5]) && back.Previews[7].SequenceEqual(a.Previews[7]), "cache round trip");

            // Untrained DINOv2 learns from SigLIP's surest frames: it should agree with SigLIP, not just follow motion.
            var zs = Scoring.ZeroShot(a);
            var dl = Scoring.DinoFromSiglip(a);
            double cz = Corr(dl, zs), cm = Corr(dl, a.Motion);
            Console.WriteLine(string.Format("untrained DINOv2 vs SigLIP {0:0.00}, vs motion {1:0.00}", cz, cm));
            Check(cz > 0.5, "untrained DINOv2 follows what SigLIP sees");

            // Untrained scores for each model choice.
            foreach (Source src in Enum.GetValues(typeof(Source)))
            {
                var sc = Scoring.Scores(a, src, null);
                var kept = Scoring.Segments(Scoring.Mask(sc, 0.5, 1.5, null), a.Duration);
                Console.WriteLine(string.Format("untrained {0}: keeps {1:0.0}s of {2:0.0}s in {3} parts", src, Segment.Total(kept), a.Duration, kept.Count));
            }

            // Learning: teach a made-up rule (second half is action) and check both models pick it up.
            var labels = new bool[a.Count];
            for (int i = a.Count / 2; i < a.Count; i++) labels[i] = true;
            Learner.AddExample(a, labels);
            sw.Restart();
            var learner = Learner.Train();
            Console.WriteLine(string.Format("trained on {0} steps in {1:0.0}s", learner.Kept + learner.Cut, sw.Elapsed.TotalSeconds));
            Check(learner.Trained && learner.Videos == 1, "learner trained from one export");
            foreach (Source src in Enum.GetValues(typeof(Source)))
            {
                var sc = Scoring.Scores(a, src, learner);
                int right = 0;
                for (int i = 0; i < a.Count; i++) if ((sc[i] >= 0.5) == labels[i]) right++;
                Check(right > a.Count * 0.85, string.Format("{0} learned the rule: {1:0}% right", src, 100.0 * right / a.Count));
            }
            Learner.Forget();
            Check(!Learner.Train().Trained, "forget clears learning");

            // Export: joined parts under the cap, x264 and NVENC, with and without audio.
            var scores = Scoring.Scores(a, Source.Both, null);
            var segs = Scoring.Segments(Scoring.Mask(scores, 0.5, 1.5, null), a.Duration);
            Export(ffmpeg, ffprobe, v, new Settings { TargetMB = 6, Segments = segs, AutoFps = false, Fps = 24 }, Path.Combine(outDir, "x264.mp4"));
            Export(ffmpeg, ffprobe, v, new Settings { TargetMB = 8, Segments = segs, AutoFps = false, Fps = 30, Nvenc = true }, Path.Combine(outDir, "nvenc.mp4"));
            Export(ffmpeg, ffprobe, v, new Settings { TargetMB = 3, Segments = segs.Take(2).ToList(), AutoFps = false, Fps = 24, Mute = true }, Path.Combine(outDir, "muted.mp4"));

            // Original format: same codec and container as the source, still under the cap.
            string original = Engine.OriginalEncoder(ffmpeg, v);
            Console.WriteLine("source " + v.VideoCodec + "/" + v.AudioCodec + " " + v.Ext + ", original encoder " + original);
            Check(original != null, "an encoder for the source's own codec");
            if (original != null)
            {
                string o = Path.Combine(outDir, "original" + v.Ext);
                Export(ffmpeg, ffprobe, v, new Settings { TargetMB = 8, Segments = segs, AutoFps = false, Fps = 30, Encoder = original, Ext = v.Ext }, o);
                Check(Engine.Probe(ffprobe, o).VideoCodec == v.VideoCodec, "original keeps the codec: " + Engine.Probe(ffprobe, o).VideoCodec);
            }

            // Stream copy: no re-encode, parts start at keyframes, so slightly longer, never bigger than the source.
            {
                string o = Path.Combine(outDir, "copy" + v.Ext);
                File.Delete(o);
                var cs = new Settings { Segments = segs, Copy = true, Ext = v.Ext };
                sw.Restart();
                long size = Engine.Run(ffmpeg, v, cs, Engine.MakePlan(v, cs), o, (f, m) => { }, new CancelFlag());
                var info = Engine.Probe(ffprobe, o);
                double kept = Segment.Total(segs);
                Console.WriteLine(string.Format("copy{0}: {1:0.0}s kept -> {2:0.0}s, {3:0.0} MB, {4}x{5} {6}, took {7:0.0}s",
                    v.Ext, kept, info.Duration, size / 1048576.0, info.Width, info.Height, info.VideoCodec, sw.Elapsed.TotalSeconds));
                Check(info.Width == v.Width && info.VideoCodec == v.VideoCodec && info.HasAudio, "copy keeps the source streams");
                Check(info.Duration >= kept - 0.5 && info.Duration <= kept + 2.5 * segs.Count, "copy length is the kept parts, give or take a keyframe each");
                Check(size < v.SizeBytes * (info.Duration / v.Duration) * 1.1 && size < v.SizeBytes, "copy is never bigger than the source");
            }

            PlayerTest(ffmpeg, v, segs);

            Window(path, outDir, a);
        }

        static void Export(string ffmpeg, string ffprobe, VideoInfo v, Settings s, string output)
        {
            File.Delete(output);
            var plan = Engine.MakePlan(v, s);
            var sw = Stopwatch.StartNew();
            long size = Engine.Run(ffmpeg, v, s, plan, output, (f, m) => { }, new CancelFlag());
            var o = Engine.Probe(ffprobe, output);
            Console.WriteLine(string.Format("{0}: {1} parts, {2:0.0}s kept -> {3:0.0}s, {4:0.00} MB, {5}x{6} {7:0.#} fps, audio={8}, took {9:0}s",
                Path.GetFileName(output), s.Segments.Count, plan.Kept, o.Duration, size / 1048576.0, o.Width, o.Height, o.Fps, o.HasAudio, sw.Elapsed.TotalSeconds));
            string n = Path.GetFileName(output);
            Check(size <= (long)(plan.TargetMB * 1048576), n + " under target");
            Check(Math.Abs(o.Duration - plan.Kept) < 0.5, n + " length is the kept parts joined");
            Check(o.HasAudio == (plan.AudioKbps > 0), n + " audio as planned");
        }

        static double Corr(float[] x, float[] y)
        {
            double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
            for (int i = 0; i < x.Length; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); syy += (y[i] - my) * (y[i] - my); }
            return sxy / Math.Sqrt(sxx * syy + 1e-12);
        }

        // The C# tokenizer matches Hugging Face's (ids from the Python tokenizers package), and the C# text model
        // reproduces the built-in prompt embeddings made in Python.
        static void TextTest()
        {
            Check(TextEncoder.Present(), "text model found");
            if (!TextEncoder.Present()) return;
            var sw = Stopwatch.StartNew();
            using (var enc = new TextEncoder())
            {
                Console.WriteLine(string.Format("text model and tokenizer loaded in {0:0.0}s", sw.Elapsed.TotalSeconds));
                var cases = new Dictionary<string, int[]>
                {
                    { "a final kill cam with a slow motion replay", new[] { 235250, 2048, 9451, 2554, 675, 476, 6080, 8252, 58659, 1 } },
                    { "  double  spaces and tabs\there", new[] { 139, 4576, 139, 46633, 578, 42607, 226, 1828, 1 } },
                    { "\u017ei\u017ekov \U0001F3AE \u00fcber na\u00efve \u2014 \u65e5\u672c\u8a9e", new[] { 20778, 235525, 11454, 235248, 243664, 5149, 155245, 2062, 26867, 236333, 1 } },
                    { new string('x', 80), new[] { 223505, 223505, 223505, 223505, 223505, 1 } },
                };
                foreach (var c in cases)
                {
                    var ids = enc.Tokens(c.Key);
                    bool same = ids.Take(c.Value.Length).SequenceEqual(c.Value) && ids.Skip(c.Value.Length).All(i => i == 0) && ids.Length == TextEncoder.Length;
                    Check(same, "tokens match Hugging Face: " + c.Key.Substring(0, Math.Min(24, c.Key.Length)) + (same ? "" : " got " + string.Join(",", ids.Take(12))));
                }
                var builtIn = PromptSet.BuiltIn();
                sw.Restart();
                var embs = enc.Embed(builtIn.Action.Concat(builtIn.Idle).ToList());
                var stored = builtIn.ActionEmb.Concat(builtIn.IdleEmb).ToList();
                double worst = Enumerable.Range(0, embs.Length).Min(i => Models.Dot(embs[i], stored[i]));
                Console.WriteLine(string.Format("embedded {0} prompts in {1:0.00}s, worst match with Python {2:0.0000}", embs.Length, sw.Elapsed.TotalSeconds, worst));
                Check(worst > 0.995, "C# text embeddings match the Python ones");
            }
        }

        // Plays two parts with a cut between them and checks frames come in order, jump the cut and keep time with the audio.
        static void PlayerTest(string ffmpeg, VideoInfo v, List<Segment> segs)
        {
            Player.Volume = 0;
            var parts = new List<Segment> { new Segment(10, 12), new Segment(40, 42) };
            var sw = Stopwatch.StartNew();
            var shown = new List<Player.Frame>();
            double firstAt = -1;
            byte[] firstFrame = null, afterCut = null;
            using (var player = new Player(ffmpeg, v, parts, true))
            {
                player.Start();
                while (!player.Finished && sw.Elapsed.TotalSeconds < 15)
                {
                    var f = player.Next();
                    if (f != null)
                    {
                        if (firstAt < 0) { firstAt = sw.Elapsed.TotalSeconds; firstFrame = (byte[])f.Bgr.Clone(); }
                        if (afterCut == null && f.Source >= 40) afterCut = (byte[])f.Bgr.Clone();
                        shown.Add(new Player.Frame { At = f.At, Source = f.Source });
                        player.Recycle(f);
                    }
                    Thread.Sleep(10);
                }
                double total = sw.Elapsed.TotalSeconds - firstAt;
                Console.WriteLine(string.Format("player: first frame after {0:0.00}s, {1} frames shown, 4 s played in {2:0.00}s", firstAt, shown.Count, total));
                Check(player.Finished, "playback finishes");
                Check(shown.Count > 4 * 30 * 0.8, "playback shows most frames: " + shown.Count);
                Check(shown.Zip(shown.Skip(1), (x, y) => y.At > x.At).All(ok => ok), "frames in order");
                Check(shown.All(f => (f.Source >= 10 && f.Source < 12.01) || (f.Source >= 40 && f.Source < 42.01)), "only kept parts play");
                Check(shown.Any(f => f.Source >= 40), "playback jumps over the cut");
                Check(total > 3.6 && total < 5.0, string.Format("plays in real time: {0:0.00}s for 4 s", total));
            }
            // Each part starts on its exact frame: same as a plain ffmpeg seek there, unlike the frame half a second before.
            foreach (var probe in new[] { Tuple.Create(10.0, firstFrame), Tuple.Create(40.0, afterCut) })
            {
                Func<double, byte[]> grab = t =>
                {
                    var psi = new ProcessStartInfo(ffmpeg, string.Format(CultureInfo.InvariantCulture,
                        "-hide_banner -loglevel error -nostdin -skip_loop_filter all -flags2 fast -ss {0:0.###} -i \"{1}\" -frames:v 1 -an -vf scale={2}:{3} -pix_fmt bgr24 -f rawvideo pipe:1", t, v.Path, Player.W, Player.H))
                    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                    using (var p = Process.Start(psi)) using (var ms = new MemoryStream()) { p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit(); return ms.ToArray(); }
                };
                Func<byte[], byte[], double> diff = (x, y) => { if (x == null || y == null || x.Length != y.Length) return 999; double d = 0; for (int i = 0; i < x.Length; i++) d += Math.Abs(x[i] - y[i]); return d / x.Length; };
                // Same decoder settings, so the same frame should match closely; neighbours a frame either side shouldn't.
                double fd = 1 / v.Fps;
                double same = diff(probe.Item2, grab(probe.Item1)), before = diff(probe.Item2, grab(probe.Item1 - fd)), after = diff(probe.Item2, grab(probe.Item1 + fd)), early = diff(probe.Item2, grab(probe.Item1 - 0.5));
                Console.WriteLine(string.Format("part at {0}s: first frame differs {1:0.0} from an exact seek, {2:0.0}/{3:0.0} from the frame before/after, {4:0.0} from 0.5 s earlier", probe.Item1, same, before, after, early));
                Check(same < 2 && same <= before && same <= after, "playback starts each part on its exact frame (" + probe.Item1 + " s)");
            }
        }

        // ---- the window, driven in-process ----

        static MainForm form;
        static T Get<T>(string name) { return (T)typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form); }
        static void Call(string name, params object[] args) { typeof(MainForm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, args); }

        static void Shot(string dir, string name)
        {
            using (var b = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                b.Save(Path.Combine(dir, name + ".png"));
            }
        }

        static void Window(string path, string outDir, Analysis cached)
        {
            DarkForm.Quiet = true;
            form = new MainForm(path) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
            form.Shown += async (o, e) =>
            {
                try { await Drive(outDir); }
                catch (Exception ex) { Console.WriteLine("CRASH " + ex); failed++; }
                form.Close();
            };
            Application.Run(form);
        }

        static async Task Drive(string outDir)
        {
            for (int i = 0; i < 100 && Get<Analysis>("analysis") == null; i++) await Task.Delay(100);
            await Task.Delay(500);
            var a = Get<Analysis>("analysis");
            Check(a != null, "window loads the cached analysis");
            if (a == null) return;
            var keep = Get<bool[]>("keep");
            Check(keep != null && keep.Length == a.Count, "window shows a keep/cut choice");
            var target = Get<Slider>("target");
            Check(Get<bool>("targetAtMax") && target.Value == target.Maximum, "target starts at max: " + Get<Label>("targetValue").Text);
            Check((bool)typeof(MainForm).GetProperty("CopyOn", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form), "at max it copies without re-encoding");
            var enc = Get<Segmented>("encoder");
            Check(enc.Items.Cast<object>().Any(i => i.ToString().StartsWith("Original")), "Original is offered: " + string.Join(" | ", enc.Items.Cast<object>()));
            Shot(outDir, "1-loaded");

            var sens = Get<Slider>("sensitivity");
            sens.UserSet(90);
            double highKept = Segment.Total(Get<List<Segment>>("segments"));
            sens.UserSet(10);
            double lowKept = Segment.Total(Get<List<Segment>>("segments"));
            Check(highKept > lowKept, string.Format("more sensitivity keeps more: {0:0.0}s vs {1:0.0}s", highKept, lowKept));
            sens.UserSet(50);

            var model = Get<Segmented>("model");
            foreach (int m in new[] { 0, 1, 2 })
            {
                model.SelectedIndex = m;
                Console.WriteLine(string.Format("model {0}: {1}", model.Items[m], Get<Label>("keptInfo").Text));
            }

            // Remove a kept part, undo it.
            keep = Get<bool[]>("keep");
            var original = (bool[])keep.Clone();
            var segs0 = Get<List<Segment>>("segments");
            Func<int, int> stepOf = sec => a.StepAt(sec);
            int step = stepOf((int)((segs0[0].Start + segs0[0].End) / 2));
            Call("RemovePart", step);
            Check(!Get<bool[]>("keep")[step] && Get<List<Segment>>("segments").Count == segs0.Count - 1, "removing a part cuts all of it");
            var timeline = Get<Timeline>("timeline");
            timeline.Playhead = a.TimeOf(step);
            Call("ShowPlayhead");
            Check(Get<Preview>("preview").Badge == "CUT", "preview badge follows the playhead");
            Call("Undo");
            Check(Get<bool[]>("keep").SequenceEqual(original), "undo brings it back");

            // Drag across the gap between two kept parts: they merge into one.
            if (segs0.Count >= 2)
            {
                int g0 = stepOf((int)segs0[0].End), g1 = stepOf((int)segs0[1].Start);
                Call("DragRange", g0 - 2, g0 + 1, true, true);
                Check(Get<List<Segment>>("segments").Count == segs0.Count, "a short drag only grows the part");
                Call("DragRange", g0 - 2, g1 + 2, true, false);
                Check(Get<List<Segment>>("segments").Count == segs0.Count - 1, string.Format("dragging into the next part merges them: {0} -> {1} parts", segs0.Count, Get<List<Segment>>("segments").Count));
                Call("Undo");
                Check(Get<bool[]>("keep").SequenceEqual(original), "one undo takes back the whole drag");
            }
            else Check(false, "found two parts to merge");

            // Drag the start of a kept part: left grows it, right shrinks it, both measured from where the drag began.
            int edge = Enumerable.Range(5, a.Count - 10).FirstOrDefault(i => !original[i - 1] && original[i] && original.Skip(i).Take(5).All(x => x));
            if (edge > 0)
            {
                Call("DragEdge", edge, edge - 4, false, true);
                bool grew = Get<bool[]>("keep")[edge - 4];
                Call("DragEdge", edge, edge + 3, false, false);
                var k = Get<bool[]>("keep");
                Check(grew && !k[edge - 4] && !k[edge + 2] && k[edge + 3], "dragging a part's edge moves it both ways");
                Call("Undo");
            }
            else Check(false, "found a part edge to drag");

            // Split a long kept part, remove only its second half, then drag over the split to join it again.
            int run = Enumerable.Range(0, a.Count - 8).FirstOrDefault(i => original.Skip(i).Take(8).All(x => x));
            Call("Split", run + 4);
            Call("RemovePart", run + 5);
            var ks = Get<bool[]>("keep");
            Check(ks[run + 3] && !ks[run + 4], "a split part is removed on its own side");
            Call("DragRange", run + 2, run + 7, true, true);
            ks = Get<bool[]>("keep");
            Check(Get<HashSet<int>>("splits").Count == 0 && Enumerable.Range(run, 7).All(i => ks[i]), "dragging over a split joins the parts");
            Call("ResetEdits");
            Check(Get<HashSet<int>>("splits").Count == 0 && Get<bool[]>("keep").SequenceEqual(original), "reset edits goes back to detection");

            // Mark a range and cut it.
            Call("SetRange", run + 1, run + 3, false);
            ks = Get<bool[]>("keep");
            Check(ks[run] && !ks[run + 1] && !ks[run + 2] && ks[run + 3], "a marked range is cut exactly");
            Call("ResetEdits");

            // The same through mouse events on the band (sent to the control in-process, not to the desktop).
            {
                int bandY = (int)typeof(Timeline).GetProperty("BandTop", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(timeline) + 3;
                var down = typeof(Control).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
                var move = typeof(Control).GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
                var up = typeof(Control).GetMethod("OnMouseUp", BindingFlags.NonPublic | BindingFlags.Instance);
                Func<double, int> xOf = t => (int)Math.Round((t - timeline.ViewStart) / timeline.ViewLength * (timeline.Width - 1));
                Action<MouseButtons, int, int> drag = (button, x0, x1) =>
                {
                    down.Invoke(timeline, new object[] { new MouseEventArgs(button, 1, x0, bandY, 0) });
                    for (int x = x0; x != x1; x += Math.Sign(x1 - x0)) move.Invoke(timeline, new object[] { new MouseEventArgs(button, 0, x, bandY, 0) });
                    move.Invoke(timeline, new object[] { new MouseEventArgs(button, 0, x1, bandY, 0) });
                    up.Invoke(timeline, new object[] { new MouseEventArgs(button, 1, x1, bandY, 0) });
                };
                var cut = segs0.Count >= 2 ? Tuple.Create(segs0[0].End + 1.5, segs0[1].Start - 1.5) : Tuple.Create(0.0, 0.0);
                if (cut.Item2 - cut.Item1 > 2)
                {
                    drag(MouseButtons.Left, xOf(cut.Item1), xOf(cut.Item2));
                    var kk = Get<bool[]>("keep");
                    Check(kk[stepOf((int)((cut.Item1 + cut.Item2) / 2))] && Get<List<Segment>>("segments").Count == segs0.Count + 1, "mouse: dragging on the band adds a kept part");
                    Shot(outDir, "2a-added");
                    double gapMid = (cut.Item1 + cut.Item2) / 2;
                    drag(MouseButtons.Right, xOf(gapMid), xOf(gapMid));
                    Check(Get<bool[]>("keep").SequenceEqual(original), "mouse: right-click removes it again");
                }
                else Check(false, "found a cut gap for the mouse test");
                Call("ResetEdits");
            }

            // Your own prompts: a new one is read by the text model, the scores change, built-in ones come back.
            var before0 = (float[])Scoring.ZeroShot(a).Clone();
            var builtInSet = PromptSet.BuiltIn();
            var myAction = builtInSet.Action.Take(2).Concat(new[] { "a final kill cam with a slow motion replay" }).ToList();
            var myIdle = new List<string> { "a game menu or loadout screen", "an empty hallway with nobody around" };
            string promptError = "pending";
            var sw2 = Stopwatch.StartNew();
            Call("ApplyPrompts", myAction, myIdle, (Action<string>)(e => promptError = e));
            while (promptError == "pending" && sw2.Elapsed.TotalSeconds < 60) await Task.Delay(100);
            Console.WriteLine(string.Format("new prompts read in {0:0.0}s: {1}", sw2.Elapsed.TotalSeconds, Get<Label>("status").Text));
            Check(promptError == null && PromptSet.Current.Custom && PromptSet.Current.Action.SequenceEqual(myAction) && PromptSet.Current.Idle.SequenceEqual(myIdle), "your prompts are read and saved");
            Check(!Scoring.ZeroShot(a).SequenceEqual(before0), "your prompts change the scores without re-analysis");
            if (Get<Learner>("learner") == null || !Get<Learner>("learner").Trained)
                Check(Get<Label>("learnInfo").Text.Contains("your prompts"), "learning text mentions your prompts");
            using (var dlg = new PromptsDialog(PromptSet.Current, false) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) })
            {
                dlg.Show();
                using (var b = new Bitmap(dlg.Width, dlg.Height)) { dlg.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height)); b.Save(Path.Combine(outDir, "prompts.png")); }
                Check(dlg.Action.SequenceEqual(myAction) && dlg.Idle.SequenceEqual(myIdle), "the editor shows your prompts");
                dlg.Close();
            }
            promptError = "pending";
            Call("ApplyPrompts", builtInSet.Action, builtInSet.Idle, (Action<string>)(e => promptError = e));
            Check(promptError == null && !PromptSet.Current.Custom && Scoring.ZeroShot(a).SequenceEqual(before0), "built-in prompts come back exactly");

            // Play from the window for a moment, silently.
            Player.Volume = 0;
            var tl = Get<Timeline>("timeline");
            tl.Playhead = Get<List<Segment>>("segments")[0].Start;
            Call("ScheduleWarm");
            // The playhead settles and a player warms up there (again, if a retrain or rescore changed the parts meanwhile).
            Func<bool> warmHere = () => Get<Player>("warm") != null && Get<Player>("warm").Ready &&
                Get<string>("warmKey") == (string)typeof(MainForm).GetMethod("PlayKey", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null);
            for (int i = 0; i < 100 && !warmHere(); i++) await Task.Delay(100);
            Console.WriteLine("warm: " + (Get<Player>("warm") == null ? "none" : Get<Player>("warm").Ready ? "ready" : "not ready") + ", key match " + (Get<string>("warmKey") == (string)typeof(MainForm).GetMethod("PlayKey", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, null)));
            Check(Get<Player>("warm") != null && Get<Player>("warm").Ready, "a player is waiting at the playhead");
            var sw3 = Stopwatch.StartNew();
            Call("PlayPause");
            while (!Get<Preview>("preview").Playing && sw3.Elapsed.TotalSeconds < 5) await Task.Delay(5);
            Console.WriteLine(string.Format("play to first frame: {0:0}ms warm", sw3.Elapsed.TotalMilliseconds));
            Check(sw3.Elapsed.TotalMilliseconds < 250, "play starts at once when warm");
            double startHead = tl.Playhead;
            await Task.Delay(4000);
            Check(Get<Player>("player") != null && Get<Preview>("preview").Playing, "play shows video in the preview");
            Check(tl.Playhead > startHead + 1, string.Format("playhead moves while playing: {0:0.0} -> {1:0.0}", startHead, tl.Playhead));
            Shot(outDir, "1b-playing");
            // Click elsewhere on the strip while playing: the marker goes there at once, stays, and playback carries on from it.
            {
                var mdown = typeof(Control).GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
                var mup = typeof(Control).GetMethod("OnMouseUp", BindingFlags.NonPublic | BindingFlags.Instance);
                var seg = Get<List<Segment>>("segments").Last();
                double seekTo = Math.Round(seg.Start + 2);
                int x = (int)Math.Round((seekTo - tl.ViewStart) / tl.ViewLength * (tl.Width - 1));
                double aimed = tl.ViewStart + (double)x / (tl.Width - 1) * tl.ViewLength;
                var sw5 = Stopwatch.StartNew();
                mdown.Invoke(tl, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, 10, 0) });
                mup.Invoke(tl, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, 10, 0) });
                Check(Math.Abs(tl.Playhead - aimed) < 0.01, string.Format("click while playing moves the marker at once: {0:0.00}s", tl.Playhead));
                double lowest = tl.Playhead;
                while (sw5.Elapsed.TotalSeconds < 5 && tl.Playhead < aimed + 0.3) { lowest = Math.Min(lowest, tl.Playhead); await Task.Delay(5); }
                var pl = Get<Player>("player");
                Console.WriteLine(string.Format("seek while playing: moving again after {0:0}ms, from {1:0.00}s (decoders: video {2:0}ms, audio {3:0}ms)",
                    sw5.Elapsed.TotalMilliseconds, aimed, pl == null ? -1 : pl.FirstFrameMs, pl == null ? -1 : pl.AudioReadyMs));
                Check(lowest >= aimed - 0.01, "the marker doesn't jump back to the old spot");
                Check(tl.Playhead >= aimed + 0.3 && tl.Playhead < aimed + 1.5 && Get<bool>("playing"), "playback carries on from the click");
            }
            Call("PlayPause");
            double pausedAt = tl.Playhead;
            await Task.Delay(600);
            Check(Get<Player>("player") == null && Get<Preview>("preview").Playing && Math.Abs(tl.Playhead - pausedAt) < 1e-9, "pause holds the frame and the playhead");
            Check(Get<Player>("warm") != null, "the paused player waits to resume");
            var sw4 = Stopwatch.StartNew();
            Call("PlayPause");
            while (tl.Playhead <= pausedAt && sw4.Elapsed.TotalSeconds < 5) await Task.Delay(2);
            Console.WriteLine(string.Format("resume to next frame: {0:0}ms (from {1:0.00}s to {2:0.00}s)", sw4.Elapsed.TotalMilliseconds, pausedAt, tl.Playhead));
            Check(sw4.Elapsed.TotalMilliseconds < 120 && tl.Playhead - pausedAt < 0.2, "resume is instant and carries on where it paused");
            Call("StopPlayback");
            Check(Get<Player>("player") == null, "playback stops");

            // Another video doesn't replace this one without asking; cancelling keeps everything.
            string other = Directory.GetFiles(Path.GetDirectoryName(Get<VideoInfo>("video").Path), "*.mkv").FirstOrDefault(f => f != Get<VideoInfo>("video").Path);
            if (other != null)
            {
                string asked = null;
                typeof(MainForm).GetField("ConfirmReplace", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, (Func<string, bool>)(f => { asked = f; return false; }));
                string was = Get<VideoInfo>("video").Path;
                Call("LoadVideo", other);
                Check(asked == other && Get<VideoInfo>("video").Path == was && Get<Analysis>("analysis") != null, "opening another video asks first, and Cancel keeps this one");
            }

            // Session: edits, playhead, zoom and settings come back in a new window that opens with no file.
            {
                var segsNow = Get<List<Segment>>("segments");
                int g0 = a.StepAt(segsNow[0].End) + 2;
                Call("DragRange", g0, g0 + 6, true, true);
                Call("Split", a.StepAt(segsNow[segsNow.Count - 1].Start) + 4);
                var savedOverrides = (sbyte[])Get<sbyte[]>("overrides").Clone();
                var savedSplits = new HashSet<int>(Get<HashSet<int>>("splits"));
                sens.UserSet(63);
                tl.SetView(20, 30);
                tl.Playhead = 33.5;
                Call("SaveSession");
                var first = form;
                var second = new MainForm(null) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
                second.Show();
                form = second;
                for (int i = 0; i < 100 && Get<Analysis>("analysis") == null; i++) await Task.Delay(100);
                await Task.Delay(300);
                var tl2 = Get<Timeline>("timeline");
                Check(Get<VideoInfo>("video") != null && Get<VideoInfo>("video").Path == Get<VideoInfo>("video").Path && Get<Analysis>("analysis") != null, "session: the last video opens again");
                Check(Get<sbyte[]>("overrides") != null && Get<sbyte[]>("overrides").SequenceEqual(savedOverrides) && Get<HashSet<int>>("splits").SetEquals(savedSplits), "session: your edits come back");
                Check(Math.Abs(tl2.Playhead - 33.5) < 1e-6 && Math.Abs(tl2.ViewStart - 20) < 1e-6 && Math.Abs(tl2.ViewLength - 30) < 1e-6,
                      string.Format("session: playhead and zoom come back ({0:0.0}s, view {1:0.0}+{2:0.0})", tl2.Playhead, tl2.ViewStart, tl2.ViewLength));
                Check(Get<Slider>("sensitivity").Value == 63, "session: settings come back");
                Shot(outDir, "4-restored");
                second.Close();
                form = first;
                Call("ResetEdits");
                Call("SaveEdits");
                tl.SetView(0, a.Duration);
                sens.UserSet(50);
            }

            var key = typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
            timeline.Playhead = 10;
            key.Invoke(timeline, new object[] { new KeyEventArgs(Keys.Right) });
            Check(Math.Abs(timeline.Playhead - 10.5) < 1e-6, "arrow scrubs half a second");
            key.Invoke(timeline, new object[] { new KeyEventArgs(Keys.Right | Keys.Shift) });
            Check(Math.Abs(timeline.Playhead - 15.5) < 1e-6, "shift+arrow scrubs five seconds");
            Shot(outDir, "2-edited");

            // Ctrl+wheel zooms around the pointer, the wheel scrolls, the view follows the playhead.
            var wheel = typeof(Control).GetMethod("OnMouseWheel", BindingFlags.NonPublic | BindingFlags.Instance);
            Action<int, int, bool> spin = (x, delta, ctrl) =>
            {
                timeline.CtrlIsDown = () => ctrl;
                wheel.Invoke(timeline, new object[] { new HandledMouseEventArgs(MouseButtons.None, 0, x, 20, delta) });
            };
            int mid = timeline.Width / 2;
            double under = timeline.ViewStart + timeline.ViewLength * mid / (timeline.Width - 1);
            spin(mid, 120 * 5, true);
            double len = timeline.ViewLength, underAfter = timeline.ViewStart + len * mid / (timeline.Width - 1);
            Check(timeline.Zoomed && len < a.Duration * 0.4 && Math.Abs(under - underAfter) < 0.05,
                  string.Format("ctrl+wheel zooms around the pointer: {0:0.0}s on screen, {1:0.00}s stays under it", len, under));
            Shot(outDir, "2b-zoomed");
            double start = timeline.ViewStart;
            spin(mid, -120, false);
            Check(timeline.ViewStart > start && Math.Abs(timeline.ViewLength - len) < 1e-6, string.Format("wheel scrolls: {0:0.0}s -> {1:0.0}s", start, timeline.ViewStart));
            spin(mid, 120 * 50, true);
            Check(Math.Abs(timeline.ViewLength - Timeline.MinView) < 1e-6, "zoom stops at " + Timeline.MinView + " s");
            timeline.Playhead = timeline.ViewStart + timeline.ViewLength + 10;
            Check(timeline.Playhead >= timeline.ViewStart && timeline.Playhead <= timeline.ViewStart + timeline.ViewLength, "view follows the playhead");
            spin(mid, -120 * 50, true);
            Check(!timeline.Zoomed && timeline.ViewStart == 0, "ctrl+wheel back out shows the whole video");
            start = timeline.ViewStart;
            var args = new HandledMouseEventArgs(MouseButtons.None, 0, mid, 20, -120);
            timeline.CtrlIsDown = () => false;
            wheel.Invoke(timeline, new object[] { args });
            Check(!args.Handled && timeline.ViewStart == 0, "unzoomed, the wheel scrolls the window as before");

            Get<Btn>("export").PerformClick();
            Check(Get<Btn>("cancelButton").Enabled, "export starts");
            for (int i = 0; i < 1500 && Get<Btn>("cancelButton").Enabled; i++) await Task.Delay(200);
            string status = Get<Label>("status").Text;
            Console.WriteLine("status: " + status);
            Check(status.StartsWith("Done"), "export from the window");
            string made = Get<string>("lastOutput");
            Check(made != null && Path.GetExtension(made) == Path.GetExtension(Get<VideoInfo>("video").Path), "default export keeps the container: " + made);
            for (int i = 0; i < 100 && (Get<Learner>("learner") == null || !Get<Learner>("learner").Trained); i++) await Task.Delay(200);
            Check(Get<Learner>("learner") != null && Get<Learner>("learner").Videos == 1, "export taught the learner");
            Shot(outDir, "3-done");
            // The window saves next to the source; don't leave test output there.
            if (made != null) try { File.Delete(made); } catch { }
        }
    }
}
