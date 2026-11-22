"""Prototype: score replays for action with SigLIP 2 (zero-shot) and motion, save DINOv2 + SigLIP embeddings.

Usage: python proto.py <models dir> <out dir> <video> [<video> ...]
Writes <out>/<name>.npz (per-step features) and <out>/<name>.png (graph strip with thumbnails).
"""
import json
import os
import subprocess
import sys
import time

import numpy as np
import onnxruntime as ort
from PIL import Image, ImageDraw
from tokenizers import Tokenizer

FFMPEG = os.path.expandvars(r"%LOCALAPPDATA%\Microsoft\WinGet\Links\ffmpeg.exe")
FPS = 10          # decode rate, used for motion
STEP = 5          # every 5th frame (2 per second) goes to the models
SIZE = 224

ACTION = [
    "a first-person shooter gunfight with muzzle flash and bullet tracers",
    "an explosion with fire, smoke and flying debris in a video game",
    "aiming down sights at an enemy player in a shooter game",
    "a building being destroyed with debris everywhere in a video game",
    "intense close-range combat in a first-person shooter",
]
IDLE = [
    "walking down an empty street in a first-person video game",
    "a game menu or loadout screen",
    "a respawn or spectator screen in a video game",
    "a calm scene with no enemies in a first-person video game",
    "looking at a wall in a video game",
]


def session(path):
    return ort.InferenceSession(path, providers=["DmlExecutionProvider", "CPUExecutionProvider"])


def text_embeddings(models):
    tok = Tokenizer.from_file(os.path.join(models, "siglip2", "tokenizer.json"))
    text = session(os.path.join(models, "siglip2", "text_model_fp16.onnx"))
    names = [i.name for i in text.get_inputs()]
    ids = []
    for p in ACTION + IDLE:
        e = tok.encode(p.lower()).ids[:64]
        ids.append(e + [0] * (64 - len(e)))
    feed = {"input_ids": np.array(ids, dtype=np.int64)}
    if "attention_mask" in names:
        feed["attention_mask"] = (feed["input_ids"] != 0).astype(np.int64)
    outs = text.run(None, feed)
    out_names = [o.name for o in text.get_outputs()]
    emb = outs[out_names.index("pooler_output")] if "pooler_output" in out_names else outs[-1]
    emb = emb.astype(np.float32)
    return emb / np.linalg.norm(emb, axis=1, keepdims=True), names, out_names


def frames(video):
    cmd = [FFMPEG, "-hide_banner", "-loglevel", "error", "-i", video, "-an",
           "-vf", f"fps={FPS},scale={SIZE}:{SIZE}:flags=bilinear", "-pix_fmt", "rgb24", "-f", "rawvideo", "-"]
    p = subprocess.Popen(cmd, stdout=subprocess.PIPE, bufsize=SIZE * SIZE * 3 * 8)
    n = SIZE * SIZE * 3
    while True:
        b = p.stdout.read(n)
        if len(b) < n:
            break
        yield np.frombuffer(b, dtype=np.uint8).reshape(SIZE, SIZE, 3)
    p.wait()


def run_vision(sess, batch, mean, std, fp16):
    x = np.stack(batch).astype(np.float32) / 255.0
    x = (x - mean) / std
    x = x.transpose(0, 3, 1, 2).astype(np.float32)
    if fp16:
        x = x.astype(np.float16)
    inp = sess.get_inputs()[0].name
    outs = sess.run(None, {inp: x})
    names = [o.name for o in sess.get_outputs()]
    if "pooler_output" in names:
        e = outs[names.index("pooler_output")]
    else:
        e = outs[0][:, 0]                  # DINOv2: CLS token of last hidden state
    e = e.astype(np.float32)
    return e / np.linalg.norm(e, axis=1, keepdims=True)


def main():
    models, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    t0 = time.time()
    texts, tin, tout = text_embeddings(models)
    print("text inputs", tin, "outputs", tout, "in %.1fs" % (time.time() - t0))
    sig = session(os.path.join(models, "siglip2", "vision_model_fp16.onnx"))
    dino = session(os.path.join(models, "dinov2-small", "model.onnx"))
    print("siglip in", [(i.name, i.type, i.shape) for i in sig.get_inputs()], "out", [o.name for o in sig.get_outputs()])
    print("dino in", [(i.name, i.type, i.shape) for i in dino.get_inputs()], "out", [o.name for o in dino.get_outputs()])
    sig_fp16 = "float16" in sig.get_inputs()[0].type
    dino_fp16 = "float16" in dino.get_inputs()[0].type
    na = len(ACTION)

    for video in sys.argv[3:]:
        name = os.path.splitext(os.path.basename(video))[0]
        t0 = time.time()
        prev = None
        motion, model_frames, thumbs = [], [], []
        for i, f in enumerate(frames(video)):
            g = f.mean(axis=2)
            motion.append(0.0 if prev is None else float(np.abs(g - prev).mean()))
            prev = g
            if i % STEP == 0:
                model_frames.append(f)
                if (i // STEP) % 8 == 0:
                    thumbs.append((i // STEP, f))
        decode = time.time() - t0
        t1 = time.time()
        sig_e, dino_e = [], []
        for k in range(0, len(model_frames), 32):
            b = model_frames[k:k + 32]
            sig_e.append(run_vision(sig, b, 0.5, 0.5, sig_fp16))
            dino_e.append(run_vision(dino, b, np.array([0.485, 0.456, 0.406]), np.array([0.229, 0.224, 0.225]), dino_fp16))
        sig_e, dino_e = np.concatenate(sig_e), np.concatenate(dino_e)
        infer = time.time() - t1

        sims = sig_e @ texts.T
        zero = sims[:, :na].max(axis=1) - sims[:, na:].max(axis=1)
        mot = np.array(motion).reshape(-1)
        mot = np.array([mot[k * STEP:(k + 1) * STEP].mean() for k in range(len(model_frames))])
        np.savez(os.path.join(out, name + ".npz"), siglip=sig_e, dino=dino_e, zero=zero, motion=mot, sims=sims)
        draw(os.path.join(out, name + ".png"), zero, mot, thumbs, len(model_frames))
        print("%s: %d steps (%.0fs video), decode %.1fs, models %.1fs, zero-shot range %.3f..%.3f"
              % (name, len(model_frames), len(model_frames) / 2, decode, infer, zero.min(), zero.max()))


def norm(a):
    a = np.asarray(a, dtype=np.float32)
    lo, hi = np.percentile(a, 5), np.percentile(a, 95)
    return np.clip((a - lo) / max(1e-6, hi - lo), 0, 1)


def draw(path, zero, mot, thumbs, n):
    W, TH, GH = 1600, 90, 70
    img = Image.new("RGB", (W, TH + GH * 2 + 30), (18, 19, 24))
    d = ImageDraw.Draw(img)
    for idx, f in thumbs:
        x = int(idx / n * W)
        img.paste(Image.fromarray(f).resize((TH * 16 // 9, TH)), (x, 0))
    for row, (label, series, color) in enumerate([("siglip zero-shot", norm(zero), (104, 130, 255)), ("motion", norm(mot), (255, 170, 80))]):
        y0 = TH + 10 + row * GH
        d.text((4, y0), label, fill=(150, 155, 170))
        pts = [(int(i / n * W), y0 + GH - 8 - int(v * (GH - 16))) for i, v in enumerate(series)]
        d.line(pts, fill=color, width=2)
    for s in range(0, n // 2 + 1, 10):
        x = int(s * 2 / n * W)
        d.text((x + 2, TH + GH * 2 + 14), "%ds" % s, fill=(92, 96, 110))
    img.save(path)


if __name__ == "__main__":
    main()
