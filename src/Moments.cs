using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace IdleTrim
{
    // "Find moments like these": you draw a screen region (say, where THE FINALS shows "Eliminated") and mark a few steps
    // where the thing you want is on screen. Every step's crop of that region, taken from the full-size original, is embedded
    // with a model, and a small head learns your examples against the rest of the video. Kept per video in the data folder.
    public class Moments
    {
        // The region as fractions of the frame (0..1). Width 0: none drawn yet.
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public List<int> Examples { get; set; } = new List<int>();   // steps you marked

        public bool HasRegion { get { return W > 0 && H > 0; } }

        static string PathOf(string key) { return Path.Combine(Analysis.DataDir, "moments", key + ".json"); }

        public static Moments Load(string key)
        {
            try { if (File.Exists(PathOf(key))) return JsonSerializer.Deserialize<Moments>(File.ReadAllText(PathOf(key))) ?? new Moments(); }
            catch (Exception) { }
            return new Moments();
        }

        public void Save(string key)
        {
            string path = PathOf(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this));
            File.Move(path + ".tmp", path, true);
        }

        // Fractions as text, so a cache knows which region it was made for.
        public string RegionId { get { return string.Format(CultureInfo.InvariantCulture, "{0:0.0000},{1:0.0000},{2:0.0000},{3:0.0000}", X, Y, W, H); } }
    }

    // One model's embedding of the region's crop at every step, from the original at full resolution.
    public static class Crops
    {
        static string PathOf(string key, string modelId) { return Path.Combine(Analysis.DataDir, "cache", key + ".crop." + ModelInfo.Slug(modelId) + ".feat"); }

        // Cached crops for this region and model, or null.
        public static float[][] Load(Analysis a, Moments m, string modelId)
        {
            if (modelId == null || !m.HasRegion) return null;
            try
            {
                string path = PathOf(a.Key, modelId);
                if (!File.Exists(path)) return null;
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (r.ReadString() != "IdleTrimCrop1" || r.ReadString() != modelId || r.ReadString() != m.RegionId) return null;
                    int n = r.ReadInt32(), dim = r.ReadInt32();
                    if (n != a.Count) return null;
                    var f = new float[n][];
                    for (int i = 0; i < n; i++) f[i] = Analysis.ReadFloats(r, dim);
                    return f;
                }
            }
            catch (Exception) { return null; }
        }

        static void Save(Analysis a, Moments m, string modelId, float[][] f)
        {
            string path = PathOf(a.Key, modelId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var w = new BinaryWriter(File.Create(path + ".tmp")))
            {
                w.Write("IdleTrimCrop1");
                w.Write(modelId);
                w.Write(m.RegionId);
                w.Write(f.Length);
                w.Write(f[0].Length);
                foreach (var e in f) Analysis.WriteFloats(w, e);
            }
            File.Move(path + ".tmp", path, true);
        }

        // Decoders tried in order: CUDA, copying out only the two frames a second that are used; D3D11; the CPU.
        static readonly string[][] Decoders =
        {
            new[] { "-hwaccel cuda -hwaccel_output_format cuda", "fps={0},hwdownload,format=nv12," },
            new[] { "-hwaccel d3d11va", "fps={0}," },
            new[] { "", "fps={0}," },
        };

        // The filter after decoding: two frames a second, the region cut out of the full frame, squeezed to the model's input.
        static string Filter(string[] decoder, Moments m, int size)
        {
            return string.Format(CultureInfo.InvariantCulture,
                decoder[1] + "crop=w=max(16\\,trunc(iw*{1:0.0000}/2)*2):h=max(16\\,trunc(ih*{2:0.0000}/2)*2):x=trunc(iw*{3:0.0000}/2)*2:y=trunc(ih*{4:0.0000}/2)*2," +
                "scale={5}:{5}:flags=area,format=rgb24", 1 / Analysis.Step, m.W, m.H, m.X, m.Y, size);
        }

        static string Args(string[] decoder, string source, Moments m, int size, string limit)
        {
            return "-hide_banner -loglevel error -nostdin " + decoder[0] + " -i \"" + source + "\"" + limit + " -an -sn -dn -vf \"" + Filter(decoder, m, size) + "\" -f rawvideo pipe:1";
        }

        static string[] PickDecoder(string ffmpeg, string source, Moments m, int size)
        {
            foreach (var d in Decoders)
            {
                var psi = new ProcessStartInfo(ffmpeg, Args(d, source, m, size, " -frames:v 1").Replace("-f rawvideo pipe:1", "-f null -"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                try
                {
                    using (var p = Process.Start(psi))
                    {
                        p.ErrorDataReceived += (o, e) => { };
                        p.BeginErrorReadLine();
                        if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } continue; }
                        if (p.ExitCode == 0) return d;
                    }
                }
                catch (Exception) { }
            }
            return Decoders[Decoders.Length - 1];
        }

        // Embeds the region at every step with the model and caches it. source: the original video.
        public static float[][] Run(string ffmpeg, Analysis a, string source, Moments m, ModelInfo model, Action<double, string> progress, CancelFlag cancel)
        {
            progress(0, "Loading " + model.Name);
            using (var enc = new ImageEncoder(model))
            {
                int size = model.InputSize, frameBytes = size * size * 3;
                var result = new List<float[]>(a.Count);
                var batch = new List<byte[]>();
                var decoder = PickDecoder(ffmpeg, source, m, size);
                var psi = new ProcessStartInfo(ffmpeg, Args(decoder, source, m, size, ""))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                var errors = new StringBuilder();
                using (var p = new Process { StartInfo = psi })
                {
                    p.ErrorDataReceived += (o, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                    p.Start();
                    cancel.Current = p;
                    p.BeginErrorReadLine();
                    var stdout = p.StandardOutput.BaseStream;
                    try
                    {
                        while (result.Count + batch.Count < a.Count)
                        {
                            var frame = new byte[frameBytes];
                            if (!Indexer.ReadFull(stdout, frame)) break;
                            if (cancel.Cancelled) throw new CancelledException();
                            batch.Add(frame);
                            if (batch.Count == enc.Batch)
                            {
                                result.AddRange(enc.Embed(batch, size, size));
                                batch.Clear();
                                progress((double)result.Count / a.Count, "Looking for moments");
                            }
                        }
                        if (batch.Count > 0) result.AddRange(enc.Embed(batch, size, size));
                    }
                    finally
                    {
                        cancel.Current = null;
                        try { if (!p.HasExited) p.Kill(); } catch { }
                    }
                    if (cancel.Cancelled) throw new CancelledException();
                }
                if (result.Count == 0) throw new Exception("Couldn't read the region from the video. " + errors.ToString().Trim());
                while (result.Count < a.Count) result.Add(result[result.Count - 1]);   // the last half second may be short a frame
                var f = result.ToArray();
                Save(a, m, model.Id, f);
                progress(1, "Looking for moments");
                return f;
            }
        }
    }

    public static class MomentFinder
    {
        public const double Threshold = 0.5;
        const int Near = 4;   // steps (2 s) around an example that are never used as counter-examples

        // 0..1 per step: how much the region looks like it does in your examples. A head learns the examples against
        // counter-examples from the rest of the video. Unmarked moments like the examples must not be taught as "not this",
        // so steps closer to the examples than halfway between a typical step and the examples themselves are left out.
        public static float[] Score(float[][] f, IList<int> examples)
        {
            if (f == null || examples == null) return null;
            var pos = examples.Where(e => e >= 0 && e < f.Length).Distinct().ToList();
            if (pos.Count == 0) return null;
            int n = f.Length;
            var sim = new float[n];
            for (int i = 0; i < n; i++) sim[i] = pos.Max(e => Models.Dot(f[i], f[e]));
            var far = Enumerable.Range(0, n).Where(i => pos.All(e => Math.Abs(i - e) > Near)).ToList();
            if (far.Count == 0) { var all = new float[n]; for (int i = 0; i < n; i++) all[i] = 1; return all; }
            float typical = far.Select(i => sim[i]).OrderBy(v => v).ElementAt(far.Count / 2);
            // How alike the examples are to each other (1 for a single one): what "like these" looks like.
            float alike = 1;
            if (pos.Count > 1)
            {
                var pairs = new List<float>();
                for (int i = 0; i < pos.Count; i++) for (int j = i + 1; j < pos.Count; j++) pairs.Add(Models.Dot(f[pos[i]], f[pos[j]]));
                alike = pairs.Average();
            }
            float cut = (typical + Math.Max(typical, alike)) / 2;
            var neg = far.Where(i => sim[i] < cut).ToList();
            if (neg.Count == 0) neg = far.OrderBy(i => sim[i]).Take(Math.Max(1, far.Count / 2)).ToList();

            var x = new List<float[]>();
            var y = new List<bool>();
            foreach (int e in pos) { x.Add(f[e]); y.Add(true); }
            foreach (int i in neg) { x.Add(f[i]); y.Add(false); }
            float b;
            var w = Learner.Fit(x, y, out b);
            var p = new float[n];
            for (int i = 0; i < n; i++) p[i] = (float)(1 / (1 + Math.Exp(-(b + Models.Dot(w, f[i])))));
            return p;
        }

        // The found moments: each run of steps over the threshold (runs under a second apart count as one) gives the
        // step where it scores highest.
        public static List<int> Events(float[] p)
        {
            var events = new List<int>();
            if (p == null) return events;
            int i = 0;
            while (i < p.Length)
            {
                if (p[i] < Threshold) { i++; continue; }
                int best = i, gap = 0;
                for (; i < p.Length && gap <= 2; i++)
                {
                    if (p[i] >= Threshold) { gap = 0; if (p[i] > p[best]) best = i; }
                    else gap++;
                }
                events.Add(best);
            }
            return events;
        }
    }
}
