using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IdleTrim
{
    public enum Source { Siglip, Dino, Both }

    // A small logistic regression per model, trained on the keep/cut choices of every exported video.
    public class Learner
    {
        public const int MinPerClass = 40;   // half-second steps of each kind before a model counts as trained

        public float[] SigW, DinoW;
        public float SigB, DinoB;
        public int Videos, Kept, Cut;

        public bool Trained { get { return SigW != null && Kept >= MinPerClass && Cut >= MinPerClass; } }

        static string Dir { get { return Path.Combine(Analysis.DataDir, "learn"); } }

        // Saves one video's final keep/cut choice with its embeddings. A later export of the same video replaces it.
        public static void AddExample(Analysis a, bool[] keep)
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, a.Key + ".bin");
            using (var w = new BinaryWriter(File.Create(path + ".tmp")))
            {
                w.Write("IdleTrimLearn1");
                w.Write(a.Count);
                for (int i = 0; i < a.Count; i++)
                {
                    w.Write(keep[i]);
                    Analysis.WriteFloats(w, a.Siglip[i]);
                    Analysis.WriteFloats(w, a.Dino[i]);
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
            var labels = new List<bool>();
            var sig = new List<float[]>();
            var dn = new List<float[]>();
            var l = new Learner();
            if (Directory.Exists(Dir))
            {
                foreach (string file in Directory.GetFiles(Dir, "*.bin"))
                {
                    try
                    {
                        using (var r = new BinaryReader(File.OpenRead(file)))
                        {
                            if (r.ReadString() != "IdleTrimLearn1") continue;
                            int n = r.ReadInt32();
                            for (int i = 0; i < n; i++)
                            {
                                labels.Add(r.ReadBoolean());
                                sig.Add(Analysis.ReadFloats(r, Analysis.SigDim));
                                dn.Add(Analysis.ReadFloats(r, Analysis.DinoDim));
                            }
                            l.Videos++;
                        }
                    }
                    catch (Exception) { }   // skip a damaged file
                }
            }
            l.Kept = labels.Count(x => x);
            l.Cut = labels.Count - l.Kept;
            if (l.Kept == 0 || l.Cut == 0) return l;
            l.SigW = Fit(sig, labels, out l.SigB);
            l.DinoW = Fit(dn, labels, out l.DinoB);
            return l;
        }

        // Class-balanced logistic regression, full-batch gradient descent with Adam and a little L2.
        static float[] Fit(List<float[]> x, List<bool> y, out float bias)
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

        public float Prob(float[] weights, float bias, float[] x)
        {
            double z = bias + Models.Dot(weights, x);
            return (float)(1 / (1 + Math.Exp(-z)));
        }
    }

    public static class Scoring
    {
        // 0..1 per step, higher is more action. Untrained: SigLIP prompts / motion, scaled to the video's own range.
        public static float[] Scores(Analysis a, Source source, Learner learner)
        {
            bool trained = learner != null && learner.Trained;
            float[] sig = new float[a.Count], dn = new float[a.Count];
            if (trained)
            {
                for (int i = 0; i < a.Count; i++)
                {
                    sig[i] = learner.Prob(learner.SigW, learner.SigB, a.Siglip[i]);
                    dn[i] = learner.Prob(learner.DinoW, learner.DinoB, a.Dino[i]);
                }
            }
            else
            {
                sig = Spread(a.ZeroShot);
                var motion = Spread(a.Motion);
                // DINOv2 has no prompts: until it has learned, it stands in with motion.
                for (int i = 0; i < a.Count; i++) dn[i] = motion[i];
                for (int i = 0; i < a.Count; i++) sig[i] = 0.8f * sig[i] + 0.2f * motion[i];
            }
            float[] raw = new float[a.Count];
            for (int i = 0; i < a.Count; i++)
                raw[i] = source == Source.Siglip ? sig[i] : source == Source.Dino ? dn[i] : (sig[i] + dn[i]) / 2;
            return Smooth(raw, 2);
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
