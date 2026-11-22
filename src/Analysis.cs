using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IdleTrim
{
    // Everything IdleTrim knows about one video, one entry per half second.
    public class Analysis
    {
        public const double Step = 0.5;
        public const int SigDim = 768, DinoDim = 384;

        public string Key;
        public double Duration;
        public int Count;
        public float[] Motion;      // mean absolute change between frames, 0..255
        public float[] ZeroShot;    // best action prompt minus best idle prompt (SigLIP cosine)
        public float[][] Siglip;    // SigLIP 2 image embeddings, L2-normalised
        public float[][] Dino;      // DINOv2 CLS embeddings, L2-normalised
        public byte[][] Previews;   // 320x180 JPEG per step

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

        static string CachePath(string key) { return Path.Combine(DataDir, "cache", key + ".bin"); }

        public void Save()
        {
            string path = CachePath(Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var w = new BinaryWriter(File.Create(path + ".tmp")))
            {
                w.Write("IdleTrim1");
                w.Write(Duration);
                w.Write(Count);
                for (int i = 0; i < Count; i++)
                {
                    w.Write(Motion[i]);
                    w.Write(ZeroShot[i]);
                    WriteFloats(w, Siglip[i]);
                    WriteFloats(w, Dino[i]);
                    w.Write(Previews[i].Length);
                    w.Write(Previews[i]);
                }
            }
            File.Move(path + ".tmp", path, true);
        }

        public static Analysis Load(string key)
        {
            string path = CachePath(key);
            if (!File.Exists(path)) return null;
            try
            {
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (r.ReadString() != "IdleTrim1") return null;
                    var a = new Analysis { Key = key, Duration = r.ReadDouble(), Count = r.ReadInt32() };
                    a.Allocate();
                    for (int i = 0; i < a.Count; i++)
                    {
                        a.Motion[i] = r.ReadSingle();
                        a.ZeroShot[i] = r.ReadSingle();
                        a.Siglip[i] = ReadFloats(r, SigDim);
                        a.Dino[i] = ReadFloats(r, DinoDim);
                        a.Previews[i] = r.ReadBytes(r.ReadInt32());
                    }
                    return a;
                }
            }
            catch (Exception)
            {
                return null;   // damaged cache: analyse again
            }
        }

        internal void Allocate()
        {
            Motion = new float[Count];
            ZeroShot = new float[Count];
            Siglip = new float[Count][];
            Dino = new float[Count][];
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

    // SigLIP 2 (base, patch16, 224) and DINOv2 (small), both Apache 2.0, run with ONNX Runtime on the GPU when it can.
    public class Models : IDisposable
    {
        public const string SiglipUrl = "https://huggingface.co/onnx-community/siglip2-base-patch16-224-ONNX/resolve/main/onnx/vision_model_fp16.onnx";
        public const string DinoUrl = "https://huggingface.co/onnx-community/dinov2-small-ONNX/resolve/main/onnx/model.onnx";
        public const int Size = 224;

        readonly InferenceSession siglip, dino;
        public readonly bool OnGpu;

        public static string Dir { get { return Environment.GetEnvironmentVariable("IDLETRIM_MODELS") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models"); } }
        static string SiglipPath { get { return Path.Combine(Dir, "siglip2", "vision_model_fp16.onnx"); } }
        static string DinoPath { get { return Path.Combine(Dir, "dinov2-small", "model.onnx"); } }

        public static bool Present() { return File.Exists(SiglipPath) && File.Exists(DinoPath); }

        public static void Download(Action<int, string> progress, CancelFlag cancel)
        {
            Fetch(new[] { new[] { SiglipUrl, SiglipPath, "SigLIP 2" }, new[] { DinoUrl, DinoPath, "DINOv2" } }, progress, cancel);
        }

        // files: { url, destination, name } each; files already there are skipped.
        public static void Fetch(string[][] files, Action<int, string> progress, CancelFlag cancel)
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
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

        public Models()
        {
            OnGpu = true;
            try
            {
                siglip = new InferenceSession(SiglipPath, Options(true));
                dino = new InferenceSession(DinoPath, Options(true));
            }
            catch (Exception)
            {
                OnGpu = false;
                if (siglip != null) siglip.Dispose();
                siglip = new InferenceSession(SiglipPath, Options(false));
                dino = new InferenceSession(DinoPath, Options(false));
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

        // frames: 320x180 RGB24 buffers. Fills the embeddings and the zero-shot score for each.
        public void Embed(List<byte[]> frames, int width, int height, float[][] sig, float[][] dn, float[] zero)
        {
            int n = frames.Count;
            var sigIn = new DenseTensor<float>(new[] { n, 3, Size, Size });
            var dinoIn = new DenseTensor<float>(new[] { n, 3, Size, Size });
            var resized = new float[Size * Size * 3];
            float[] dMean = { 0.485f, 0.456f, 0.406f }, dStd = { 0.229f, 0.224f, 0.225f };
            for (int b = 0; b < n; b++)
            {
                Resize(frames[b], width, height, resized);
                for (int c = 0; c < 3; c++)
                    for (int y = 0; y < Size; y++)
                        for (int x = 0; x < Size; x++)
                        {
                            float v = resized[(y * Size + x) * 3 + c];
                            sigIn[b, c, y, x] = (v - 0.5f) / 0.5f;
                            dinoIn[b, c, y, x] = (v - dMean[c]) / dStd[c];
                        }
            }

            using (var outs = siglip.Run(new[] { NamedOnnxValue.CreateFromTensor(First(siglip), sigIn) }))
            {
                Tensor<float> pooled = null;
                foreach (var o in outs) if (o.Name == "pooler_output") pooled = o.AsTensor<float>();
                for (int b = 0; b < n; b++)
                {
                    var e = new float[Analysis.SigDim];
                    for (int i = 0; i < e.Length; i++) e[i] = pooled[b, i];
                    sig[b] = Normalise(e);
                    zero[b] = PromptSet.Current.Score(sig[b]);
                }
            }

            using (var outs = dino.Run(new[] { NamedOnnxValue.CreateFromTensor(First(dino), dinoIn) }))
            {
                Tensor<float> hidden = null;
                foreach (var o in outs) { hidden = o.AsTensor<float>(); break; }
                for (int b = 0; b < n; b++)
                {
                    var e = new float[Analysis.DinoDim];
                    for (int i = 0; i < e.Length; i++) e[i] = hidden[b, 0, i];   // CLS token
                    dn[b] = Normalise(e);
                }
            }
        }

        static string First(InferenceSession s)
        {
            foreach (var k in s.InputMetadata.Keys) return k;
            return "pixel_values";
        }

        // Bilinear resize of an RGB24 frame to Size x Size, values 0..1, interleaved.
        static void Resize(byte[] src, int w, int h, float[] dst)
        {
            float sx = (float)w / Size, sy = (float)h / Size;
            for (int y = 0; y < Size; y++)
            {
                float fy = Math.Max(0, (y + 0.5f) * sy - 0.5f);
                int y0 = Math.Min(h - 1, (int)fy), y1 = Math.Min(h - 1, y0 + 1);
                float ty = fy - y0;
                for (int x = 0; x < Size; x++)
                {
                    float fx = Math.Max(0, (x + 0.5f) * sx - 0.5f);
                    int x0 = Math.Min(w - 1, (int)fx), x1 = Math.Min(w - 1, x0 + 1);
                    float tx = fx - x0;
                    for (int c = 0; c < 3; c++)
                    {
                        float a = src[(y0 * w + x0) * 3 + c], b = src[(y0 * w + x1) * 3 + c];
                        float d = src[(y1 * w + x0) * 3 + c], e = src[(y1 * w + x1) * 3 + c];
                        float top = a + (b - a) * tx, bottom = d + (e - d) * tx;
                        dst[(y * Size + x) * 3 + c] = (top + (bottom - top) * ty) / 255f;
                    }
                }
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

        public void Dispose()
        {
            siglip.Dispose();
            dino.Dispose();
        }
    }

    public static class Analyzer
    {
        public const int Width = 320, Height = 180, DecodeFps = 10;
        const int PerStep = (int)(DecodeFps * Analysis.Step);
        const int Batch = 16;

        // Decodes at 10 fps, measures motion on every frame, sends 2 frames a second to the models.
        public static Analysis Run(string ffmpeg, VideoInfo v, Models models, Action<double> progress, CancelFlag cancel)
        {
            var a = new Analysis { Key = Analysis.KeyFor(v.Path), Duration = v.Duration };
            int expected = (int)Math.Ceiling(v.Duration / Analysis.Step) + 2;
            var motion = new List<float>(expected);
            var zero = new List<float>(expected);
            var sig = new List<float[]>(expected);
            var dn = new List<float[]>(expected);
            var previews = new List<byte[]>(expected);
            var batch = new List<byte[]>(Batch);

            string args = string.Format("-hide_banner -loglevel error -nostdin -hwaccel d3d11va -i \"{0}\" -an -sn -dn -vf fps={1},scale={2}:{3} -pix_fmt rgb24 -f rawvideo pipe:1",
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
                            var copy = (byte[])frame.Clone();
                            batch.Add(copy);
                            previews.Add(Jpeg(copy));
                            if (batch.Count == Batch) Flush(models, batch, sig, dn, zero);
                            progress(Math.Min(0.99, index / (v.Duration * DecodeFps)));
                        }
                        prev = prev ?? new byte[frameBytes];
                        Buffer.BlockCopy(frame, 0, prev, 0, frameBytes);
                        index++;
                    }
                    if (batch.Count > 0) Flush(models, batch, sig, dn, zero);
                    proc.WaitForExit();
                }
                finally
                {
                    cancel.Current = null;
                    try { if (!proc.HasExited) proc.Kill(); } catch { }
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
                a.ZeroShot[i] = zero[i];
                a.Siglip[i] = sig[i];
                a.Dino[i] = dn[i];
                a.Previews[i] = previews[i];
            }
            progress(1);
            return a;
        }

        static void Flush(Models models, List<byte[]> batch, List<float[]> sig, List<float[]> dn, List<float> zero)
        {
            int n = batch.Count;
            var s = new float[n][];
            var d = new float[n][];
            var z = new float[n];
            models.Embed(batch, Width, Height, s, d, z);
            sig.AddRange(s);
            dn.AddRange(d);
            zero.AddRange(z);
            batch.Clear();
        }

        static bool ReadFull(Stream s, byte[] buffer)
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
