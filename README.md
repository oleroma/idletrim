# IdleTrim

A portable Windows app that cuts the idle parts out of gameplay recordings and keeps
the action, then compresses the result to fit a size cap (like Discord's 10 MB). It
grew out of [cap-compress](https://github.com/oleroma/cap-compress) and keeps all of
its size controls.

Action is found from the video alone with two local AI image models running on your
GPU: no HUD positions, no game-specific setup, nothing sent online.

## How it works

1. **Drop a video.** IdleTrim decodes it at 10 fps and sends two frames a second to
   both models. A 100 s 1440p60 replay takes about 25 s.
2. **Look at the timeline.** It shows a film strip, an action graph with the
   threshold line, and a band with the kept parts in blue. Cut parts are dimmed.
3. **Adjust.**
   - **Detection**: SigLIP 2, DINOv2 or Both.
   - **Sensitivity** moves the threshold; higher keeps more.
   - **Padding** keeps some time before each action, and 1.5x as much after.
   - **Click the band** (or press Space) to flip the part under it between kept and
     cut. Parts you flipped by hand are underlined and stay as you set them; click
     one again to hand it back to detection.
   - **Scrub** by clicking or dragging the strip, or with the arrow keys (0.5 s, hold
     Shift for 5 s, switchable mid-hold). The preview shows the frame and whether
     it's kept.
4. **Export.** The kept parts are joined in order, with short audio fades at each cut,
   and compressed to the target size like cap-compress. The output is saved next to
   the original as `name_idletrim.mp4`.

### The two models

- **SigLIP 2** (Google) understands text, so it works before any training: each frame
  is compared with prompts like "a gunfight with muzzle flash" and "a game menu".
- **DINOv2** (Meta) captures finer visual detail but has no text side. Until it has
  learned, motion between frames stands in for it.

### Learning from your edits

With **Learn from my edits** on, every export saves the final keep/cut choice with the
frames' model features. IdleTrim then trains a small classifier on top of each model
and uses it instead of the prompts and motion once it has seen at least 20 s of both
action and idle time. The more videos you fix and export, the better it gets at
telling action from idle in your game. **Forget learning** deletes it all.

Analyses are cached, so opening the same video again is instant. Cache and learning
live in `%LOCALAPPDATA%\IdleTrim`.

## First run

IdleTrim needs:

- **ffmpeg**: found next to the app, on `PATH` or via winget, or downloaded on request
  (about 100 MB).
- **The models**: downloaded on request from Hugging Face into a `models` folder next
  to the app (SigLIP 2 vision 178 MB, DINOv2 small 85 MB).
- **The .NET 8 Desktop Runtime**.

The models run on the GPU through DirectML, so any DirectX 12 GPU works; without one
they fall back to the CPU, which is much slower.

## Building

```bat
build.cmd
```

This publishes to `build\app` with the .NET 8 SDK. The whole folder is the app: copy
it anywhere.

## Tests

The tests run in the background: windows open off-screen without taking focus, and
no input is sent to the desktop.

```bat
dotnet build tests\Tests.csproj -c Release
set IDLETRIM_MODELS=<folder with siglip2\ and dinov2-small\>
build\bin\Tests\Release\net8.0-windows\Tests.exe <video> <out dir>
```

Without arguments only the detection logic is tested. With a video they also cover
the real analysis on the GPU, the cache, learning, exports with x264 and NVENC, and
the window driven in-process.

## Tools

`tools\proto.py` is the Python prototype used to compare the models, and
`tools\prompts.py` regenerates `src\prompts.txt` if the prompts change.

## Licenses

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). SigLIP 2 and DINOv2 are
Apache 2.0, ONNX Runtime is MIT.
