using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace IdleTrim
{
    // Plays a list of parts back to back, as the export will join them. Each part gets its own ffmpeg for video
    // (raw BGR frames at preview size) and one for audio (PCM); the next part's ffmpeg starts early so the
    // jump over a cut is instant. Audio is the clock when there is audio; otherwise a stopwatch.
    class Player : IDisposable
    {
        public const int W = 640, H = 360;
        internal static float Volume = 1;   // tests play silently
        const int Rate = 48000, BytesPerSecond = Rate * 4;

        public class Frame
        {
            public double At;       // seconds into the playback
            public double Source;   // seconds in the source video
            public byte[] Bgr;
        }

        readonly string ffmpeg, path;
        readonly List<Segment> parts;
        readonly double fps;
        readonly bool audio;
        readonly Queue<Frame> frames = new Queue<Frame>();
        readonly Stack<byte[]> pool = new Stack<byte[]>();
        readonly Stopwatch watch = new Stopwatch();
        readonly List<Process> processes = new List<Process>();
        volatile bool stopped, videoDone;
        BufferedWaveProvider buffer;
        WaveOutEvent output;
        Thread videoThread, audioThread;
        long audioQueued;
        volatile bool armed;

        public readonly double Length;
        readonly Stopwatch life = Stopwatch.StartNew();
        public double FirstFrameMs = -1, AudioReadyMs = -1;   // how long the decoders took to deliver, for tuning

        // armed false: decode ahead but don't start until Arm, so Play can start instantly.
        // source: the file to decode, normally the video's proxy (same timeline, small and quick to seek); null for the original.
        public Player(string ffmpeg, VideoInfo v, List<Segment> parts, bool withAudio, bool armed = true, string source = null)
        {
            this.armed = armed;
            this.ffmpeg = ffmpeg;
            path = source ?? v.Path;
            this.parts = parts;
            fps = Math.Min(30, v.Fps);
            audio = withAudio && v.HasAudio;
            Length = Segment.Total(parts);
        }

        public void Arm() { armed = true; }

        // Stops the sound and the clock where they are; decoding stays ready, so Arm resumes at once.
        public void Pause()
        {
            armed = false;
            lock (frames)
            {
                if (output != null && output.PlaybackState == PlaybackState.Playing) output.Pause();
                watch.Stop();
            }
        }

        // Something to show and enough sound buffered to start without a stutter.
        public bool Ready
        {
            get { lock (frames) return frames.Count > 0 && (!audio || buffer.BufferedBytes >= BytesPerSecond / 20 || videoDone); }
        }

        public bool Started { get { return watch.IsRunning || (output != null && output.PlaybackState == PlaybackState.Playing); } }

        // Seconds into the playback that are on screen and in the speakers right now.
        public double Clock
        {
            get
            {
                if (output != null) return Math.Min(output.GetPosition() / (double)BytesPerSecond, audioQueued / (double)BytesPerSecond);
                return watch.Elapsed.TotalSeconds;
            }
        }

        public bool Finished
        {
            get
            {
                if (!videoDone) return false;
                lock (frames) if (frames.Count > 0) return false;
                return Clock >= Length - 0.05 || output == null;
            }
        }

        public void Start()
        {
            if (audio)
            {
                buffer = new BufferedWaveProvider(new WaveFormat(Rate, 16, 2)) { BufferDuration = TimeSpan.FromSeconds(4), ReadFully = true, DiscardOnBufferOverflow = false };
                output = new WaveOutEvent { DesiredLatency = 120 };
                output.Init(buffer);
                output.Volume = Volume;
                audioThread = new Thread(FeedAudio) { IsBackground = true, Name = "player audio" };
                audioThread.Start();
            }
            videoThread = new Thread(FeedVideo) { IsBackground = true, Name = "player video" };
            videoThread.Start();
        }

        // Called by the UI timer. Starts the clock once there is something to show, then returns the newest frame
        // that is due, or null if nothing new is due. Give frames back with Recycle.
        public Frame Next()
        {
            lock (frames)
            {
                if (!Started)
                {
                    if (!armed) return null;
                    bool ready = frames.Count > 0 && (!audio || buffer.BufferedBytes >= BytesPerSecond / 20 || videoDone);
                    if (!ready) return null;
                    if (output != null) output.Play(); else watch.Start();
                }
                double now = Clock;
                Frame due = null;
                while (frames.Count > 0 && frames.Peek().At <= now)
                {
                    if (due != null) Recycle(due);
                    due = frames.Dequeue();
                    Monitor.PulseAll(frames);
                }
                return due;
            }
        }

        public void Recycle(Frame f)
        {
            lock (pool) if (pool.Count < 16) pool.Push(f.Bgr);
        }

        // Preview video decodes on the CPU without the deblocking filter: it reaches the first frame of a seek in about half
        // the time the GPU decoder needs to set up, still runs over twice real time on 1440p60, and the loss can't be seen at
        // 640x360. A small probe opens the file faster; the container already says what the streams are.
        string Args(Segment s, bool video)
        {
            var c = CultureInfo.InvariantCulture;
            string seek = string.Format(c, "-probesize 1M -analyzeduration 0 -ss {0:0.###} -i \"{1}\" -t {2:0.###}", s.Start, path, s.Length);
            return video
                ? string.Format(c, "-hide_banner -loglevel error -nostdin -skip_loop_filter all -flags2 fast {0} -an -sn -dn -vf fps={1:0.###},scale={2}:{3} -pix_fmt bgr24 -f rawvideo pipe:1", seek, fps, W, H)
                : string.Format(c, "-hide_banner -loglevel error -nostdin {0} -vn -sn -dn -ac 2 -ar {1} -f s16le pipe:1", seek, Rate);
        }

        Process Open(Segment s, bool video)
        {
            if (stopped) return null;
            var psi = new ProcessStartInfo(ffmpeg, Args(s, video))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var p = new Process { StartInfo = psi };
            p.ErrorDataReceived += (o, e) => { };
            p.Start();
            p.BeginErrorReadLine();
            lock (processes) processes.Add(p);
            return p;
        }

        void Close(Process p)
        {
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch { }
            lock (processes) processes.Remove(p);
            p.Dispose();
        }

        static bool ReadFull(Stream s, byte[] b, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(b, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        void FeedVideo()
        {
            int size = W * H * 3;
            double offset = 0;
            Process next = parts.Count > 0 ? Open(parts[0], true) : null;
            try
            {
                for (int i = 0; i < parts.Count && !stopped; i++)
                {
                    var cur = next;
                    next = i + 1 < parts.Count ? Open(parts[i + 1], true) : null;
                    var seg = parts[i];
                    var stream = cur.StandardOutput.BaseStream;
                    for (int k = 0; !stopped; k++)
                    {
                        double t = k / fps;
                        if (t >= seg.Length) break;
                        byte[] b;
                        lock (pool) b = pool.Count > 0 ? pool.Pop() : new byte[size];
                        if (!ReadFull(stream, b, size)) break;
                        var f = new Frame { At = offset + t, Source = seg.Start + t, Bgr = b };
                        if (FirstFrameMs < 0) FirstFrameMs = life.Elapsed.TotalMilliseconds;
                        lock (frames)
                        {
                            while (frames.Count >= 8 && !stopped) Monitor.Wait(frames, 100);
                            frames.Enqueue(f);
                        }
                    }
                    Close(cur);
                    offset += seg.Length;
                }
            }
            catch (Exception) { }
            finally
            {
                Close(next);
                videoDone = true;
            }
        }

        // Feeds exactly each part's length of samples, padding with silence, so audio and video stay in step across cuts.
        void FeedAudio()
        {
            var chunk = new byte[BytesPerSecond / 10];
            Process next = parts.Count > 0 ? Open(parts[0], false) : null;
            try
            {
                for (int i = 0; i < parts.Count && !stopped; i++)
                {
                    var cur = next;
                    next = i + 1 < parts.Count ? Open(parts[i + 1], false) : null;
                    long want = (long)Math.Round(parts[i].Length * Rate) * 4, fed = 0;
                    var stream = cur.StandardOutput.BaseStream;
                    bool eof = false;
                    while (fed < want && !stopped)
                    {
                        while (buffer.BufferedBytes > BytesPerSecond && !stopped) Thread.Sleep(20);
                        int n = (int)Math.Min(chunk.Length, want - fed);
                        int got = 0;
                        if (!eof)
                        {
                            while (got < n)
                            {
                                int r = stream.Read(chunk, got, n - got);
                                if (r <= 0) { eof = true; break; }
                                got += r;
                            }
                        }
                        if (got < n) Array.Clear(chunk, got, n - got);
                        buffer.AddSamples(chunk, 0, n);
                        if (AudioReadyMs < 0 && buffer.BufferedBytes >= BytesPerSecond / 20) AudioReadyMs = life.Elapsed.TotalMilliseconds;
                        fed += n;
                        Interlocked.Add(ref audioQueued, n);
                    }
                    Close(cur);
                }
            }
            catch (Exception) { }
            finally { Close(next); }
        }

        public void Dispose()
        {
            stopped = true;
            lock (frames) Monitor.PulseAll(frames);
            lock (processes)
                foreach (var p in processes.ToArray())
                    try { if (!p.HasExited) p.Kill(); } catch { }
            if (output != null) { try { output.Stop(); output.Dispose(); } catch { } output = null; }
            watch.Stop();
        }
    }
}
