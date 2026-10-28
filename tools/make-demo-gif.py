"""Builds docs/demo.gif from the real 2x screenshots (no mock-ups): a short walk through the flow.

    python tools/make-demo-gif.py

Needs Pillow and ffmpeg on PATH. Each scene is held, then dissolves into the next; ffmpeg builds an optimised palette.
"""
import shutil
import subprocess
import tempfile
from pathlib import Path

from PIL import Image

root = Path(__file__).resolve().parent.parent
shots = root / "website-assets" / "screenshots"
out = root / "docs" / "demo.gif"
scenes = ["01-home", "03-scanning", "04-review", "06-review-preview", "08-copying", "09-countdown", "10-done"]
hold = {"01-home": 1.6, "03-scanning": 1.4, "04-review": 1.8, "06-review-preview": 2.4, "08-copying": 1.3, "09-countdown": 2.0, "10-done": 2.0}
fps, fade, width = 15, 0.35, 960

frames_dir = Path(tempfile.mkdtemp(prefix="miles_gif_"))
imgs = []
for name in scenes:
    im = Image.open(shots / f"{name}.png").convert("RGB")
    h = round(im.height * width / im.width)
    imgs.append(im.resize((width, h), Image.LANCZOS))

n = 0
def emit(im):
    global n
    im.save(frames_dir / f"f{n:04d}.png")
    n += 1

for i, im in enumerate(imgs):
    for _ in range(int(hold[scenes[i]] * fps)):
        emit(im)
    if i + 1 < len(imgs):
        steps = int(fade * fps)
        for s in range(1, steps + 1):
            emit(Image.blend(im, imgs[i + 1], s / (steps + 1)))

palette = frames_dir / "palette.png"
src = str(frames_dir / "f%04d.png")
subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(fps), "-i", src,
                "-vf", "palettegen=max_colors=160:stats_mode=full", str(palette)], check=True)
subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(fps), "-i", src, "-i", str(palette),
                "-lavfi", "paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle", "-loop", "0", str(out)], check=True)
shutil.rmtree(frames_dir, ignore_errors=True)
print(out, round(out.stat().st_size / 1e6, 2), "MB")
