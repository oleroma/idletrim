using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace IdleTrim
{
    // What the window looked like when it closed, so the next start carries on there. Saved as JSON in the data folder.
    public class Session
    {
        public string Video { get; set; }
        public double Playhead { get; set; }
        public double ViewStart { get; set; }
        public double ViewLength { get; set; }

        public int Model { get; set; } = 2;
        public int Sensitivity { get; set; } = 50;
        public int Padding { get; set; } = 15;
        public bool Learn { get; set; } = true;
        public bool PlayAll { get; set; }
        public bool AskBeforeReplacing { get; set; } = true;

        // Output settings for Video.
        public string Encoder { get; set; }
        public bool Mute { get; set; }
        public bool Copy { get; set; } = true;
        public bool TargetAtMax { get; set; } = true;
        public double TargetMB { get; set; } = 10;
        public bool AutoRes { get; set; } = true;
        public int Short { get; set; }
        public int Fps { get; set; }

        static string PathOf { get { return System.IO.Path.Combine(Analysis.DataDir, "session.json"); } }

        public static Session Load()
        {
            try { if (File.Exists(PathOf)) return JsonSerializer.Deserialize<Session>(File.ReadAllText(PathOf)) ?? new Session(); }
            catch (Exception) { }
            return new Session();
        }

        public void Save()
        {
            Directory.CreateDirectory(Analysis.DataDir);
            File.WriteAllText(PathOf + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(PathOf + ".tmp", PathOf, true);
        }
    }

    // Your hand edits of one video (parts added and removed, splits), kept per video so they come back when it's opened again.
    public static class Edits
    {
        static string PathOf(string key) { return Path.Combine(Analysis.DataDir, "edits", key + ".bin"); }

        public static void Save(string key, sbyte[] overrides, HashSet<int> splits)
        {
            string path = PathOf(key);
            bool any = splits.Count > 0;
            foreach (var o in overrides) if (o != 0) { any = true; break; }
            if (!any) { try { File.Delete(path); } catch (IOException) { } return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var w = new BinaryWriter(File.Create(path + ".tmp")))
            {
                w.Write("IdleTrimEdits1");
                w.Write(overrides.Length);
                foreach (var o in overrides) w.Write(o);
                w.Write(splits.Count);
                foreach (var sp in splits) w.Write(sp);
            }
            File.Move(path + ".tmp", path, true);
        }

        public static bool Load(string key, int count, out sbyte[] overrides, out HashSet<int> splits)
        {
            overrides = null;
            splits = null;
            try
            {
                string path = PathOf(key);
                if (!File.Exists(path)) return false;
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (r.ReadString() != "IdleTrimEdits1" || r.ReadInt32() != count) return false;
                    overrides = new sbyte[count];
                    for (int i = 0; i < count; i++) overrides[i] = r.ReadSByte();
                    int n = r.ReadInt32();
                    splits = new HashSet<int>();
                    for (int i = 0; i < n; i++) splits.Add(r.ReadInt32());
                    return true;
                }
            }
            catch (Exception) { overrides = null; splits = null; return false; }
        }
    }
}
