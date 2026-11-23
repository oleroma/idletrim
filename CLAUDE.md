# IdleTrim: notes for coding agents

Read [README.md](README.md) for what the app does, and [docs/HANDOFF.md](docs/HANDOFF.md)
for where work stands, what's next and the design decisions behind it.

## Stack

- C# .NET 8 WinForms (`net8.0-windows`), dark custom-drawn theme (`src/Theme.cs`), no
  designer files. Nullable and implicit usings are off.
- ffmpeg/ffprobe as external processes (`src/Engine.cs` finds them: next to the app,
  `PATH`, winget, or downloads on request).
- ONNX Runtime with DirectML 1.24.4 for the models; NAudio.WinMM 2.2.1 for audio.
- Windows only. The developer's machine: Windows 11, RTX 5060 Laptop (8 GB), ffmpeg
  from winget (Gyan build, `%LOCALAPPDATA%\Microsoft\WinGet\Links\ffmpeg.exe`).

## Build and test

```bat
build.cmd                                   rem publishes the app to build\app
dotnet build tests\Tests.csproj -c Release  rem the test exe compiles ../src/*.cs itself
set IDLETRIM_MODELS=D:\dev\idletrim\build\models
build\bin\Tests\Release\net8.0-windows\Tests.exe                       rem logic only
build\bin\Tests\Release\net8.0-windows\Tests.exe <video> <outdir>        rem everything
build\bin\Tests\Release\net8.0-windows\Tests.exe <video> <outdir> window rem window tests only
build\bin\Tests\Release\net8.0-windows\Tests.exe <video> <outdir> player rem playback only
```

- Everything built goes under `build/` (git-ignored, see `Directory.Build.props`),
  including `build/models` (downloaded models for development) and `build/proto/venv`
  (Python venv for `tools/`).
- The developer's test video is a 100 s 2560x1600 THE FINALS replay,
  `D:\obs\Replay 2026-11-22 12-14-20.mkv`. Any gameplay recording with audio works.
- The run prints one line per check and ends with `ALL PASSED` or `N FAILED`. A full
  run takes several minutes (exports included); run it in the background.
- `IDLETRIM_DATA` redirects `%LOCALAPPDATA%\IdleTrim` (cache, learning, session); the
  tests set it to `<outdir>\data`. `IDLETRIM_MODELS` redirects the models folder.
- If `build.cmd` says IdleTrim is running, ask the user to close it. Never kill it.

## Rules the user has set

- **Test only in the background.** No focus grabbing, no input injected into the
  desktop, no mouse capture: windows open off-screen (`DarkForm.Quiet`), handlers are
  called through reflection, `Timeline.UseCapture = false`, `Player.Volume = 0`, and the
  confirm delegates (`ConfirmReplace`, `ConfirmDownload`) are replaced. Keep new tests
  the same way.
- **Ask before outward actions**: pushing, deleting, anything published.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- The user often asks "question" / "answer only": then answer, don't change code.
- Measure instead of guessing (latencies, sizes, frame accuracy), and say what wasn't
  verified. Never invent URLs or numbers.
- Keep user-facing text plain and short; update README.md when behaviour changes.

## Map of the code

| File | What's in it |
|---|---|
| `src/MainForm.cs` | The window: open/index, Detect, model pickers, sliders, editing (parts, splits, undo), playback control, session, export |
| `src/Timeline.cs` | Film strip, action graph, kept-parts band, mouse editing, zoom/scroll; `Preview` control |
| `src/Player.cs` | Plays kept parts: one ffmpeg per part for video (bgr24 640x360) and audio (s16le 48 kHz), exact `-ss` seeks, prefetches the next part; warm/armed states |
| `src/Proxy.cs` | 640x360 preview copy (keyframe every 0.5 s) for fast seeking; CUDA or D3D11 decode |
| `src/Analysis.cs` | `Analysis` (per-video index + per-model features, cache format), `Models` (download, ORT sessions), `Indexer` (motion, thumbnails, proxy in one decode; no AI) |
| `src/Catalog.cs` | Model catalog (built-in SigLIP 2 / DINOv2 sizes, Add from Hugging Face), `ImageEncoder`, `Detector` (runs models at 2 fps on the proxy) |
| `src/Prompts.cs` | Prompt sets, their embeddings per model, `TextEncoder`, Gemma BPE `Tokenizer` (matches HF exactly) |
| `src/Scoring.cs` | Zero-shot scores, visual model pseudo-labelled from the prompt model, `Learner` (logistic heads per model), smoothing and segments |
| `src/Engine.cs` | ffmpeg/ffprobe discovery, probing, encoders (x264, NVENC, Original), stream copy, size-cap encoding (from cap-compress) |
| `src/Session.cs` | Session and per-video edits (`session.json`) |
| `src/PromptsDialog.cs`, `src/Theme.cs` | Prompt editor; theme controls, `Dropdown`, dialogs |
| `tests/Tests.cs` | All tests (one exe, see above) |
| `tools/` | Python prototype and the script that regenerates `src/prompts.txt` |
