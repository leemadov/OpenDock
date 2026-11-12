# The Apple-style cut: the laptop opens, "Meet GlassDock.", the Dock pops out of the screen in glass (the hero), then
# the features, each with one short, bold line, and the end card. Big Display-weight headlines whose words rise in
# out of a soft blur; no caption boxes.
#   python video/scripts/assemble_apple.py [width height]       -> renders/opendock_ad_apple_<h>p.mp4
import os, subprocess, sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter

V = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
R = os.path.join(V, "renders" if len(sys.argv) <= 2 or int(sys.argv[2]) == 720 else "renders%s" % sys.argv[2])   # the frames at that size
W, H = (int(sys.argv[1]), int(sys.argv[2])) if len(sys.argv) > 2 else (1280, 720)
S = W / 1280.0
FPS = 30
VAR = r"C:\Windows\Fonts\SegUIVar.ttf"
def font(style, size):
    f = ImageFont.truetype(VAR, int(size * S)); f.set_variation_by_name(style); return f
INK = (29, 29, 31)

# ---------- the cut ----------
shots = [   # (folder, first, last, caption lines, caption placement)
    ("opening", 21, 150, ["Meet GlassDock."], "top"),
    ("hero3d", 1000, 1180, ["The Dock.", "Now in Liquid Glass."], "topleft"),
    ("shot4", 1, 90, ["Glass that bends light."], "top"),
    ("shot5", 1, 90, ["Your icons. Your colors."], "top"),
    ("shot6", 1, 90, ["A Finder that feels at home."], "top"),
    ("shot7", 1, 120, ["Tile. Stage. Focus."], "bottom"),
    ("shot8", 1, 90, ["Everything, one click away."], "bottom"),
    ("shot9", 241, 420, None, None),
]
frames, cues = [], []
for folder, a, b, lines, where in shots:
    start = len(frames)
    for i in range(a, b + 1):
        p = os.path.join(R, folder, "f_%04d.png" % i)
        if not os.path.exists(p): sys.exit("missing " + p)
        frames.append(p)
    if lines:
        delay = 30 if folder == "opening" else 8
        cues.append((start + delay, len(frames) - 6, lines, where))
END = len(frames) - 150      # the end card's first frame: 5 s from the end

def ease(t): t = max(0.0, min(1.0, t)); return 1 - (1 - t) ** 3

def headline(img, lines, where, f, a, b):
    """Each word rises in out of a soft blur, one after another; all of it fades out at the end."""
    out = max(0.0, min(1.0, (b - f) / 8.0))
    big = font("Bold Display", 58 if where != "topleft" else 64)
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    line_h = big.getbbox("Hg")[3] * 1.12
    total_h = line_h * len(lines)
    if where == "top": y0 = H * 0.075
    elif where == "bottom": y0 = H * 0.86 - total_h
    else: y0 = H * 0.09
    k = 0
    for li, line in enumerate(lines):
        words = line.split(" ")
        widths = [d.textlength(w_ + (" " if j < len(words) - 1 else ""), font=big) for j, w_ in enumerate(words)]
        x = (W - sum(widths)) / 2 if where != "topleft" else W * 0.07
        y = y0 + li * line_h
        for j, word in enumerate(words):
            t = ease((f - a - k * 4) / 14.0)
            k += 1
            if t <= 0: x += widths[j]; continue
            wl = Image.new("RGBA", img.size, (0, 0, 0, 0)); wd = ImageDraw.Draw(wl)
            rise = (1 - t) * 18 * S
            # a soft white glow under the ink keeps it readable on the screen shots
            wd.text((x, y + rise), word, font=big, fill=(255, 255, 255, int(150 * t * out)))
            wl = wl.filter(ImageFilter.GaussianBlur(7 * S))
            wd = ImageDraw.Draw(wl)
            wd.text((x, y + rise), word, font=big, fill=INK + (int(255 * t * out),))
            if t < 1: wl = wl.filter(ImageFilter.GaussianBlur((1 - t) * 6 * S))
            layer.alpha_composite(wl)
            x += widths[j]
    img.alpha_composite(layer)

def end_card(img, f):
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    x0 = W * 0.575
    rows = [("GlassDock.", font("Bold Display", 76), INK + (255,), H * 0.30, 0),
            ("The Mac-style desktop for Windows.", font("Semibold Display", 25), (60, 60, 64, 255), H * 0.30 + 98 * S, 10),
            ("Free and open source.", font("Regular Display", 25), (110, 110, 115, 255), H * 0.30 + 134 * S, 18),
            ("github.com/leemadov/OpenDock", font("Semibold Display", 22), (255, 55, 95, 255), H * 0.30 + 184 * S, 28)]
    for text, fnt, col, y, delay in rows:
        t = ease((f - END - delay) / 16.0)
        if t <= 0: continue
        wl = Image.new("RGBA", img.size, (0, 0, 0, 0)); d = ImageDraw.Draw(wl)
        d.text((x0, y + (1 - t) * 16 * S), text, font=fnt, fill=col[:3] + (int(col[3] * t),))
        if t < 1: wl = wl.filter(ImageFilter.GaussianBlur((1 - t) * 5 * S))
        layer.alpha_composite(wl)
    img.alpha_composite(layer)

out = os.path.join(V, "renders", "opendock_ad_apple_%dp.mp4" % H)
ff = subprocess.Popen(["ffmpeg", "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", "%dx%d" % (W, H), "-r", str(FPS), "-i", "-",
                       "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "16", "-preset", "slow", "-movflags", "+faststart", out], stdin=subprocess.PIPE)
for i, path in enumerate(frames):
    img = Image.open(path).convert("RGBA")
    if img.size != (W, H): img = img.resize((W, H), Image.LANCZOS)
    for (a, b, lines, where) in cues:
        if a <= i <= b: headline(img, lines, where, i, a, b)
    if i >= END: end_card(img, i)
    ff.stdin.write(img.convert("RGB").tobytes())
ff.stdin.close(); ff.wait()
print("wrote", out, len(frames), "frames,", round(len(frames) / FPS, 1), "s")
