using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace IdleTrim
{
    // A small copy of each video used only for playback and seeking: 640x360, a keyframe every half second, AAC audio.
    // Seeking in it decodes a few small frames instead of up to two seconds of the original, so playback starts quickly
    // and uses little CPU. Exports always read the original. Lives in the cache next to the analysis.
    public static class Proxy
    {
        public const int W = 640, H = 360;

        public static string PathFor(string key) { return Path.Combine(Analysis.DataDir, "cache", key + ".proxy.mp4"); }

        public static bool Ready(string key) { return File.Exists(PathFor(key)); }

        public static double Fps(VideoInfo v) { return Math.Min(30, v.Fps); }

        // Decoding on an NVIDIA GPU with CUDA and shrinking each frame there before it's copied out is about twice as
        // fast as copying full-size frames out of the GPU and shrinking them on the CPU. Each entry: input options, and the
        // filter that turns [0:v:0] into 640x360 frames in system memory. Tried in order until one works.
        public static readonly string[][] Decoders =
        {
            new[] { "-hwaccel cuda -hwaccel_output_format cuda", "scale_cuda=" + W + ":" + H + ",hwdownload,format=nv12" },
            new[] { "-hwaccel d3d11va", "scale=" + W + ":" + H },
        };

        // The first decoder that can read a frame of this file (a one-frame test, under a second).
        public static string[] PickDecoder(string ffmpeg, string path)
        {
            foreach (var d in Decoders)
            {
                var psi = new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error -nostdin " + d[0] + " -i \"" + path + "\" -frames:v 1 -an -vf \"" + d[1] + "\" -f null -")
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

        // ffmpeg output options that write the proxy from the stream labelled [label] plus the source's audio.
        public static string OutputArgs(VideoInfo v, string label, string output)
        {
            int gop = Math.Max(1, (int)Math.Round(Fps(v) / 2));
            return string.Format(CultureInfo.InvariantCulture,
                " -map \"{0}\" -map 0:a:0? -sn -dn -c:v libx264 -preset veryfast -tune fastdecode -crf 26 -g {1} -keyint_min {1} -sc_threshold 0 -bf 0" +
                " -c:a aac -b:a 128k -ac 2 -ar 48000 -movflags +faststart -f mp4 \"{2}\"", label, gop, output);
        }

        // Builds the proxy on its own, for videos analysed before proxies existed. Runs at low priority.
        public static void Build(string ffmpeg, VideoInfo v, string key, Action<double> progress, CancelFlag cancel)
        {
            string dest = PathFor(key), part = dest + ".part";
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            Exception last = null;
            foreach (var d in Decoders)
            {
                try { BuildWith(ffmpeg, v, d, part, progress, cancel); last = null; break; }
                catch (CancelledException) { throw; }
                catch (Exception ex) { last = ex; }
            }
            try
            {
                if (last != null) throw last;
                Finish(part, dest);
            }
            finally { try { File.Delete(part); } catch { } }
        }

        static void BuildWith(string ffmpeg, VideoInfo v, string[] decoder, string part, Action<double> progress, CancelFlag cancel)
        {
            string filter = string.Format(CultureInfo.InvariantCulture, "{0},fps={1:0.###},format=yuv420p", decoder[1], Fps(v));
            string args = "-hide_banner -loglevel error -nostdin -progress pipe:1 -y " + decoder[0] + " -i \"" + v.Path + "\" -filter_complex \"[0:v:0]" + filter + "[px]\"" +
                          OutputArgs(v, "[px]", part);
            var psi = new ProcessStartInfo(ffmpeg, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var errors = new StringBuilder();
            {
                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += (o, e) =>
                    {
                        long us;
                        if (e.Data != null && e.Data.StartsWith("out_time_ms=") && long.TryParse(e.Data.Substring(12), out us))
                            progress(Math.Max(0, Math.Min(1, us / 1e6 / v.Duration)));
                    };
                    p.ErrorDataReceived += (o, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                    p.Start();
                    try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                    cancel.Current = p;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    cancel.Current = null;
                    if (cancel.Cancelled) throw new CancelledException();
                    if (p.ExitCode != 0) throw new Exception("ffmpeg failed: " + errors.ToString().Trim());
                }
            }
        }

        public static void Finish(string part, string dest)
        {
            if (File.Exists(part) && new FileInfo(part).Length > 0) File.Move(part, dest, true);
        }
    }
}
