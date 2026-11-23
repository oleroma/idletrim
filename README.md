# IdleTrim

A portable Windows app that cuts the idle parts out of gameplay recordings and keeps
the action, then compresses the result to fit a size cap (like Discord's 10 MB). It
grew out of [cap-compress](https://github.com/oleroma/cap-compress) and keeps all of
its size controls.

It works as a light video editor straight away: opening a video runs no AI. When you
want, **Detect action** finds the action with local AI image models from Hugging Face,
running on your GPU: any size your hardware allows, no game-specific setup, nothing
sent online.

## How it works

1. **Drop a video.** One quick decode makes the timeline's thumbnails and a small
   preview copy for playback; no AI. Everything starts kept: cut what you don't want by
   hand, or let detection do it.
2. **Look at the timeline.** It shows a film strip, an action graph with the
   threshold line, and a band with the kept parts in blue. Cut parts are dimmed.
3. **Adjust.**
   - **Prompt model** and **Visual model**: which local models detect action (see
     below); either can be None. **Detect action** runs them over the video, two
     frames a second, and keeps their results, so switching between models you've
     run is instant.
   - **Sensitivity** moves the threshold; higher keeps more.
   - **Padding** keeps some time before each action, and 1.5x as much after.
   - **Drag across the band** to add a kept part. Dragging into a part next to it
     merges them into one. **Right-drag** cuts a range.
   - **Right-click a part** (or press Delete at the playhead) to remove it.
   - **Drag a kept part** to move its nearer edge: grab it right of its middle to move
     the right edge, left of its middle for the left edge. The edge moves exactly as
     far as you drag, from wherever you grabbed it. A plain click just moves the
     playhead.
   - **Double-click a part** (or press **S** at the playhead) to split it there, so each
     half can be removed on its own.
     Doing it again on a split removes it, and dragging a new part over it joins the halves. Each
     half's edges drag like any part's; dragging into the other half moves the split.
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
   playhead at once and carries on from exactly there in about a quarter of a
   second.

   Playback uses a small **preview copy** of the video (640x360, a keyframe every
   half second, about 10–30 MB per 100 s), written during analysis from the same
   decode. Seeking in it is quick and playing it takes little CPU, whatever the
   original's bitrate or keyframe interval. Videos analysed before it existed get one
   in the background, at low priority, the first time they're opened. Exports always
   use the original.
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

### The models

- **Prompt models, SigLIP 2** (Google) understand text, so they work before any
  training: each frame is compared with action prompts like "a gunfight with muzzle
  flash" and idle prompts like "a game menu".
- **Visual models, DINOv2** (Meta) capture finer visual detail but have no text side.
  Until one has learned from you, it learns from the prompt model: in each video, the
  frames the prompt model is surest about teach it what action and idle look like, and
  it then judges every frame by what's on screen. Camera movement on its own doesn't
  count as action.

Built in, sized for what your GPU can take (download sizes, half precision):

| Model | Kind | Download | Notes |
|---|---|---|---|
| SigLIP 2 base, 224 px | prompt | 786 MB | fast, any GPU |
| SigLIP 2 large, 384 px | prompt | 1.8 GB | sharper frames |
| SigLIP 2 so400m, 384 px | prompt | 2.3 GB | best that runs well on 8 GB |
| SigLIP 2 giant, 384 px | prompt | 3.8 GB | 8 GB or more, slow |
| DINOv2 small | visual | 88 MB | fast, any GPU |
| DINOv2 base | visual | 176 MB | |
| DINOv2 large | visual | 612 MB | best that runs well on 8 GB |
| DINOv2 giant | visual | 2.3 GB | 12 GB or more |

**Add from Hugging Face...** in either list takes any repo id of an ONNX SigLIP or
DINOv2 model (for example from [onnx-community](https://huggingface.co/onnx-community)).
IdleTrim reads its input size and colour normalisation from the repo's own config, so
new and bigger models work without an update. A model downloads the first time you
Detect with it, after asking. Each model keeps its own results and learning.

### Your own prompts

**Prompts** opens the two lists, one prompt per line: action to keep, idle to cut.
Describe what's on screen, for example "an enemy player being eliminated in a
shooter" or "a kill feed notification popping up". The prompt model reads new prompts
with its text side (downloaded with it) and they apply at once to every video it has
run on, with no re-detecting. **Built-in prompts** puts the originals back.

### Find moments like these

To find every moment like one you point at, for example THE FINALS' "Eliminated"
text: press **Region** and drag over the preview where it shows on screen, then put
the playhead on a frame where it's there and press **M** (or **Mark**). Mark a few if
they look different. **Find** looks at that part of every half second of the original
video, at full size, with the visual model (or the prompt model if there's no visual
one), and learns your examples against the rest of the video. Found moments get an
orange tick on the timeline, examples an orange diamond, and the thin orange line is
how alike each moment looks. **N** jumps to the next one, Shift+N back. M next to an
example unmarks it. The region and examples are saved with the video; marking more
rescores at once, a new region or model looks again.

For now this only finds the moments; it doesn't change what's kept yet.

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

Indexes and each model's results are cached, so opening the same video again is
instant. Analyses from older versions are converted on first use. Cache, learning,
edits and the session live in `%LOCALAPPDATA%\IdleTrim`.

## First run

IdleTrim needs:

- **ffmpeg**: found next to the app, on `PATH` or via winget, or downloaded on request
  (about 100 MB).
- **Models**: only for Detect action, downloaded on request from Hugging Face into a
  `models` folder next to the app.
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
format, stream copy), silent playback, and the window driven in-process. Add
`window` or `player` after the out dir to run only the window or playback tests.

## Tools

`tools\proto.py` is the Python prototype used to compare the models, and
`tools\prompts.py` regenerates `src\prompts.txt` if the prompts change.

## Licenses

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). SigLIP 2 and DINOv2 are
Apache 2.0, ONNX Runtime and NAudio are MIT.
