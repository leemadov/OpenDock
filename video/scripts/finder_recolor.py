# Shot 6: the Finder window's frames as its folders turn pink one after another ("One color everywhere").
# The two renders (finder_window.png: tagged colours, finder_window_pink.png: one colour) differ only where the
# colour changes; each folder's changed area is found from that difference and switched in reading order, with a
# little pop. The sidebar's icons follow at the end.
#   python video/scripts/finder_recolor.py
import os
import numpy as np
from PIL import Image
from scipy import ndimage

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(os.path.dirname(HERE), "assets")
OUT = os.path.join(ASSETS, "finder_seq")
FRAMES, START, STAGGER, FLIP = 90, 40, 3.5, 8

a = np.asarray(Image.open(os.path.join(ASSETS, "finder_window.png")).convert("RGBA")).astype(np.float32)
b = np.asarray(Image.open(os.path.join(ASSETS, "finder_window_pink.png")).convert("RGBA")).astype(np.float32)
diff = np.abs(a - b).max(axis=2) > 12
diff = ndimage.binary_dilation(diff, iterations=6)
labels, n = ndimage.label(diff)
boxes = ndimage.find_objects(labels)
H, W = diff.shape
regions = []
for i, sl in enumerate(boxes):
    ys, xs = sl
    if (ys.stop - ys.start) * (xs.stop - xs.start) < 400:
        continue
    regions.append((ys.start, xs.start, ys.stop, xs.stop, i + 1))
# folders (the big ones in the content area) in reading order, then everything else (sidebar, path bar) together
sidebar_x = W * 0.25
folders = sorted([r for r in regions if r[1] > sidebar_x and (r[2] - r[0]) > 120], key=lambda r: (round(r[0] / 200), r[1]))
rest = [r for r in regions if r not in folders]
print(len(folders), "folders,", len(rest), "other areas")

def ease(t):
    t = min(1.0, max(0.0, t))
    return 4 * t ** 3 if t < 0.5 else 1 - (-2 * t + 2) ** 3 / 2

os.makedirs(OUT, exist_ok=True)
for f in range(FRAMES):
    out = a.copy()
    for k, (y0, x0, y1, x1, lab) in enumerate(folders + rest):
        start = START + STAGGER * (k if k < len(folders) else len(folders))
        p = (f - start) / FLIP
        if p <= 0:
            continue
        m = ease(p)
        mask = (labels[y0:y1, x0:x1] == lab)[..., None]
        region = a[y0:y1, x0:x1] * (1 - m) + b[y0:y1, x0:x1] * m
        if 0 < p < 1 and k < len(folders):
            # the pop: the folder dips and springs back as it changes
            s = 1 - 0.1 * np.sin(np.pi * p)
            hh, ww = y1 - y0, x1 - x0
            img = Image.fromarray(np.clip(region, 0, 255).astype(np.uint8))
            small = img.resize((max(1, int(ww * s)), max(1, int(hh * s))), Image.LANCZOS)
            canvas = Image.fromarray(np.clip(a[y0:y1, x0:x1] * (1 - m) + b[y0:y1, x0:x1] * m, 0, 255).astype(np.uint8))
            # fill the folder's spot with the window's background, then the smaller folder centred in it
            bg = out[y0 + 2, x1 - 3].copy()
            base = np.empty_like(region); base[:] = bg
            canvas = Image.fromarray(base.astype(np.uint8))
            canvas.paste(small, ((ww - small.width) // 2, (hh - small.height) // 2), small)
            region = np.asarray(canvas).astype(np.float32)
        out[y0:y1, x0:x1] = np.where(mask, region, out[y0:y1, x0:x1])
    Image.fromarray(np.clip(out, 0, 255).astype(np.uint8)).save(os.path.join(OUT, "w_%04d.png" % (f + 1)))
print("wrote", FRAMES, "frames to", OUT)
