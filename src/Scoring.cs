using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IdleTrim
{
    // A small logistic regression per model, trained on the keep/cut choices of every exported video.
    public class Learner
    {
        public const int MinPerClass = 40;   // half-second steps of each kind before a model counts as trained

        public class Head
        {
            public float[] W;
            public float B;
            public int Kept, Cut;
        }

        public readonly Dictionary<string, Head> Heads = new Dictionary<string, Head>();
        public int Videos;

        public bool TrainedFor(string modelId)
        {
            Head h;
            return modelId != null && Heads.TryGetValue(modelId, out h) && h.W != null && h.Kept >= MinPerClass && h.Cut >= MinPerClass;
        }

        public bool Trained { get { return Heads.Keys.Any(TrainedFor); } }
        public int Kept { get { return Heads.Count == 0 ? 0 : Heads.Values.Max(h => h.Kept); } }
        public int Cut { get { return Heads.Count == 0 ? 0 : Heads.Values.Max(h => h.Cut); } }

        static string Dir { get { return Path.Combine(Analysis.DataDir, "learn"); } }

        // Saves one video's final keep/cut choice with every model's features it has. A later export of the same video replaces it.
        public static void AddExample(Analysis a, bool[] keep)
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, a.Key + ".bin");
            using (var w = new BinaryWriter(File.Create(path + ".tmp")))
            {
                w.Write("IdleTrimLearn2");
                w.Write(a.Count);
                for (int i = 0; i < a.Count; i++) w.Write(keep[i]);
                w.Write(a.Features.Count);
                foreach (var f in a.Features)
                {
                    w.Write(f.Key);
                    w.Write(f.Value[0].Length);
                    for (int i = 0; i < a.Count; i++) Analysis.WriteFloats(w, f.Value[i]);
                }
            }
            File.Move(path + ".tmp", path, true);
        }

        public static void Forget()
        {
            if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
        }

        public static Learner Train()
        {
            var labels = new Dictionary<string, List<bool>>();
            var feats = new Dictionary<string, List<float[]>>();
            var l = new Learner();
            Action<string, bool[], float[][]> add = (id, keep, f) =>
            {
                if (!labels.ContainsKey(id)) { labels[id] = new List<bool>(); feats[id] = new List<float[]>(); }
                labels[id].AddRange(keep);
                feats[id].AddRange(f);
            };
            if (Directory.Exists(Dir))
            {
                foreach (string file in Directory.GetFiles(Dir, "*.bin"))
                {
                    try
                    {
                        using (var r = new BinaryReader(File.OpenRead(file)))
                        {
                            string format = r.ReadString();
                            int n = r.ReadInt32();
                            var keep = new bool[n];
                            if (format == "IdleTrimLearn1")
                            {
                                // The first version: SigLIP 2 base and DINOv2 small features with each step.
                                var sig = new float[n][];
                                var dn = new float[n][];
                                for (int i = 0; i < n; i++) { keep[i] = r.ReadBoolean(); sig[i] = Analysis.ReadFloats(r, 768); dn[i] = Analysis.ReadFloats(r, 384); }
                                add(Catalog.SiglipBase, keep, sig);
                                add(Catalog.DinoSmall, keep, dn);
                            }
                            else if (format == "IdleTrimLearn2")
                            {
                                for (int i = 0; i < n; i++) keep[i] = r.ReadBoolean();
                                int models = r.ReadInt32();
                                for (int m = 0; m < models; m++)
                                {
                                    string id = r.ReadString();
                                    int dim = r.ReadInt32();
                                    var f = new float[n][];
                                    for (int i = 0; i < n; i++) f[i] = Analysis.ReadFloats(r, dim);
                                    add(id, keep, f);
                                }
                            }
                            else continue;
                            l.Videos++;
                        }
                    }
                    catch (Exception) { }   // skip a damaged file
                }
            }
            foreach (var id in labels.Keys)
            {
                var y = labels[id];
                var h = new Head { Kept = y.Count(x => x) };
                h.Cut = y.Count - h.Kept;
                if (h.Kept > 0 && h.Cut > 0) h.W = Fit(feats[id], y, out h.B);
                l.Heads[id] = h;
            }
            return l;
        }

        // Class-balanced logistic regression, full-batch gradient descent with Adam and a little L2.
        internal static float[] Fit(List<float[]> x, List<bool> y, out float bias)
        {
            int n = x.Count, d = x[0].Length;
            int pos = y.Count(v => v);
            double wPos = n / (2.0 * pos), wNeg = n / (2.0 * (n - pos));
            var w = new double[d];
            var m = new double[d + 1];
            var v2 = new double[d + 1];
            double b = 0, lr = 0.05, l2 = 1e-3, scale = 8;   // inputs are unit vectors; scale lets the weights stay small
            var grad = new double[d];
            for (int it = 1; it <= 300; it++)
            {
                Array.Clear(grad, 0, d);
                double gb = 0;
                for (int i = 0; i < n; i++)
                {
                    var xi = x[i];
                    double z = b;
                    for (int j = 0; j < d; j++) z += w[j] * xi[j] * scale;
                    double p = 1 / (1 + Math.Exp(-z));
                    double err = (p - (y[i] ? 1 : 0)) * (y[i] ? wPos : wNeg) / n;
                    for (int j = 0; j < d; j++) grad[j] += err * xi[j] * scale;
                    gb += err;
                }
                for (int j = 0; j <= d; j++)
                {
                    double g = j < d ? grad[j] + l2 * w[j] : gb;
                    m[j] = 0.9 * m[j] + 0.1 * g;
                    v2[j] = 0.999 * v2[j] + 0.001 * g * g;
                    double step = lr * (m[j] / (1 - Math.Pow(0.9, it))) / (Math.Sqrt(v2[j] / (1 - Math.Pow(0.999, it))) + 1e-8);
                    if (j < d) w[j] -= step; else b -= step;
                }
            }
            bias = (float)b;
            var result = new float[d];
            for (int j = 0; j < d; j++) result[j] = (float)(w[j] * scale);
            return result;
        }

        public float Prob(string modelId, float[] x)
        {
            var h = Heads[modelId];
            double z = h.B + Models.Dot(h.W, x);
            return (float)(1 / (1 + Math.Exp(-z)));
        }
    }

    public static class Scoring
    {
        // 0..1 per step, higher is more action, from the chosen prompt and visual models (either may be null). Each model
        // that has learned from your edits uses that. Otherwise the prompt model compares frames with the prompts, and the
        // visual model learns from the prompt model's surest frames in this video. Null when there's nothing to go on yet:
        // the video hasn't been through Detect with these models, or the prompts haven't been read by the prompt model.
        public static float[] Scores(Analysis a, string promptModel, string visualModel, Learner learner)
        {
            var parts = new List<float[]>();
            float[][] pf = a.FeaturesOf(promptModel), vf = a.FeaturesOf(visualModel);
            float[] zero = pf != null ? ZeroShot(a, promptModel) : null;
            if (pf != null)
            {
                if (learner != null && learner.TrainedFor(promptModel)) parts.Add(pf.Select(e => learner.Prob(promptModel, e)).ToArray());
                else if (zero != null) parts.Add(Spread(zero));
            }
            if (vf != null)
            {
                if (learner != null && learner.TrainedFor(visualModel)) parts.Add(vf.Select(e => learner.Prob(visualModel, e)).ToArray());
                else if (zero != null) parts.Add(Spread(VisualFromPrompts(a, visualModel, zero)));
            }
            if (parts.Count == 0) return null;
            var raw = new float[a.Count];
            for (int i = 0; i < a.Count; i++) raw[i] = parts.Sum(p => p[i]) / parts.Count;
            return Smooth(raw, 2);
        }

        // The prompts' score for each step, from the stored image features, so editing prompts needs no re-detecting.
        // Null until the prompt model has read the current prompts.
        static readonly Dictionary<string, Tuple<Analysis, int, float[]>> zeroCache = new Dictionary<string, Tuple<Analysis, int, float[]>>();

        public static float[] ZeroShot(Analysis a, string promptModel)
        {
            var f = a.FeaturesOf(promptModel);
            var e = f == null ? null : PromptSet.For(promptModel);
            if (e == null) return null;
            lock (zeroCache)
            {
                Tuple<Analysis, int, float[]> c;
                if (zeroCache.TryGetValue(promptModel, out c) && c.Item1 == a && c.Item2 == PromptSet.Version) return c.Item3;
                var r = e.Scores(f);
                zeroCache[promptModel] = Tuple.Create(a, PromptSet.Version, r);
                return r;
            }
        }

        // A visual model has no text side, so until it has learned from you it learns from the prompt model: the quarter of
        // this video's steps the prompts rate highest are action and the lowest quarter idle, which train a classifier on the
        // visual model's features; it then judges every step by what is on screen. Camera movement alone doesn't count.
        static readonly Dictionary<string, Tuple<Analysis, float[], float[]>> visualCache = new Dictionary<string, Tuple<Analysis, float[], float[]>>();

        public static float[] VisualFromPrompts(Analysis a, string visualModel, float[] zero)
        {
            lock (visualCache)
            {
                Tuple<Analysis, float[], float[]> c;
                if (visualCache.TryGetValue(visualModel, out c) && c.Item1 == a && c.Item2 == zero) return c.Item3;
            }
            var feats = a.FeaturesOf(visualModel);
            var result = new float[a.Count];
            if (a.Count >= 8)
            {
                var order = Enumerable.Range(0, a.Count).OrderBy(i => zero[i]).ToArray();
                int q = Math.Max(2, a.Count / 4), stride = Math.Max(1, q / 1000);   // at most about 2000 examples
                var x = new List<float[]>();
                var y = new List<bool>();
                for (int k = 0; k < q; k += stride)
                {
                    x.Add(feats[order[a.Count - 1 - k]]); y.Add(true);
                    x.Add(feats[order[k]]); y.Add(false);
                }
                float b;
                var w = Learner.Fit(x, y, out b);
                for (int i = 0; i < a.Count; i++) result[i] = b + Models.Dot(w, feats[i]);
            }
            lock (visualCache) visualCache[visualModel] = Tuple.Create(a, zero, result);
            return result;
        }

        // Maps the 5th..95th percentile of a series onto 0..1.
        public static float[] Spread(float[] v)
        {
            if (v.Length == 0) return new float[0];
            var sorted = (float[])v.Clone();
            Array.Sort(sorted);
            float lo = sorted[(int)(0.05 * (sorted.Length - 1))], hi = sorted[(int)(0.95 * (sorted.Length - 1))];
            var r = new float[v.Length];
            for (int i = 0; i < v.Length; i++) r[i] = hi > lo ? Math.Max(0, Math.Min(1, (v[i] - lo) / (hi - lo))) : 0.5f;
            return r;
        }

        // Centred moving average over 2*radius+1 steps.
        public static float[] Smooth(float[] v, int radius)
        {
            var r = new float[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                float sum = 0;
                int n = 0;
                for (int j = Math.Max(0, i - radius); j <= Math.Min(v.Length - 1, i + radius); j++) { sum += v[j]; n++; }
                r[i] = sum / n;
            }
            return r;
        }

        // Threshold, then grow each action by the padding, fill short gaps and drop short leftovers.
        // overrides: +1 always keep, -1 always cut, 0 automatic.
        public static bool[] Mask(float[] scores, double threshold, double padSeconds, sbyte[] overrides)
        {
            int n = scores.Length;
            var hit = new bool[n];
            for (int i = 0; i < n; i++) hit[i] = scores[i] >= threshold;

            int before = (int)Math.Round(padSeconds / Analysis.Step), after = (int)Math.Round(padSeconds * 1.5 / Analysis.Step);
            var keep = new bool[n];
            for (int i = 0; i < n; i++)
                if (hit[i])
                    for (int j = Math.Max(0, i - before); j <= Math.Min(n - 1, i + after); j++) keep[j] = true;

            FillRuns(keep, false, (int)(2 / Analysis.Step));   // gaps under 2 s become kept
            FillRuns(keep, true, (int)(1.5 / Analysis.Step));  // kept bits under 1.5 s are cut

            if (overrides != null)
                for (int i = 0; i < n; i++)
                    if (overrides[i] != 0) keep[i] = overrides[i] > 0;
            return keep;
        }

        // Flips runs of `value` shorter than minLength that sit between two runs of the other value.
        static void FillRuns(bool[] mask, bool value, int minLength)
        {
            int i = 0;
            while (i < mask.Length)
            {
                if (mask[i] != value) { i++; continue; }
                int start = i;
                while (i < mask.Length && mask[i] == value) i++;
                bool inside = start > 0 && i < mask.Length;
                if (inside && i - start < minLength)
                    for (int j = start; j < i; j++) mask[j] = !value;
            }
        }

        public static List<Segment> Segments(bool[] keep, double duration)
        {
            var list = new List<Segment>();
            int i = 0;
            while (i < keep.Length)
            {
                if (!keep[i]) { i++; continue; }
                int start = i;
                while (i < keep.Length && keep[i]) i++;
                list.Add(new Segment(start * Analysis.Step, Math.Min(duration, i * Analysis.Step)));
            }
            list.RemoveAll(s => s.Length < 0.2);
            return list;
        }

        // Same, but a split (the step a new part starts at) also ends a run.
        public static void RunAt(bool[] keep, int i, HashSet<int> splits, out int start, out int end)
        {
            start = i;
            end = i;
            while (start > 0 && keep[start - 1] == keep[i] && !splits.Contains(start)) start--;
            while (end < keep.Length - 1 && keep[end + 1] == keep[i] && !splits.Contains(end + 1)) end++;
        }

        // The run of equal keep/cut steps that contains step i.
        public static void RunAt(bool[] keep, int i, out int start, out int end)
        {
            start = i;
            end = i;
            while (start > 0 && keep[start - 1] == keep[i]) start--;
            while (end < keep.Length - 1 && keep[end + 1] == keep[i]) end++;
        }
    }
}
