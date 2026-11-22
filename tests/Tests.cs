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
            if (args.Length >= 2)
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

            // Flip the part under a step and check it flips, then flip it back.
            keep = Get<bool[]>("keep");
            int step = a.Count / 3;
            bool before = keep[step];
            var original = (bool[])keep.Clone();
            Call("Toggle", step);
            Check(Get<bool[]>("keep")[step] != before, "clicking a part flips it");
            var timeline = Get<Timeline>("timeline");
            timeline.Playhead = a.TimeOf(step);
            Call("ShowPlayhead");
            Check(Get<Preview>("preview").Badge == (before ? "CUT" : "KEPT"), "preview badge follows the playhead");
            Call("Toggle", step);
            Check(Get<bool[]>("keep").SequenceEqual(original), "clicking again restores exactly what detection chose");

            var key = typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
            timeline.Playhead = 10;
            key.Invoke(timeline, new object[] { new KeyEventArgs(Keys.Right) });
            Check(Math.Abs(timeline.Playhead - 10.5) < 1e-6, "arrow scrubs half a second");
            key.Invoke(timeline, new object[] { new KeyEventArgs(Keys.Right | Keys.Shift) });
            Check(Math.Abs(timeline.Playhead - 15.5) < 1e-6, "shift+arrow scrubs five seconds");
            Shot(outDir, "2-edited");

            Get<Btn>("export").PerformClick();
            Check(Get<Btn>("cancelButton").Enabled, "export starts");
            for (int i = 0; i < 1500 && Get<Btn>("cancelButton").Enabled; i++) await Task.Delay(200);
            string status = Get<Label>("status").Text;
            Console.WriteLine("status: " + status);
            Check(status.StartsWith("Done"), "export from the window");
            for (int i = 0; i < 100 && (Get<Learner>("learner") == null || !Get<Learner>("learner").Trained); i++) await Task.Delay(200);
            Check(Get<Learner>("learner") != null && Get<Learner>("learner").Videos == 1, "export taught the learner");
            Shot(outDir, "3-done");
        }
    }
}
