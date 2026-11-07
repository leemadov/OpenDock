# The final edit: every shot's frames in order (30 s at 30 fps), with the captions and the end card, encoded to MP4.
#   python video/scripts/assemble.py [width height]      (default 1280 720: the frames' size)
# Shots: 1-3 = renders/opening (240), 4 = shot4 (90), 5 = shot5 (90), 6 = shot6 (90), 7 = shot7 (120),
#        8 = shot8 (90), 9 = shot9 (180, Blender frames 241-420).
import os, subprocess, sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter

V = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
R = os.path.join(V, "renders")
W, H = (int(sys.argv[1]), int(sys.argv[2])) if len(sys.argv) > 2 else (1280, 720)
FPS = 30
S = W / 1280.0                     # sizes below are for 1280 x 720

frames = []
def add(folder, a, b):
    for i in range(a, b + 1):
        frames.append(os.path.join(R, folder, "f_%04d.png" % i))
add("opening", 1, 240); add("shot4", 1, 90); add("shot5", 1, 90); add("shot6", 1, 90)
add("shot7", 1, 120); add("shot8", 1, 90); add("shot9", 241, 420)
missing = [f for f in frames if not os.path.exists(f)]
if missing:
    sys.exit("missing frames, e.g. " + missing[0])

FONTS = r"C:\Windows\Fonts"
def font(name, size): return ImageFont.truetype(os.path.join(FONTS, name), int(size * S))
INK = (29, 29, 31, 255)            # Apple's near-black
# (start, end, text, style, where): style "studio" = dark text on the white studio, "screen" = white on a soft dark pill
CAPTIONS = [
    (96, 176, "Meet OpenDock", "studio", 0.045),
    (186, 238, "A Mac-style desktop for Windows", "screen", 0.12),
    (246, 328, "Glass that really bends light", "screen", 0.12),
    (336, 418, "Your icons, your colors", "screen", 0.12),
    (426, 508, "A Finder that feels at home", "screen", 0.08),
    (516, 628, "Tile. Stage. Focus.", "screen", 0.80),
    (636, 712, "Everything one click away", "screen", 0.84),
]
END = 760   # the end card fades in from here

def fade(f, a, b, edge=8):
    if f < a or f > b: return 0.0
    return max(0.0, min(1.0, (f - a) / edge, (b - f) / edge))

def caption(img, text, style, where, alpha, rise):
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    if style == "studio":
        f = font("seguisb.ttf", 40)
        tw = d.textlength(text, font=f)
        x, y = (W - tw) / 2, H * where + rise
        d.text((x, y), text, font=f, fill=INK[:3] + (int(255 * alpha),))
    else:
        f = font("seguisb.ttf", 30)
        tw = d.textlength(text, font=f)
        padx, pady = 26 * S, 13 * S
        asc, desc = f.getmetrics()
        bw, bh = tw + 2 * padx, asc + desc + 2 * pady
        x, y = (W - bw) / 2, H * where + rise
        d.rounded_rectangle((x, y, x + bw, y + bh), radius=bh / 2, fill=(20, 20, 24, int(150 * alpha)))
        d.text((x + padx, y + pady), text, font=f, fill=(255, 255, 255, int(255 * alpha)))
    img.alpha_composite(layer)

def end_card(img, f):
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    x0 = W * 0.585
    def line(text, fnt, color, y, start):
        a = max(0.0, min(1.0, (f - start) / 14.0))
        if a <= 0: return
        rise = (1 - a) ** 3 * 14 * S
        d.text((x0, y + rise), text, font=fnt, fill=color[:3] + (int(color[3] * a),))
    line("OpenDock", font("seguisb.ttf", 66), INK, H * 0.36, END)
    line("Free and open source", font("segoeui.ttf", 27), (110, 110, 115, 255), H * 0.36 + 88 * S, END + 10)
    line("github.com/leemadov/OpenDock", font("seguisb.ttf", 23), (255, 55, 95, 255), H * 0.36 + 128 * S, END + 20)
    img.alpha_composite(layer)

out = os.path.join(R, "opendock_ad_%dp.mp4" % H)
ff = subprocess.Popen(["ffmpeg", "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", "%dx%d" % (W, H), "-r", str(FPS), "-i", "-",
                       "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "16", "-preset", "slow", "-movflags", "+faststart", out], stdin=subprocess.PIPE)
for i, path in enumerate(frames):
    img = Image.open(path).convert("RGBA")
    if img.size != (W, H): img = img.resize((W, H), Image.LANCZOS)
    for (a, b, text, style, where) in CAPTIONS:
        al = fade(i, a, b)
        if al > 0:
            rise = (1 - min(1.0, (i - a) / 10.0)) ** 3 * 10 * S
            caption(img, text, style, where, al, rise)
    if i >= END: end_card(img, i)
    ff.stdin.write(img.convert("RGB").tobytes())
ff.stdin.close(); ff.wait()
print("wrote", out, len(frames), "frames")
