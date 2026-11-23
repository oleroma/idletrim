using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.ML.OnnxRuntime;

namespace IdleTrim
{
    // Everything IdleTrim knows about one video, one entry per half second. Opening a video only builds the index
    // (thumbnails and motion, plus the playback proxy) with no AI; Detect adds each model's features, stored per model.
    public class Analysis
    {
        public const double Step = 0.5;

        public string Key;
        public double Duration;
        public int Count;
        public float[] Motion;      // mean absolute change between frames, 0..255
        public byte[][] Previews;   // 320x180 JPEG per step
        // Model id -> one L2-normalised embedding per step. Filled by Detect, or by older analyses for the original two models.
        public readonly Dictionary<string, float[][]> Features = new Dictionary<string, float[][]>();

        public float[][] FeaturesOf(string modelId)
        {
            float[][] f;
            return modelId != null && Features.TryGetValue(modelId, out f) ? f : null;
        }

        public double TimeOf(int step) { return Math.Min(Duration, step * Step); }
        public int StepAt(double seconds) { return Math.Max(0, Math.Min(Count - 1, (int)(seconds / Step))); }

        // Same file, same size, same timestamp: same analysis.
        public static string KeyFor(string path)
        {
            var info = new FileInfo(path);
            string id = string.Format(CultureInfo.InvariantCulture, "v1|{0}|{1}|{2}", Path.GetFullPath(path).ToLowerInvariant(), info.Length, info.LastWriteTimeUtc.Ticks);
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(id))).Replace("-", "").ToLowerInvariant();
        }

        public static string DataDir
        {
            get { return Environment.GetEnvironmentVariable("IDLETRIM_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IdleTrim"); }
        }

        static string CacheDir { get { return Path.Combine(DataDir, "cache"); } }
        static string IndexPath(string key) { return Path.Combine(CacheDir, key + ".bin"); }
        static string FeaturePath(string key, string modelId) { return Path.Combine(CacheDir, key + "." + ModelInfo.Slug(modelId) + ".feat"); }

        public void Save()
        {
            string path = IndexPath(Key);
            Directory.CreateDirectory(CacheDir);
            using (var w = new BinaryWriter(File.Create(path + ".tmp")))
            {
                w.Write("IdleTrim2");
                w.Write(Duration);
                w.Write(Count);
                for (int i = 0; i < Count; i++)
                {
                    w.Write(Motion[i]);
                    w.Write(Previews[i].Length);
                    w.Write(Previews[i]);
                }
            }
            File.Move(path + ".tmp", path, true);
            foreach (var f in Features) SaveFeatures(f.Key, f.Value);
        }

        public void SaveFeatures(string modelId, float[][] features)
        {
            Features[modelId] = features;
            string path = FeaturePath(Key, modelId);
            Directory.CreateDirectory(CacheDir);
            using (var w = new BinaryWriter(File.Create(path + ".tmp")))
            {
                w.Write("IdleTrimFeat1");
                w.Write(modelId);
                w.Write(features.Length);
                w.Write(features.Length > 0 ? features[0].Length : 0);
                foreach (var e in features) WriteFloats(w, e);
            }
            File.Move(path + ".tmp", path, true);
        }

        public static Analysis Load(string key)
        {
            string path = IndexPath(key);
            if (!File.Exists(path)) return null;
            try
            {
                Analysis a;
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    string format = r.ReadString();
                    if (format == "IdleTrim1") a = LoadOld(r, key);
                    else if (format == "IdleTrim2")
                    {
                        a = new Analysis { Key = key, Duration = r.ReadDouble(), Count = r.ReadInt32() };
                        a.Allocate();
                        for (int i = 0; i < a.Count; i++)
                        {
                            a.Motion[i] = r.ReadSingle();
                            a.Previews[i] = r.ReadBytes(r.ReadInt32());
                        }
                    }
                    else return null;
                }
                if (a.Features.Count > 0) a.Save();   // an old analysis: rewrite as index plus per-model features
                foreach (string file in Directory.GetFiles(CacheDir, key + ".*.feat"))
                {
                    try
                    {
                        using (var r = new BinaryReader(File.OpenRead(file)))
                        {
                            if (r.ReadString() != "IdleTrimFeat1") continue;
                            string id = r.ReadString();
                            int n = r.ReadInt32(), dim = r.ReadInt32();
                            if (n != a.Count) continue;
                            var f = new float[n][];
                            for (int i = 0; i < n; i++) f[i] = ReadFloats(r, dim);
                            a.Features[id] = f;
                        }
                    }
                    catch (Exception) { }   // a damaged feature file: that model detects again
                }
                return a;
            }
            catch (Exception)
            {
                return null;   // damaged cache: index again
            }
        }

        // The first format had SigLIP 2 base and DINOv2 small features inline; they become those models' feature files.
        static Analysis LoadOld(BinaryReader r, string key)
        {
            var a = new Analysis { Key = key, Duration = r.ReadDouble(), Count = r.ReadInt32() };
            a.Allocate();
            var sig = new float[a.Count][];
            var dn = new float[a.Count][];
            for (int i = 0; i < a.Count; i++)
            {
                a.Motion[i] = r.ReadSingle();
                r.ReadSingle();   // the old built-in zero-shot score, now computed from the features
                sig[i] = ReadFloats(r, 768);
                dn[i] = ReadFloats(r, 384);
                a.Previews[i] = r.ReadBytes(r.ReadInt32());
            }
            a.Features[Catalog.SiglipBase] = sig;
            a.Features[Catalog.DinoSmall] = dn;
            return a;
        }

        internal void Allocate()
        {
            Motion = new float[Count];
            Previews = new byte[Count][];
        }

        internal static void WriteFloats(BinaryWriter w, float[] v)
        {
            w.Write(MemoryMarshal.AsBytes(v.AsSpan()));
        }

        internal static float[] ReadFloats(BinaryReader r, int n)
        {
            var v = new float[n];
            r.Read(MemoryMarshal.AsBytes(v.AsSpan()));
            return v;
        }
    }

    // Shared helpers for the models: where they live, downloading, ONNX Runtime options, vector maths.
    public static class Models
    {
        public static string Dir { get { return Environment.GetEnvironmentVariable("IDLETRIM_MODELS") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models"); } }

        // files: { url, destination, name } each; files already there are skipped.
        public static void Fetch(string[][] files, Action<int, string> progress, CancelFlag cancel)
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromHours(2) })
            {
                for (int f = 0; f < files.Length; f++)
                {
                    string url = files[f][0], dest = files[f][1], name = files[f][2];
                    if (File.Exists(dest)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    string part = dest + ".part";
                    using (var response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).Result)
                    {
                        response.EnsureSuccessStatusCode();
                        long total = response.Content.Headers.ContentLength ?? 0;
                        using (var input = response.Content.ReadAsStreamAsync().Result)
                        using (var output = File.Create(part))
                        {
                            var buffer = new byte[1 << 16];
                            long done = 0;
                            int read, last = -1;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                if (cancel.Cancelled) break;
                                output.Write(buffer, 0, read);
                                done += read;
                                int pct = total > 0 ? (int)(done * 100 / total) : 0;
                                if (pct != last) { last = pct; progress(pct, name); }
                            }
                        }
                    }
                    if (cancel.Cancelled) { File.Delete(part); throw new CancelledException(); }
                    File.Move(part, dest, true);
                }
            }
        }

        internal static SessionOptions Options(bool gpu)
        {
            var o = new SessionOptions();
            if (gpu)
            {
                o.EnableMemoryPattern = false;
                o.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                o.AppendExecutionProvider_DML(0);
            }
            return o;
        }

        // Opens a model on the GPU through DirectML, or on the CPU if that fails.
        internal static InferenceSession Open(string path, out bool onGpu)
        {
            try { onGpu = true; return new InferenceSession(path, Options(true)); }
            catch (Exception)
            {
                onGpu = false;
                return new InferenceSession(path, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC });
            }
        }

        public static float Dot(float[] a, float[] b)
        {
            float s = 0;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }

        public static float[] Normalise(float[] v)
        {
            float n = (float)Math.Sqrt(Dot(v, v));
            if (n > 0) for (int i = 0; i < v.Length; i++) v[i] /= n;
            return v;
        }
    }

    // Builds a video's index with no AI: decodes at 10 fps, measures motion, keeps a thumbnail every half second.
    public static class Indexer
    {
        public const int Width = 320, Height = 180, DecodeFps = 10;
        const int PerStep = (int)(DecodeFps * Analysis.Step);

        // With makeProxy, the same decode also writes the playback proxy (see Proxy).
        public static Analysis Run(string ffmpeg, VideoInfo v, Action<double> progress, CancelFlag cancel, bool makeProxy = true)
        {
            var a = new Analysis { Key = Analysis.KeyFor(v.Path), Duration = v.Duration };
            int expected = (int)Math.Ceiling(v.Duration / Analysis.Step) + 2;
            var motion = new List<float>(expected);
            var previews = new List<byte[]>(expected);

            string proxy = Proxy.PathFor(a.Key), proxyPart = proxy + ".part";
            if (makeProxy) Directory.CreateDirectory(Path.GetDirectoryName(proxy));
            // One decode feeds both: shrunk to 640x360 (on the GPU when it can), then split into the analysis frames and the proxy.
            var dec = makeProxy ? Proxy.PickDecoder(ffmpeg, v.Path) : null;
            string args = makeProxy
                ? string.Format(CultureInfo.InvariantCulture, "-hide_banner -loglevel error -nostdin -y {5} -i \"{0}\" -filter_complex \"[0:v:0]{6},split=2[an][pv];[an]fps={1},scale={2}:{3},format=rgb24[a];[pv]fps={4:0.###},format=yuv420p[px]\" -map \"[a]\" -f rawvideo pipe:1",
                                v.Path, DecodeFps, Width, Height, Proxy.Fps(v), dec[0], dec[1]) + Proxy.OutputArgs(v, "[px]", proxyPart)
                : string.Format("-hide_banner -loglevel error -nostdin -hwaccel d3d11va -i \"{0}\" -an -sn -dn -vf fps={1},scale={2}:{3} -pix_fmt rgb24 -f rawvideo pipe:1",
                                v.Path, DecodeFps, Width, Height);
            var psi = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            int frameBytes = Width * Height * 3;
            using (var proc = new Process { StartInfo = psi })
            {
                var errors = new StringBuilder();
                proc.ErrorDataReceived += (o, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                proc.Start();
                cancel.Current = proc;
                proc.BeginErrorReadLine();
                var stdout = proc.StandardOutput.BaseStream;
                byte[] frame = new byte[frameBytes], prev = null;
                float motionSum = 0;
                int index = 0;
                try
                {
                    while (ReadFull(stdout, frame))
                    {
                        if (cancel.Cancelled) throw new CancelledException();
                        if (prev != null) motionSum += Diff(prev, frame);
                        if (index % PerStep == 0)
                        {
                            if (index > 0) motion.Add(motionSum / PerStep);
                            motionSum = 0;
                            previews.Add(Jpeg(frame));
                            progress(Math.Min(0.99, index / (v.Duration * DecodeFps)));
                        }
                        prev = prev ?? new byte[frameBytes];
                        Buffer.BlockCopy(frame, 0, prev, 0, frameBytes);
                        index++;
                    }
                    proc.WaitForExit();
                }
                finally
                {
                    cancel.Current = null;
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                    if (makeProxy)
                    {
                        if (!cancel.Cancelled && proc.HasExited && proc.ExitCode == 0) try { Proxy.Finish(proxyPart, proxy); } catch { }
                        try { File.Delete(proxyPart); } catch { }
                    }
                }
                if (cancel.Cancelled) throw new CancelledException();
                if (previews.Count == 0)
                    throw new Exception("Couldn't read any frames:\n" + errors.ToString().Trim());
                motion.Add(motionSum / Math.Max(1, index % PerStep == 0 ? PerStep : index % PerStep));
            }

            a.Count = previews.Count;
            a.Allocate();
            for (int i = 0; i < a.Count; i++)
            {
                a.Motion[i] = i < motion.Count ? motion[i] : 0;
                a.Previews[i] = previews[i];
            }
            progress(1);
            return a;
        }

        internal static bool ReadFull(Stream s, byte[] buffer)
        {
            int got = 0;
            while (got < buffer.Length)
            {
                int n = s.Read(buffer, got, buffer.Length - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        static float Diff(byte[] a, byte[] b)
        {
            long sum = 0;
            for (int i = 0; i < a.Length; i += 3)
            {
                int ga = a[i] + a[i + 1] * 2 + a[i + 2], gb = b[i] + b[i + 1] * 2 + b[i + 2];
                sum += Math.Abs(ga - gb);
            }
            return sum / 4f / (a.Length / 3);
        }

        public static byte[] Jpeg(byte[] rgb)
        {
            using (var bmp = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
            {
                var data = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                var row = new byte[Width * 3];
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int i = (y * Width + x) * 3;
                        row[x * 3] = rgb[i + 2];       // GDI+ stores BGR
                        row[x * 3 + 1] = rgb[i + 1];
                        row[x * 3 + 2] = rgb[i];
                    }
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
                }
                bmp.UnlockBits(data);
                using (var ms = new MemoryStream())
                {
                    var codec = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid);
                    var p = new EncoderParameters(1);
                    p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
                    bmp.Save(ms, codec, p);
                    return ms.ToArray();
                }
            }
        }
    }
}
