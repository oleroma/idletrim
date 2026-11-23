using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace IdleTrim
{
    // The text prompts a prompt model compares frames with. The texts are yours (or the built-in ones); each prompt model
    // turns them into its own embeddings with its text side, which are kept per model until the texts change.
    public class PromptSet
    {
        public readonly List<string> Action = new List<string>(), Idle = new List<string>();
        public bool Custom;

        // One model's embeddings of these texts.
        public class Embedded
        {
            public readonly List<float[]> Action = new List<float[]>(), Idle = new List<float[]>();

            // Best action prompt minus best idle prompt, for one normalised image embedding.
            public float Score(float[] image)
            {
                float a = float.MinValue, b = float.MinValue;
                foreach (var p in Action) a = Math.Max(a, Models.Dot(image, p));
                foreach (var p in Idle) b = Math.Max(b, Models.Dot(image, p));
                return a - b;
            }

            public float[] Scores(float[][] images)
            {
                var r = new float[images.Length];
                for (int i = 0; i < images.Length; i++) r[i] = Score(images[i]);
                return r;
            }
        }

        static PromptSet current;
        static readonly object gate = new object();
        static readonly Dictionary<string, Embedded> embedded = new Dictionary<string, Embedded>();

        static string UserPath { get { return Path.Combine(Analysis.DataDir, "prompts.txt"); } }
        static string EmbeddingPath(string modelId) { return Path.Combine(Analysis.DataDir, "prompts", ModelInfo.Slug(modelId) + ".txt"); }

        public static PromptSet Current
        {
            get
            {
                lock (gate)
                {
                    if (current == null)
                    {
                        try
                        {
                            if (File.Exists(UserPath))
                            {
                                Embedded legacy;
                                current = Parse(File.ReadAllText(UserPath), out legacy);
                                current.Custom = true;
                                // Saved by the first version, with SigLIP 2 base's embeddings inline.
                                if (legacy != null) embedded[Catalog.SiglipBase] = legacy;
                            }
                        }
                        catch (Exception) { current = null; }
                        if (current == null) current = BuiltIn();
                    }
                    return current;
                }
            }
        }

        public static PromptSet BuiltIn()
        {
            Embedded unused;
            return BuiltIn(out unused);
        }

        // The built-in texts, with SigLIP 2 base's embeddings of them, made when the app was built.
        static PromptSet BuiltIn(out Embedded baseEmbeddings)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("IdleTrim.prompts.txt"))
            using (var reader = new StreamReader(stream))
                return Parse(reader.ReadToEnd(), out baseEmbeddings);
        }

        // "<action|idle>\t<prompt>", each optionally followed by a line of numbers (that prompt's embedding).
        static PromptSet Parse(string text, out Embedded emb)
        {
            var set = new PromptSet();
            emb = new Embedded();
            bool all = true;
            var lines = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("#") || line.Length == 0) continue;
                int tab = line.IndexOf('\t');
                if (tab < 0) continue;
                bool action = line.Substring(0, tab) == "action";
                (action ? set.Action : set.Idle).Add(line.Substring(tab + 1));
                if (i + 1 < lines.Length && lines[i + 1].Length > 0 && (char.IsDigit(lines[i + 1][0]) || lines[i + 1][0] == '-'))
                {
                    string[] parts = lines[++i].Split(' ');
                    var v = new float[parts.Length];
                    for (int k = 0; k < parts.Length; k++) v[k] = float.Parse(parts[k], CultureInfo.InvariantCulture);
                    (action ? emb.Action : emb.Idle).Add(v);
                }
                else all = false;
            }
            if (set.Action.Count == 0 || set.Idle.Count == 0) throw new Exception("A prompt list needs at least one action and one idle prompt.");
            if (!all) emb = null;
            return set;
        }

        static string Serialize(PromptSet set, Embedded emb)
        {
            var sb = new StringBuilder("# IdleTrim prompts" + (emb != null ? " with one model's embeddings (L2-normalised)" : "") + ".\n");
            for (int k = 0; k < 2; k++)
            {
                var names = k == 0 ? set.Action : set.Idle;
                for (int i = 0; i < names.Count; i++)
                {
                    sb.Append(k == 0 ? "action\t" : "idle\t").Append(names[i]).Append('\n');
                    if (emb == null) continue;
                    var e = (k == 0 ? emb.Action : emb.Idle)[i];
                    sb.Append(string.Join(" ", e.Select(x => x.ToString("0.######", CultureInfo.InvariantCulture)))).Append('\n');
                }
            }
            return sb.ToString();
        }

        bool SameTexts(PromptSet other) { return Action.SequenceEqual(other.Action) && Idle.SequenceEqual(other.Idle); }

        // This model's embeddings of the current texts, or null if they haven't been made yet (see Embed).
        public static Embedded For(string modelId)
        {
            var set = Current;
            lock (gate)
            {
                Embedded e;
                if (embedded.TryGetValue(modelId, out e)) return e;
                if (modelId == Catalog.SiglipBase && !set.Custom)
                {
                    BuiltIn(out e);
                    return embedded[modelId] = e;
                }
                try
                {
                    string path = EmbeddingPath(modelId);
                    if (File.Exists(path))
                    {
                        var saved = Parse(File.ReadAllText(path), out e);
                        if (e != null && saved.SameTexts(set)) return embedded[modelId] = e;
                    }
                }
                catch (Exception) { }
                return null;
            }
        }

        // Makes this model's embeddings of the current texts with its text side, and keeps them.
        public static Embedded Embed(ModelInfo model)
        {
            var set = Current;
            var e = new Embedded();
            using (var enc = new TextEncoder(model))
            {
                var all = enc.Embed(set.Action.Concat(set.Idle).ToList());
                e.Action.AddRange(all.Take(set.Action.Count));
                e.Idle.AddRange(all.Skip(set.Action.Count));
            }
            string path = EmbeddingPath(model.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Serialize(set, e));
            lock (gate) embedded[model.Id] = e;
            return e;
        }

        // New texts: saved, and every model's embeddings start over.
        public static void SetTexts(List<string> action, List<string> idle)
        {
            var b = BuiltIn();
            bool builtIn = action.SequenceEqual(b.Action) && idle.SequenceEqual(b.Idle);
            var set = new PromptSet { Custom = !builtIn };
            set.Action.AddRange(action);
            set.Idle.AddRange(idle);
            if (builtIn) { try { File.Delete(UserPath); } catch (IOException) { } }
            else
            {
                Directory.CreateDirectory(Analysis.DataDir);
                File.WriteAllText(UserPath + ".tmp", Serialize(set, null));
                File.Move(UserPath + ".tmp", UserPath, true);
            }
            lock (gate)
            {
                current = set;
                embedded.Clear();
                Version++;
            }
        }

        public static int Version;   // changes when the texts do, so scores made from older embeddings are redone
    }

    // A prompt model's text side: turns prompts into embeddings.
    public class TextEncoder : IDisposable
    {
        readonly Tokenizer tokenizer;
        readonly InferenceSession session;
        public readonly int Length;

        public TextEncoder(ModelInfo model)
        {
            string tokenizerPath = model.PathOf("tokenizer.json");
            tokenizer = new Tokenizer(tokenizerPath);
            Length = Tokenizer.FixedLength(tokenizerPath, 64);
            bool gpu;
            session = Models.Open(model.PathOf(model.TextFile), out gpu);
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
                var pooled = (outs.FirstOrDefault(o => o.Name == "pooler_output") ?? outs.Last()).AsTensor<float>();
                int dim = pooled.Dimensions[pooled.Dimensions.Length - 1];
                for (int b = 0; b < n; b++)
                {
                    var e = new float[dim];
                    for (int i = 0; i < dim; i++) e[i] = pooled[b, i];
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

        // The fixed length the tokenizer pads to (SigLIP 2: 64), from tokenizer.json.
        public static int FixedLength(string path, int fallback)
        {
            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllBytes(path)))
                    if (doc.RootElement.TryGetProperty("padding", out var p) && p.ValueKind == JsonValueKind.Object &&
                        p.TryGetProperty("strategy", out var st) && st.ValueKind == JsonValueKind.Object && st.TryGetProperty("Fixed", out var f))
                        return f.GetInt32();
            }
            catch (Exception) { }
            return fallback;
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
