# Handoff: where IdleTrim stands

Last updated 2026-11-23. Repo: `oleroma/idletrim` (public), branch `main`.

## What it is

IdleTrim trims idle gameplay out of recordings (mainly THE FINALS) and keeps the
action, then compresses to a size cap. It's a derivative of
[cap-compress](https://github.com/oleroma/cap-compress) and keeps its size controls.

## Done and tested

- **Light editor first.** Opening a video runs no AI: one decode (10 fps, 320x180)
  makes motion, thumbnails and the 640x360 preview copy. 100 s replay: 13.8 s.
- **Detect action** runs the chosen local models at 2 fps over the preview copy
  (3.5 s for base SigLIP 2 + small DINOv2 on the 100 s replay). Results are cached per
  model per video.
- **Model catalog**: SigLIP 2 base/large/so400m/giant (prompt models), DINOv2
  small/base/large/giant (visual models), plus **Add from Hugging Face** for any ONNX
  SigLIP/DINOv2 repo (reads size, mean and std from the repo's config). Downloads ask
  first. Old caches (`IdleTrim1`), old learning (`IdleTrimLearn1`) and old model paths
  are migrated.
- **Own prompts** (stored as text, embedded per prompt model) and **learning from
  edits** (logistic heads per model, from exports).
- **Editing**: drag on the band adds a kept part (merging into neighbours), right-drag
  cuts, right-click removes, drag a part to move its nearer edge by exactly the drag,
  double-click or S splits, I/O/K/X ranges, Ctrl+Z, Reset edits. Flat band.
- **Timeline**: Ctrl+wheel zoom, wheel scroll, playhead follows, scrubbing never lags
  (tested: longest stall 17 ms), dragging to the very start works.
- **Playback** with audio, skipping cuts; instant play/pause (warm player); clicking
  while playing jumps at once (~0.25 s to moving again thanks to the proxy).
- **Sessions** saved on close; asks before replacing a video ("Don't ask again").
- **Export**: x264, NVENC, Original codec/container, and stream copy at max size;
  target size starts at max.
- **Icon**: rotated 90° and orange compared with cap-compress.

## In progress: universal "find moments like these" detector

Stage 1 (branch `moments-detector`, PR #1): Region drawn on the preview, examples marked
with M, Find embeds full-res crops of the region (decoded from the original, CUDA →
D3D11 → CPU) with the visual model, and `MomentFinder` trains a head on the examples
against steps clearly unlike them. Found moments show on the timeline; N jumps. Code in
`src/Moments.cs`. Next is step 3 below (walk back to the fight start, keep it). The
original plan, agreed with the user:

1. The user marks a few example moments in a video, for example THE FINALS'
   **"Eliminated"** text, and can draw the **screen region** to look in.
2. The detector must be **universal**, not exact template matching. That card is text
   with no background over the live game, so pixel templates and OpenCV matching won't
   work well. Plan: embed full-resolution **crops of that region** (decoded from the
   original, not the 640x360 proxy) with the visual or prompt model, and score every
   frame's crop by similarity to the examples. Hard negatives come from the unmarked
   frames. A small head is trained as in `Scoring.cs`.
3. From each detected event, **walk backwards** with the models to find where the
   fight began (the score falls back to idle), and keep from there to just after the
   event. Learn from the user's adjustments of those fight starts.
4. HUD information is read **only from the captured video file**, never from the game.

## Ruled out or parked

- **Cloud APIs**: parked by the user ("lets ditch api idea for now"). If they come
  back, only whole-video APIs such as Gemini.
- **Faster seeking via concat/movie-filter tricks**: the concat demuxer started about
  2 s early and the movie filter gave no speedup; exact `-ss` per part on the proxy is
  what's used.
- **Motion as the action signal**: useless for this game (the camera moves all the
  time). DINOv2 doesn't use motion; it learns from the prompt model's surest frames.

## Ideas offered, not done

- Measure each built-in model's real GPU memory on the user's card and show it in the
  model dropdowns, next to the download size.

## Gotchas

- ffmpeg.exe is 228 MB, so each start costs 0.2–0.6 s. The player hides this by
  prefetching and keeping a warm, unarmed player at the playhead.
- Proxy decode: `-hwaccel cuda -hwaccel_output_format cuda` + `scale_cuda`, falling back
  to d3d11va, then the CPU (`Proxy.PickDecoder`).
- Large ONNX models have external `_data` weight files; `Catalog` downloads them
  alongside.
- `src/prompts.txt` holds the built-in prompt embeddings for SigLIP 2 base only; other
  prompt models embed the prompts with their own text model on first use.
- Mouse capture in tests used to grab the user's real mouse: keep
  `Timeline.UseCapture = false` in any test that drives the timeline.
