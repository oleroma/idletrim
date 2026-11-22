using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IdleTrim
{
    // The text prompts SigLIP 2 compares frames with. Built-in ones ship with the app; your own are saved
    // with their embeddings in the data folder, so the text model is only needed while editing them.
    public class PromptSet
    {
        public readonly List<string> Action = new List<string>(), Idle = new List<string>();
        public readonly List<float[]> ActionEmb = new List<float[]>(), IdleEmb = new List<float[]>();
        public bool Custom;

        static PromptSet current;
        static readonly object gate = new object();

        static string UserPath { get { return Path.Combine(Analysis.DataDir, "prompts.txt"); } }

        public static PromptSet Current
        {
            get
            {
                lock (gate)
                {
                    if (current == null)
                    {
                        current = null;
                        try { if (File.Exists(UserPath)) { current = Parse(File.ReadAllText(UserPath)); current.Custom = true; } }
                        catch (Exception) { current = null; }
                        if (current == null) current = BuiltIn();
                    }
                    return current;
                }
            }
        }

        public static PromptSet BuiltIn()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("IdleTrim.prompts.txt"))
            using (var reader = new StreamReader(stream))
                return Parse(reader.ReadToEnd());
        }

        // "<action|idle>\t<prompt>" then a line of 768 numbers.
        static PromptSet Parse(string text)
        {
            var set = new PromptSet();
            var lines = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("#") || line.Length == 0) continue;
                int tab = line.IndexOf('\t');
                if (tab < 0 || i + 1 >= lines.Length) continue;
                string[] parts = lines[++i].Split(' ');
                var v = new float[parts.Length];
                for (int k = 0; k < parts.Length; k++) v[k] = float.Parse(parts[k], CultureInfo.InvariantCulture);
                bool action = line.Substring(0, tab) == "action";
                (action ? set.Action : set.Idle).Add(line.Substring(tab + 1));
                (action ? set.ActionEmb : set.IdleEmb).Add(v);
            }
            if (set.Action.Count == 0 || set.Idle.Count == 0) throw new Exception("A prompt list needs at least one action and one idle prompt.");
            return set;
        }

        public string Serialize()
        {
            var sb = new StringBuilder("# SigLIP 2 base-patch16-224 text embeddings (pooler_output, L2-normalised).\n");
            for (int k = 0; k < 2; k++)
            {
                var names = k == 0 ? Action : Idle;
                var embs = k == 0 ? ActionEmb : IdleEmb;
                for (int i = 0; i < names.Count; i++)
                {
                    sb.Append(k == 0 ? "action\t" : "idle\t").Append(names[i]).Append('\n');
                    var parts = new string[embs[i].Length];
                    for (int j = 0; j < parts.Length; j++) parts[j] = embs[i][j].ToString("0.######", CultureInfo.InvariantCulture);
                    sb.Append(string.Join(" ", parts)).Append('\n');
                }
            }
            return sb.ToString();
        }

        // Saves your own prompts and makes them the current ones.
        public static void Save(PromptSet set)
        {
            Directory.CreateDirectory(Analysis.DataDir);
            File.WriteAllText(UserPath + ".tmp", set.Serialize());
            File.Move(UserPath + ".tmp", UserPath, true);
            set.Custom = true;
            lock (gate) current = set;
        }

        public static void Reset()
        {
            try { File.Delete(UserPath); } catch (IOException) { }
            lock (gate) current = BuiltIn();
        }

        // Best action prompt minus best idle prompt, for one normalised SigLIP image embedding.
        public float Score(float[] sig)
        {
            float a = float.MinValue, b = float.MinValue;
            foreach (var p in ActionEmb) a = Math.Max(a, Models.Dot(sig, p));
            foreach (var p in IdleEmb) b = Math.Max(b, Models.Dot(sig, p));
            return a - b;
        }

        public float[] Scores(Analysis an)
        {
            var r = new float[an.Count];
            for (int i = 0; i < an.Count; i++) r[i] = Score(an.Siglip[i]);
            return r;
        }
    }

    // SigLIP 2's text half: turns prompts into embeddings. About 600 MB, downloaded the first time you edit prompts.
    public class TextEncoder : IDisposable
    {
        public const string ModelUrl = "https://huggingface.co/onnx-community/siglip2-base-patch16-224-ONNX/resolve/main/onnx/text_model_fp16.onnx";
        public const string TokenizerUrl = "https://huggingface.co/onnx-community/siglip2-base-patch16-224-ONNX/resolve/main/tokenizer.json";
        public const int Length = 64;

        static string ModelPath { get { return Path.Combine(Models.Dir, "siglip2", "text_model_fp16.onnx"); } }
        static string TokenizerPath { get { return Path.Combine(Models.Dir, "siglip2", "tokenizer.json"); } }

        public static bool Present() { return File.Exists(ModelPath) && File.Exists(TokenizerPath); }

        public static void Download(Action<int, string> progress, CancelFlag cancel)
        {
            Models.Fetch(new[] { new[] { TokenizerUrl, TokenizerPath, "the tokenizer" }, new[] { ModelUrl, ModelPath, "the SigLIP 2 text model" } }, progress, cancel);
        }

        readonly Tokenizer tokenizer;
        readonly InferenceSession session;

        public TextEncoder()
        {
            tokenizer = new Tokenizer(TokenizerPath);
            // The fp16 export only loads on DirectML; on the CPU it needs graph optimisations off.
            try { session = new InferenceSession(ModelPath, Models.Options(true)); }
            catch (Exception)
            {
                var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL };
                session = new InferenceSession(ModelPath, o);
            }
        }

        public int[] Tokens(string prompt) { return tokenizer.Encode(prompt.ToLowerInvariant(), Length); }

        public float[][] Embed(IList<string> prompts)
        {
            int n = prompts.Count;
            var ids = new DenseTensor<long>(new[] { n, Length });
            for (int b = 0; b < n; b++)
            {
                var t = Tokens(prompts[b]);
                for (int i = 0; i < Length; i++) ids[b, i] = t[i];
            }
            var result = new float[n][];
            using (var outs = session.Run(new[] { NamedOnnxValue.CreateFromTensor("input_ids", ids) }))
            {
                Tensor<float> pooled = null;
                foreach (var o in outs) if (o.Name == "pooler_output") pooled = o.AsTensor<float>();
                for (int b = 0; b < n; b++)
                {
                    var e = new float[Analysis.SigDim];
                    for (int i = 0; i < e.Length; i++) e[i] = pooled[b, i];
                    result[b] = Models.Normalise(e);
                }
            }
            return result;
        }

        public void Dispose() { session.Dispose(); }
    }

    // The Gemma BPE tokenizer SigLIP 2 uses, read from its tokenizer.json: spaces become "▁", pieces merge by rank,
    // characters missing from the vocabulary fall back to their UTF-8 bytes, then <eos> and padding to a fixed length.
    public class Tokenizer
    {
        readonly Dictionary<string, int> vocab = new Dictionary<string, int>();
        readonly Dictionary<string, int> ranks = new Dictionary<string, int>();
        const int Pad = 0, Eos = 1, Unk = 3;

        public Tokenizer(string path)
        {
            using (var doc = JsonDocument.Parse(File.ReadAllBytes(path)))
            {
                var model = doc.RootElement.GetProperty("model");
                foreach (var p in model.GetProperty("vocab").EnumerateObject()) vocab[p.Name] = p.Value.GetInt32();
                int rank = 0;
                foreach (var m in model.GetProperty("merges").EnumerateArray())
                {
                    string key;
                    if (m.ValueKind == JsonValueKind.Array) key = m[0].GetString() + "\u0001" + m[1].GetString();
                    else { string s = m.GetString(); int sp = s.IndexOf(' '); key = s.Substring(0, sp) + "\u0001" + s.Substring(sp + 1); }
                    if (!ranks.ContainsKey(key)) ranks[key] = rank;
                    rank++;
                }
            }
        }

        public int[] Encode(string text, int length)
        {
            text = text.Replace(' ', '▁');
            var pieces = new List<string>();
            for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
                pieces.Add(char.IsSurrogatePair(text, i) ? text.Substring(i, 2) : text.Substring(i, 1));

            while (pieces.Count > 1)
            {
                int best = int.MaxValue, at = -1;
                for (int i = 0; i + 1 < pieces.Count; i++)
                {
                    int r;
                    if (ranks.TryGetValue(pieces[i] + "\u0001" + pieces[i + 1], out r) && r < best) { best = r; at = i; }
                }
                if (at < 0) break;
                pieces[at] = pieces[at] + pieces[at + 1];
                pieces.RemoveAt(at + 1);
            }

            var ids = new List<int>();
            foreach (string p in pieces)
            {
                int id;
                if (vocab.TryGetValue(p, out id)) { ids.Add(id); continue; }
                foreach (byte b in Encoding.UTF8.GetBytes(p))
                    ids.Add(vocab.TryGetValue(string.Format("<0x{0:X2}>", b), out id) ? id : Unk);
            }
            ids.Add(Eos);
            var r2 = new int[length];
            for (int i = 0; i < length; i++) r2[i] = i < ids.Count ? ids[i] : Pad;
            return r2;
        }
    }
}
