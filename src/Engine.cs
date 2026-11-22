using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;

namespace IdleTrim
{
    public class VideoInfo
    {
        public string Path;
        public long SizeBytes;
        public double Duration;
        public int Width, Height;
        public double Fps;
        public bool HasAudio;

        public int ShortSide { get { return Math.Min(Width, Height); } }
        public int LongSide { get { return Math.Max(Width, Height); } }
        public int MaxFps { get { return Math.Max(1, (int)Math.Round(Fps)); } }
    }

    public class Settings
    {
        public double TargetMB = 10;
        public double TrimStart;
        public double TrimEnd;   // 0 means the end of the video
        public bool AutoRes = true, AutoFps = true;
        public int Short;        // used when AutoRes is off
        public int Fps;          // used when AutoFps is off
        public bool Nvenc, Mute;
        public List<Segment> Segments;   // when set, only these parts are kept, joined in order; trim is ignored
    }

    public class Segment
    {
        public double Start, End;   // seconds in the source

        public Segment(double start, double end) { Start = start; End = end; }

        public double Length { get { return End - Start; } }

        public static double Total(List<Segment> segments)
        {
            double sum = 0;
            foreach (var s in segments) sum += s.Length;
            return sum;
        }
    }

    public class Plan
    {
        public double Kept;
        public double MinMB, MaxMB, TargetMB;
        public bool CanReduce;
        public int AudioKbps, VideoKbps;
        public List<int> Rungs;
        public int Short, Fps, MinFps, ResCap, FpsCap;
        public int OutLong;
    }

    public class CancelFlag
    {
        public volatile bool Cancelled;
        public volatile Process Current;

        public void Cancel()
        {
            Cancelled = true;
            var p = Current;
            try { if (p != null && !p.HasExited) p.Kill(); } catch { }
        }
    }

    public class CancelledException : Exception { }

    public static class Engine
    {
        const double Mb = 1024 * 1024;
        // Share of the target given to the encoder; the rest is headroom for overshoot and muxing.
        const double Usable = 0.95;
        // Bits per pixel per frame. Below the floor the encoder can't reliably hit the bitrate;
        // Auto aims for the goal.
        const double FloorX264 = 0.01, FloorNvenc = 0.02, Goal = 0.1;
        const int MinVideoX264 = 50, MinVideoNvenc = 100, MinAudio = 48, FpsFloor = 24;
        static readonly int[] Ladder = { 144, 240, 360, 480, 540, 720, 900, 1080, 1440, 2160, 2880, 4320 };

        public const string FfmpegZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

        static long Pixels(VideoInfo v, int shortSide)
        {
            return (long)shortSide * OutLong(v, shortSide);
        }

        static int OutLong(VideoInfo v, int shortSide)
        {
            return (int)Math.Round((double)v.LongSide * shortSide / v.ShortSide / 2) * 2;
        }

        static double Bpp(VideoInfo v, int shortSide, int fps, int videoKbps)
        {
            return videoKbps * 1000.0 / (Pixels(v, shortSide) * (double)fps);
        }

        public static Plan MakePlan(VideoInfo v, Settings s)
        {
            var p = new Plan();
            double end = s.TrimEnd > 0 ? Math.Min(s.TrimEnd, v.Duration) : v.Duration;
            p.Kept = Math.Max(0.1, s.Segments != null ? Segment.Total(s.Segments) : end - s.TrimStart);

            bool hasAudio = v.HasAudio && !s.Mute;
            int minVideo = s.Nvenc ? MinVideoNvenc : MinVideoX264;
            double floor = s.Nvenc ? FloorNvenc : FloorX264;

            double minBytes = (minVideo + (hasAudio ? MinAudio : 0)) * 1000.0 * p.Kept / 8 / Usable;
            double sourceMB = v.SizeBytes * (p.Kept / v.Duration) / Mb;
            p.MinMB = Math.Max(0.1, Math.Ceiling(minBytes / Mb * 10) / 10);
            p.MaxMB = Math.Floor(sourceMB * 10) / 10;
            if (p.MaxMB >= sourceMB) p.MaxMB -= 0.1;   // never allow a target at or above the source
            p.CanReduce = p.MaxMB >= p.MinMB;
            p.TargetMB = Math.Max(p.MinMB, Math.Min(p.MaxMB, s.TargetMB));

            double totalKbps = p.TargetMB * Mb * 8 * Usable / p.Kept / 1000;
            p.AudioKbps = !hasAudio ? 0 : totalKbps >= 1000 ? 128 : totalKbps >= 300 ? 96 : totalKbps >= 120 ? 64 : MinAudio;
            p.VideoKbps = Math.Max(1, (int)(totalKbps - p.AudioKbps));

            p.Rungs = MakeRungs(v);
            p.MinFps = Math.Min(FpsFloor, v.MaxFps);

            Func<int, int> resCap = fps =>
            {
                int best = p.Rungs[0];
                foreach (int r in p.Rungs)
                    if (Bpp(v, r, fps, p.VideoKbps) >= floor) best = r;
                return best;
            };
            Func<int, int> fpsCap = shortSide =>
            {
                int f = (int)(p.VideoKbps * 1000.0 / (Pixels(v, shortSide) * floor));
                return Math.Max(p.MinFps, Math.Min(v.MaxFps, f));
            };
            Func<int, int> bestResAtGoal = fps =>
            {
                int best = p.Rungs[0];
                foreach (int r in p.Rungs)
                    if (Bpp(v, r, fps, p.VideoKbps) >= Goal) best = r;
                return best;
            };

            if (s.AutoRes && s.AutoFps)
            {
                // Spend the bitrate on resolution before motion: 60 -> 30 fps first,
                // then step resolution down, and only then drop to the fps floor.
                int top = p.Rungs[p.Rungs.Count - 1];
                p.Fps = v.MaxFps;
                if (Bpp(v, top, p.Fps, p.VideoKbps) < Goal && p.Fps > 30) p.Fps = 30;
                p.Short = bestResAtGoal(p.Fps);
                if (Bpp(v, p.Short, p.Fps, p.VideoKbps) < Goal) p.Fps = p.MinFps;
            }
            else if (s.AutoRes)
            {
                p.Fps = Clamp(s.Fps, p.MinFps, v.MaxFps);
                p.Short = bestResAtGoal(p.Fps);
                p.Fps = Math.Min(p.Fps, fpsCap(p.Short));
            }
            else if (s.AutoFps)
            {
                p.Short = Math.Min(NearestRung(p.Rungs, s.Short), resCap(p.MinFps));
                p.Fps = p.MinFps;
                foreach (int f in new[] { v.MaxFps, 30 })
                    if (f <= v.MaxFps && f > p.Fps && Bpp(v, p.Short, f, p.VideoKbps) >= Goal) { p.Fps = f; break; }
            }
            else
            {
                p.Fps = Clamp(s.Fps, p.MinFps, v.MaxFps);
                p.Short = Math.Min(NearestRung(p.Rungs, s.Short), resCap(p.Fps));
                p.Fps = Math.Min(p.Fps, fpsCap(p.Short));
            }

            p.ResCap = resCap(p.Fps);
            p.FpsCap = fpsCap(p.Short);
            p.OutLong = OutLong(v, p.Short);
            return p;
        }

        static List<int> MakeRungs(VideoInfo v)
        {
            int source = v.ShortSide / 2 * 2;
            var rungs = new List<int>();
            foreach (int r in Ladder)
                if (r < source) rungs.Add(r);
            rungs.Add(source);
            return rungs;
        }

        static int NearestRung(List<int> rungs, int value)
        {
            int best = rungs[0];
            foreach (int r in rungs)
                if (r <= value) best = r;
            return best;
        }

        static int Clamp(int x, int lo, int hi) { return Math.Max(lo, Math.Min(hi, x)); }

        // ---- ffmpeg discovery and download ----

        public static string FindTool(string name)
        {
            string file = name + ".exe";
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            var dirs = new List<string> { appDir, Path.Combine(appDir, "ffmpeg") };
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Links"));
            foreach (string dir in dirs)
            {
                try
                {
                    if (dir.Length == 0) continue;
                    string path = Path.Combine(dir.Trim(), file);
                    if (File.Exists(path)) return path;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        public static void DownloadFfmpeg(string destDir, Action<int> percent, CancelFlag cancel)
        {
            Directory.CreateDirectory(destDir);
            string zip = Path.Combine(destDir, "ffmpeg-download.zip");
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            try
            {
                using (var web = new WebClient())
                using (var done = new ManualResetEvent(false))
                {
                    Exception error = null;
                    web.DownloadProgressChanged += (o, e) =>
                    {
                        percent(e.ProgressPercentage);
                        if (cancel.Cancelled) web.CancelAsync();
                    };
                    web.DownloadFileCompleted += (o, e) => { error = e.Error; done.Set(); };
                    web.DownloadFileAsync(new Uri(FfmpegZipUrl), zip);
                    done.WaitOne();
                    if (cancel.Cancelled) throw new CancelledException();
                    if (error != null) throw error;
                }
                using (var archive = ZipFile.OpenRead(zip))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.Name == "ffmpeg.exe" || entry.Name == "ffprobe.exe")
                            entry.ExtractToFile(Path.Combine(destDir, entry.Name), true);
                    }
                }
                if (!File.Exists(Path.Combine(destDir, "ffmpeg.exe")) || !File.Exists(Path.Combine(destDir, "ffprobe.exe")))
                    throw new Exception("The download didn't contain ffmpeg.exe and ffprobe.exe.");
            }
            finally
            {
                try { File.Delete(zip); } catch { }
            }
        }

        public static bool HasEncoder(string ffmpeg, string encoder)
        {
            try { return Capture(ffmpeg, "-hide_banner -encoders").Contains(" " + encoder + " "); }
            catch { return false; }
        }

        // ---- probing ----

        static string Capture(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var proc = Process.Start(psi))
            {
                proc.ErrorDataReceived += (o, e) => { };
                proc.BeginErrorReadLine();
                string text = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                return text;
            }
        }

        public static VideoInfo Probe(string ffprobe, string path)
        {
            var v = new VideoInfo { Path = path, SizeBytes = new FileInfo(path).Length };
            string text = Capture(ffprobe, string.Format(
                "-v error -select_streams v:0 -show_entries stream=width,height,avg_frame_rate,r_frame_rate:format=duration -of default=nw=1 \"{0}\"", path));
            double avg = 0, raw = 0;
            foreach (string line in text.Split('\n'))
            {
                string[] kv = line.Trim().Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;
                if (kv[0] == "width") int.TryParse(kv[1], out v.Width);
                else if (kv[0] == "height") int.TryParse(kv[1], out v.Height);
                else if (kv[0] == "duration") double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v.Duration);
                else if (kv[0] == "avg_frame_rate") avg = ParseRate(kv[1]);
                else if (kv[0] == "r_frame_rate") raw = ParseRate(kv[1]);
            }
            if (v.Width <= 0 || v.Height <= 0 || v.Duration <= 0)
                throw new Exception("This file doesn't look like a video that can be read.");
            v.Fps = avg > 0 ? avg : raw > 0 ? raw : 30;
            string audio = Capture(ffprobe, string.Format(
                "-v error -select_streams a:0 -show_entries stream=codec_type -of default=nw=1 \"{0}\"", path));
            v.HasAudio = audio.Contains("audio");
            return v;
        }

        static double ParseRate(string rate)
        {
            string[] parts = rate.Split('/');
            double n, d = 1;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return 0;
            if (parts.Length > 1 && (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out d) || d == 0)) return 0;
            return n / d;
        }

        // ---- encoding ----

        public static string OutputPath(string input)
        {
            string dir = Path.GetDirectoryName(input);
            string name = Path.GetFileNameWithoutExtension(input) + "_idletrim";
            string path = Path.Combine(dir, name + ".mp4");
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(dir, string.Format("{0} ({1}).mp4", name, i));
            return path;
        }

        // Returns the size of the finished file. The output only appears once it fits the target.
        public static long Run(string ffmpeg, VideoInfo v, Settings s, Plan p, string output,
                               Action<double, string> progress, CancelFlag cancel)
        {
            string part = output + ".part.mp4";
            string passlog = Path.Combine(Path.GetTempPath(), "idletrim-" + Guid.NewGuid().ToString("N"));
            string graph = passlog + "-graph.txt";
            if (s.Segments != null)
            {
                File.WriteAllText(graph, Graph(v, s, p, true));
                File.WriteAllText(graph + ".silent", Graph(v, s, p, false));
            }
            long targetBytes = (long)(p.TargetMB * Mb);
            int minVideo = s.Nvenc ? MinVideoNvenc : MinVideoX264;
            int video = p.VideoKbps;
            try
            {
                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    string label = attempt == 1 ? "" : string.Format(" (retry {0})", attempt - 1);
                    if (s.Nvenc)
                    {
                        Encode(ffmpeg, BaseArgs(v, s, p, graph) + NvencArgs(video) + AudioArgs(s, p) + Quote(part), p.Kept,
                               f => progress(f, "Encoding" + label), cancel);
                    }
                    else
                    {
                        Encode(ffmpeg, BaseArgs(v, s, p, graph + ".silent") + X264Args(video) + string.Format(" -pass 1 -passlogfile {0} -an -f null NUL", Quote(passlog)), p.Kept,
                               f => progress(f * 0.35, "Analysing" + label), cancel);
                        Encode(ffmpeg, BaseArgs(v, s, p, graph) + X264Args(video) + string.Format(" -pass 2 -passlogfile {0}", Quote(passlog)) + AudioArgs(s, p) + Quote(part), p.Kept,
                               f => progress(0.35 + f * 0.65, "Encoding" + label), cancel);
                    }

                    long size = new FileInfo(part).Length;
                    if (size <= targetBytes)
                    {
                        File.Move(part, output);
                        progress(1, "Done");
                        return size;
                    }

                    double total = (video + p.AudioKbps) * (targetBytes * Usable / size);
                    video = (int)(total - p.AudioKbps);
                    if (video < minVideo) break;
                }
                throw new Exception("Couldn't get the file under the target size. Try a larger target or trim the video.");
            }
            finally
            {
                try { File.Delete(part); } catch { }
                foreach (string f in Directory.GetFiles(Path.GetDirectoryName(passlog), Path.GetFileName(passlog) + "*"))
                    try { File.Delete(f); } catch { }
            }
        }

        static string Quote(string path) { return " \"" + path + "\""; }

        // Works for landscape and for phone videos stored rotated.
        static string Scale(VideoInfo v, Plan p)
        {
            string fps = p.Fps < v.MaxFps ? string.Format("fps={0},", p.Fps) : "";
            return fps + string.Format("scale='if(gte(iw,ih),-2,{0})':'if(gte(iw,ih),{0},-2)'", p.Short);
        }

        static string BaseArgs(VideoInfo v, Settings s, Plan p, string graphFile)
        {
            var a = new StringBuilder("-hide_banner -loglevel error -nostats -nostdin -progress pipe:1 -y");
            if (s.Segments != null)
            {
                a.Append(" -i").Append(Quote(v.Path));
                a.Append(" -/filter_complex").Append(Quote(graphFile));
                a.Append(" -map \"[v]\" -sn -dn");
            }
            else
            {
                if (s.TrimStart > 0) a.AppendFormat(CultureInfo.InvariantCulture, " -ss {0:0.###}", s.TrimStart);
                a.Append(" -i").Append(Quote(v.Path));
                a.AppendFormat(CultureInfo.InvariantCulture, " -t {0:0.###}", p.Kept);
                a.Append(" -map 0:v:0 -sn -dn");
                a.Append(" -vf \"").Append(Scale(v, p)).Append('"');
            }
            a.Append(" -pix_fmt yuv420p");
            return a.ToString();
        }

        // Cuts each kept part out, joins them in order, then scales. Audio gets 30 ms fades at every cut so joins don't click.
        public static string Graph(VideoInfo v, Settings s, Plan p, bool audio)
        {
            var c = CultureInfo.InvariantCulture;
            var g = new StringBuilder();
            int n = s.Segments.Count;
            audio = audio && p.AudioKbps > 0;
            g.AppendFormat("[0:v:0]split={0}", n);
            for (int i = 0; i < n; i++) g.AppendFormat("[s{0}]", i);
            g.Append(';');
            if (audio)
            {
                g.AppendFormat("[0:a:0]asplit={0}", n);
                for (int i = 0; i < n; i++) g.AppendFormat("[t{0}]", i);
                g.Append(';');
            }
            for (int i = 0; i < n; i++)
            {
                var seg = s.Segments[i];
                double fade = Math.Min(0.03, seg.Length / 4);
                g.AppendFormat(c, "[s{0}]trim=start={1:0.###}:end={2:0.###},setpts=PTS-STARTPTS[v{0}];", i, seg.Start, seg.End);
                if (audio)
                    g.AppendFormat(c, "[t{0}]atrim=start={1:0.###}:end={2:0.###},asetpts=PTS-STARTPTS,afade=t=in:d={3:0.###},afade=t=out:st={4:0.###}:d={3:0.###}[a{0}];",
                                   i, seg.Start, seg.End, fade, seg.Length - fade);
            }
            for (int i = 0; i < n; i++)
            {
                g.AppendFormat("[v{0}]", i);
                if (audio) g.AppendFormat("[a{0}]", i);
            }
            g.AppendFormat("concat=n={0}:v=1:a={1}[cv]", n, audio ? 1 : 0);
            if (audio) g.Append("[a]");
            g.Append(";[cv]").Append(Scale(v, p)).Append("[v]");
            return g.ToString();
        }

        static string X264Args(int video)
        {
            return string.Format(" -c:v libx264 -preset slow -b:v {0}k -maxrate {1}k -bufsize {2}k", video, video * 3 / 2, video * 2);
        }

        static string NvencArgs(int video)
        {
            return string.Format(" -c:v h264_nvenc -preset p7 -tune hq -rc vbr -multipass fullres -b:v {0}k -maxrate {1}k -bufsize {2}k", video, video * 3 / 2, video * 2);
        }

        static string AudioArgs(Settings s, Plan p)
        {
            string source = s.Segments != null ? "\"[a]\"" : "0:a:0";
            string audio = p.AudioKbps > 0 ? string.Format(" -map {0} -c:a aac -b:a {1}k", source, p.AudioKbps) : " -an";
            return audio + " -movflags +faststart";
        }

        static void Encode(string ffmpeg, string args, double kept, Action<double> progress, CancelFlag cancel)
        {
            if (cancel.Cancelled) throw new CancelledException();
            var psi = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var errors = new Queue<string>();
            using (var proc = new Process { StartInfo = psi })
            {
                proc.OutputDataReceived += (o, e) =>
                {
                    long us;
                    if (e.Data != null && e.Data.StartsWith("out_time_ms=") && long.TryParse(e.Data.Substring(12), out us))
                        progress(Math.Max(0, Math.Min(1, us / 1000000.0 / kept)));
                };
                proc.ErrorDataReceived += (o, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    lock (errors)
                    {
                        errors.Enqueue(e.Data);
                        if (errors.Count > 6) errors.Dequeue();
                    }
                };
                proc.Start();
                cancel.Current = proc;
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();
                cancel.Current = null;
                if (cancel.Cancelled) throw new CancelledException();
                if (proc.ExitCode != 0)
                {
                    lock (errors)
                        throw new Exception("ffmpeg failed:\n" + string.Join("\n", errors.ToArray()));
                }
            }
        }
    }
}
