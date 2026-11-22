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
   - **Drag across the band** to add a kept part. Dragging into a part next to it
     merges them into one. **Right-drag** cuts a range.
   - **Right-click a part** (or press Delete at the playhead) to remove it.
   - **Drag a part's edge** to make it start or end elsewhere.
   - **S** splits the part at the playhead, so each half can be removed on its own.
     S on a split removes it, and dragging a new part over it joins the halves.
   - **I** and **O** mark a range; **K** keeps it, **X** cuts it, Esc clears the marks.
   - **Ctrl+Z** undoes, and **Reset edits** goes back to what detection chose.
   - Everything you change by hand stays as you set it when you move the sliders or
     switch models.
   - **Scrub** by clicking or dragging the strip, or with the arrow keys (0.5 s, hold
     Shift for 5 s, switchable mid-hold). The preview shows the frame and whether
     it's kept.
   - **Ctrl+wheel** zooms the timeline in and out around the pointer (down to 5 s on
     screen), and the **wheel** scrolls it while zoomed. A thin bar under the band
     shows where you are, and the view follows the playhead.
4. **Play** (or Space) plays the result in the preview with sound, from the playhead,
   jumping over the cut parts like the export will. **Play cut parts too** plays
   everything. Scrubbing or editing while playing carries on from there. A player
   warms up wherever the playhead rests, so Play starts at once, and Pause keeps it
   ready, so resuming is instant too. Clicking elsewhere while playing moves the
   playhead at once and carries on from exactly there in about a second. Most of
   that is decoding from the keyframe before the click, so a shorter keyframe
   interval in OBS makes seeking faster.
5. **Export.** The kept parts are joined in order and saved next to the original.
   - The **target size starts at max**: the source's own quality for the kept length,
     and it follows the kept length until you move it.
   - At max, **No re-encode** copies the kept parts as they are: no quality loss and
     very fast, in the source's container. Each part starts at the keyframe before
     its cut, so it may begin a moment early, and there are no audio fades at cuts.
   - Below max (or with No re-encode off), the parts are re-encoded to fit the target
     like cap-compress, with short audio fades at each cut. **x264** and **NVENC**
     write H.264 MP4; **Original** writes the source's codec (H.264, HEVC or AV1, on
     the GPU when it can) in the source's container.
   - The output is `name_idletrim` plus the extension.

### The two models

- **SigLIP 2** (Google) understands text, so it works before any training: each frame
  is compared with action prompts like "a gunfight with muzzle flash" and idle prompts
  like "a game menu".
- **DINOv2** (Meta) captures finer visual detail but has no text side. Until it has
  learned from you, it learns from SigLIP: in each video, the frames SigLIP is surest
  about teach DINOv2 what action and idle look like, and DINOv2 then judges every
  frame by what's on screen. Camera movement on its own doesn't count as action.

### Your own prompts

**Prompts** opens the two lists, one prompt per line: action to keep, idle to cut.
Describe what's on screen, for example "an enemy player being eliminated in a
shooter" or "a kill feed notification popping up". New prompts are read by SigLIP 2's
text half (about 600 MB, downloaded the first time you add one) and apply at once to
every video, with no re-analysis. **Built-in prompts** puts the originals back.

### Learning from your edits

With **Learn from my edits** on, every export saves the final keep/cut choice with the
frames' model features. IdleTrim then trains a small classifier on top of each model
and uses it instead of the prompts once it has seen at least 20 s of both
action and idle time. The more videos you fix and export, the better it gets at
telling action from idle in your game. **Forget learning** deletes it all.

### Sessions

Closing IdleTrim saves the session: the video, playhead, zoom and all settings come
back next time. Your edits are saved per video, so any video you edited gets its
edits back when you open it again. Opening another video asks first; tick **Don't
ask again** to skip that (set `AskBeforeReplacing` back to `true` in `session.json`
to bring it back).

Analyses are cached, so opening the same video again is instant. Cache, learning,
edits and the session live in `%LOCALAPPDATA%\IdleTrim`.

## First run

IdleTrim needs:

- **ffmpeg**: found next to the app, on `PATH` or via winget, or downloaded on request
  (about 100 MB).
- **The models**: downloaded on request from Hugging Face into a `models` folder next
  to the app (SigLIP 2 vision 178 MB, DINOv2 small 85 MB). The SigLIP 2 text model
  (about 600 MB) is fetched only if you add your own prompts.
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
the real analysis on the GPU, the cache, learning, exports (x264, NVENC, original
format, stream copy), silent playback, and the window driven in-process.

## Tools

`tools\proto.py` is the Python prototype used to compare the models, and
`tools\prompts.py` regenerates `src\prompts.txt` if the prompts change.

## Licenses

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). SigLIP 2 and DINOv2 are
Apache 2.0, ONNX Runtime and NAudio are MIT.
