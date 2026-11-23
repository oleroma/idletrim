using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IdleTrim
{
    // Prompts: an image-and-text model (SigLIP 2) that compares frames with your prompts. Visual: an image-only model
    // (DINOv2) that learns from the prompt model and from your edits.
    public enum ModelKind { Prompts, Visual }

    // A model from Hugging Face in ONNX form: which files to fetch and how to prepare frames for it.
    public class ModelInfo
    {
        public string Id { get; set; }             // Hugging Face repo id, e.g. onnx-community/siglip2-base-patch16-224-ONNX
        public string Name { get; set; }
        public ModelKind Kind { get; set; }
        public string VisionFile { get; set; }     // repo path of the image model
        public string TextFile { get; set; }       // repo path of the text model (prompt models)
        public List<string> Files { get; set; } = new List<string>();   // everything to download, external weight files included
        public int InputSize { get; set; }
        public float[] Mean { get; set; }
        public float[] Std { get; set; }
        public double DownloadMB { get; set; }
        public string Note { get; set; }

        public static string Slug(string id)
        {
            var chars = id.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '.' ? c : '_').ToArray();
            return new string(chars);
        }

        public string Dir { get { return Path.Combine(Models.Dir, Slug(Id)); } }

        // The first two models were kept in models\siglip2 and models\dinov2-small; use those copies if they're there.
        public string PathOf(string repoFile)
        {
            string path = Path.Combine(Dir, repoFile.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) return path;
            string legacy = null;
            if (Id == Catalog.SiglipBase) legacy = Path.Combine(Models.Dir, "siglip2", Path.GetFileName(repoFile));
            if (Id == Catalog.DinoSmall && repoFile == "onnx/model.onnx") legacy = Path.Combine(Models.Dir, "dinov2-small", "model.onnx");
            return legacy != null && File.Exists(legacy) ? legacy : path;
        }

        public bool Present() { return Files.All(f => File.Exists(PathOf(f))); }

        public void Download(Action<int, string> progress, CancelFlag cancel)
        {
            var list = Files.Where(f => !File.Exists(PathOf(f)))
                            .Select(f => new[] { "https://huggingface.co/" + Id + "/resolve/main/" + f, Path.Combine(Dir, f.Replace('/', Path.DirectorySeparatorChar)), Name })
                            .ToArray();
            Models.Fetch(list, progress, cancel);
        }

        public string SizeText { get { return DownloadMB >= 1000 ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", DownloadMB / 1000) : string.Format(CultureInfo.InvariantCulture, "{0:0} MB", DownloadMB); } }
    }

    // The models IdleTrim offers, plus any you add from Hugging Face (saved in the data folder).
    public static class Catalog
    {
        public const string SiglipBase = "onnx-community/siglip2-base-patch16-224-ONNX";
        public const string DinoSmall = "onnx-community/dinov2-small-ONNX";
        static readonly float[] Half = { 0.5f, 0.5f, 0.5f }, ImageNetMean = { 0.485f, 0.456f, 0.406f }, ImageNetStd = { 0.229f, 0.224f, 0.225f };

        static ModelInfo Siglip(string id, string name, int size, double mb, bool externalVision, string note)
        {
            var m = new ModelInfo
            {
                Id = id, Name = name, Kind = ModelKind.Prompts, InputSize = size, Mean = Half, Std = Half, DownloadMB = mb, Note = note,
                VisionFile = "onnx/vision_model_fp16.onnx", TextFile = "onnx/text_model_fp16.onnx"
            };
            m.Files.AddRange(new[] { m.VisionFile, m.TextFile, "tokenizer.json" });
            if (externalVision) m.Files.Add(m.VisionFile + "_data");
            return m;
        }

        static ModelInfo Dino(string id, string name, string file, double mb, bool external, string note)
        {
            var m = new ModelInfo { Id = id, Name = name, Kind = ModelKind.Visual, InputSize = 224, Mean = ImageNetMean, Std = ImageNetStd, DownloadMB = mb, Note = note, VisionFile = file };
            m.Files.Add(file);
            if (external) m.Files.Add(file + "_data");
            return m;
        }

        public static readonly List<ModelInfo> BuiltIn = new List<ModelInfo>
        {
            Siglip(SiglipBase, "SigLIP 2 base, 224 px", 224, 786, false, "Fast; fine on any GPU."),
            Siglip("onnx-community/siglip2-large-patch16-384-ONNX", "SigLIP 2 large, 384 px", 384, 1800, false, "Sharper frames, better judgement; about 4 GB of GPU memory."),
            Siglip("onnx-community/siglip2-so400m-patch14-384-ONNX", "SigLIP 2 so400m, 384 px", 384, 2310, false, "Best quality that runs well on 8 GB."),
            Siglip("onnx-community/siglip2-giant-opt-patch16-384-ONNX", "SigLIP 2 giant, 384 px", 384, 3780, true, "Biggest; needs 8 GB or more and is slow."),
            Dino(DinoSmall, "DINOv2 small", "onnx/model.onnx", 88, false, "Fast; fine on any GPU."),
            Dino("onnx-community/dinov2-base-ONNX", "DINOv2 base", "onnx/model_fp16.onnx", 176, false, "Finer detail."),
            Dino("onnx-community/dinov2-large-ONNX", "DINOv2 large", "onnx/model_fp16.onnx", 612, false, "Best quality that runs well on 8 GB."),
            Dino("onnx-community/dinov2-with-registers-giant", "DINOv2 giant", "onnx/model_fp16.onnx", 2274, true, "Biggest; for 12 GB or more."),
        };

        static string CustomPath { get { return Path.Combine(Analysis.DataDir, "models.json"); } }

        public static List<ModelInfo> Custom()
        {
            try { if (File.Exists(CustomPath)) return JsonSerializer.Deserialize<List<ModelInfo>>(File.ReadAllText(CustomPath)) ?? new List<ModelInfo>(); }
            catch (Exception) { }
            return new List<ModelInfo>();
        }

        public static List<ModelInfo> All(ModelKind kind)
        {
            return BuiltIn.Concat(Custom()).Where(m => m.Kind == kind).ToList();
        }

        public static ModelInfo Find(string id)
        {
            return id == null ? null : BuiltIn.Concat(Custom()).FirstOrDefault(m => m.Id == id);
        }

        // Looks a repo up on Hugging Face and adds it if it's an ONNX SigLIP or DINOv2 model. Returns it, or throws why not.
        public static ModelInfo AddFromHub(string repoId)
        {
            repoId = repoId.Trim().Trim('/');
            if (repoId.StartsWith("https://huggingface.co/")) repoId = repoId.Substring("https://huggingface.co/".Length).Trim('/');
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            {
                Func<string, JsonDocument> get = url =>
                {
                    var r = http.GetAsync(url).Result;
                    if (!r.IsSuccessStatusCode) throw new Exception("Hugging Face said " + (int)r.StatusCode + " for " + url);
                    return JsonDocument.Parse(r.Content.ReadAsStringAsync().Result);
                };
                var sizes = new Dictionary<string, long>();
                using (var info = get("https://huggingface.co/api/models/" + repoId + "?blobs=true"))
                    foreach (var s in info.RootElement.GetProperty("siblings").EnumerateArray())
                        sizes[s.GetProperty("rfilename").GetString()] = s.TryGetProperty("size", out var z) ? z.GetInt64() : 0;
                string type;
                using (var config = get("https://huggingface.co/" + repoId + "/resolve/main/config.json"))
                    type = config.RootElement.TryGetProperty("model_type", out var t) ? t.GetString() : "";
                JsonElement pre;
                using (var p = get("https://huggingface.co/" + repoId + "/resolve/main/preprocessor_config.json")) pre = p.RootElement.Clone();

                Func<string, string> pick = name =>
                    sizes.ContainsKey("onnx/" + name + "_fp16.onnx") ? "onnx/" + name + "_fp16.onnx" : sizes.ContainsKey("onnx/" + name + ".onnx") ? "onnx/" + name + ".onnx" : null;
                float[] mean = pre.TryGetProperty("image_mean", out var mn) ? mn.EnumerateArray().Select(x => x.GetSingle()).ToArray() : Half;
                float[] std = pre.TryGetProperty("image_std", out var sd) ? sd.EnumerateArray().Select(x => x.GetSingle()).ToArray() : Half;
                Func<string, int> side = prop => pre.TryGetProperty(prop, out var sz) && sz.ValueKind == JsonValueKind.Object && sz.TryGetProperty("height", out var h) ? h.GetInt32() : 0;

                var m = new ModelInfo { Id = repoId, Name = repoId.Split('/').Last(), Mean = mean, Std = std, Note = "Added from Hugging Face." };
                if (type.StartsWith("siglip"))
                {
                    m.Kind = ModelKind.Prompts;
                    m.VisionFile = pick("vision_model");
                    m.TextFile = pick("text_model");
                    if (m.VisionFile == null || m.TextFile == null || !sizes.ContainsKey("tokenizer.json"))
                        throw new Exception("This repo has no ONNX vision_model and text_model with a tokenizer.json, which IdleTrim needs.");
                    m.Files.AddRange(new[] { m.VisionFile, m.TextFile, "tokenizer.json" });
                    m.InputSize = side("size") > 0 ? side("size") : 224;
                }
                else if (type.StartsWith("dinov2"))
                {
                    m.Kind = ModelKind.Visual;
                    m.VisionFile = pick("model");
                    if (m.VisionFile == null) throw new Exception("This repo has no ONNX model.onnx, which IdleTrim needs.");
                    m.Files.Add(m.VisionFile);
                    m.InputSize = side("crop_size") > 0 ? side("crop_size") : 224;
                }
                else throw new Exception("IdleTrim can use SigLIP and DINOv2 models; this one is \"" + type + "\".");
                foreach (var f in m.Files.ToList()) if (sizes.ContainsKey(f + "_data")) m.Files.Add(f + "_data");
                m.DownloadMB = m.Files.Sum(f => sizes.TryGetValue(f, out var b) ? b : 0) / 1e6;

                var custom = Custom();
                custom.RemoveAll(c => c.Id == m.Id);
                if (BuiltIn.All(b => b.Id != m.Id)) custom.Add(m);
                Directory.CreateDirectory(Analysis.DataDir);
                File.WriteAllText(CustomPath, JsonSerializer.Serialize(custom, new JsonSerializerOptions { WriteIndented = true }));
                return Find(m.Id);
            }
        }
    }

    // Turns frames into normalised embeddings with one model's image side, on the GPU when it can.
    public class ImageEncoder : IDisposable
    {
        public readonly ModelInfo Info;
        readonly InferenceSession session;
        readonly string input;
        public readonly bool OnGpu;

        public ImageEncoder(ModelInfo info)
        {
            Info = info;
            session = Models.Open(info.PathOf(info.VisionFile), out OnGpu);
            input = session.InputMetadata.Keys.First();
        }

        // Bigger inputs take much more GPU memory per frame, so they go in smaller batches.
        public int Batch { get { return Info.InputSize >= 384 ? 4 : 16; } }

        // frames: RGB24, width x height each.
        public float[][] Embed(IList<byte[]> frames, int width, int height)
        {
            int n = frames.Count, size = Info.InputSize;
            var t = new DenseTensor<float>(new[] { n, 3, size, size });
            var resized = new float[size * size * 3];
            for (int b = 0; b < n; b++)
            {
                Resize(frames[b], width, height, resized, size);
                for (int c = 0; c < 3; c++)
                {
                    float mean = Info.Mean[c], std = Info.Std[c];
                    for (int y = 0; y < size; y++)
                        for (int x = 0; x < size; x++)
                            t[b, c, y, x] = (resized[(y * size + x) * 3 + c] - mean) / std;
                }
            }
            var result = new float[n][];
            using (var outs = session.Run(new[] { NamedOnnxValue.CreateFromTensor(input, t) }))
            {
                // SigLIP gives a pooled embedding; DINOv2 gives tokens, of which the first (CLS) describes the whole image.
                var pooled = outs.FirstOrDefault(o => o.Name == "pooler_output") ?? outs.First();
                var tensor = pooled.AsTensor<float>();
                bool tokens = tensor.Dimensions.Length == 3;
                int dim = tensor.Dimensions[tensor.Dimensions.Length - 1];
                for (int b = 0; b < n; b++)
                {
                    var e = new float[dim];
                    for (int i = 0; i < dim; i++) e[i] = tokens ? tensor[b, 0, i] : tensor[b, i];
                    result[b] = Models.Normalise(e);
                }
            }
            return result;
        }

        // Bilinear resize of an RGB24 frame to size x size, values 0..1, interleaved.
        static void Resize(byte[] src, int w, int h, float[] dst, int size)
        {
            float sx = (float)w / size, sy = (float)h / size;
            for (int y = 0; y < size; y++)
            {
                float fy = Math.Max(0, (y + 0.5f) * sy - 0.5f);
                int y0 = Math.Min(h - 1, (int)fy), y1 = Math.Min(h - 1, y0 + 1);
                float ty = fy - y0;
                for (int x = 0; x < size; x++)
                {
                    float fx = Math.Max(0, (x + 0.5f) * sx - 0.5f);
                    int x0 = Math.Min(w - 1, (int)fx), x1 = Math.Min(w - 1, x0 + 1);
                    float tx = fx - x0;
                    for (int c = 0; c < 3; c++)
                    {
                        float a = src[(y0 * w + x0) * 3 + c], b = src[(y0 * w + x1) * 3 + c];
                        float d = src[(y1 * w + x0) * 3 + c], e = src[(y1 * w + x1) * 3 + c];
                        float top = a + (b - a) * tx, bottom = d + (e - d) * tx;
                        dst[(y * size + x) * 3 + c] = (top + (bottom - top) * ty) / 255f;
                    }
                }
            }
        }

        public void Dispose() { session.Dispose(); }
    }

    // Detect: runs the chosen models over the video, two frames a second, and stores each one's features with the analysis.
    // Reads the small proxy when there is one, so it decodes quickly.
    public static class Detector
    {
        public const int W = 640, H = 360;

        public static void Run(string ffmpeg, Analysis a, string source, IList<ModelInfo> models, Action<double, string> progress, CancelFlag cancel)
        {
            if (models.Count == 0) return;
            progress(0, "Loading " + string.Join(" and ", models.Select(m => m.Name)));
            var encoders = new List<ImageEncoder>();
            try
            {
                foreach (var m in models) encoders.Add(new ImageEncoder(m));
                var results = encoders.Select(_ => new List<float[]>(a.Count)).ToList();
                var batch = new List<byte[]>();
                int batchSize = encoders.Min(e => e.Batch);
                Action flush = () =>
                {
                    for (int k = 0; k < encoders.Count; k++) results[k].AddRange(encoders[k].Embed(batch, W, H));
                    batch.Clear();
                };
                string args = string.Format(CultureInfo.InvariantCulture,
                    "-hide_banner -loglevel error -nostdin -i \"{0}\" -an -sn -dn -vf fps={1:0.###},scale={2}:{3} -pix_fmt rgb24 -f rawvideo pipe:1", source, 1 / Analysis.Step, W, H);
                var psi = new ProcessStartInfo(ffmpeg, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = new Process { StartInfo = psi })
                {
                    p.ErrorDataReceived += (o, e) => { };
                    p.Start();
                    cancel.Current = p;
                    p.BeginErrorReadLine();
                    var stdout = p.StandardOutput.BaseStream;
                    int done = 0;
                    try
                    {
                        while (done < a.Count)
                        {
                            var frame = new byte[W * H * 3];
                            if (!Indexer.ReadFull(stdout, frame)) break;
                            if (cancel.Cancelled) throw new CancelledException();
                            batch.Add(frame);
                            done++;
                            if (batch.Count == batchSize) { flush(); progress((double)done / a.Count, "Detecting"); }
                        }
                        if (batch.Count > 0) flush();
                    }
                    finally
                    {
                        cancel.Current = null;
                        try { if (!p.HasExited) p.Kill(); } catch { }
                    }
                    if (cancel.Cancelled) throw new CancelledException();
                }
                for (int k = 0; k < encoders.Count; k++)
                {
                    var f = results[k];
                    if (f.Count == 0) throw new Exception("Couldn't read any frames to detect on.");
                    while (f.Count < a.Count) f.Add(f[f.Count - 1]);   // the last half second may be short a frame
                    a.SaveFeatures(models[k].Id, f.Take(a.Count).ToArray());
                }
                progress(1, "Detecting");
            }
            finally
            {
                foreach (var e in encoders) e.Dispose();
            }
        }
    }
}
