# The laptop's keyboard: a Windows laptop layout (key positions in key units) and a texture with every key's
# legend, drawn so it lines up with the keys Blender builds from the same layout.
#   python video/scripts/keyboard.py      -> video/assets/keyboard.json, video/assets/keyboard_legends.png
import json, os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(os.path.dirname(HERE), "assets")

rows = [
    (0.62, [("esc", 1.5)] + [("F%d" % i, 1) for i in range(1, 13)] + [("\u23FB", 1)]),
    (1, [(c, 1) for c in "`1234567890-="] + [("backspace", 1.5)]),
    (1, [("tab", 1.5)] + [(c, 1) for c in "QWERTYUIOP[]\\"]),
    (1, [("caps", 1.75)] + [(c, 1) for c in "ASDFGHJKL;'"] + [("enter", 1.75)]),
    (1, [("shift", 2.25)] + [(c, 1) for c in "ZXCVBNM,./"] + [("shift", 2.25)]),
]
keys, y = [], 0.0
for h, row in rows:
    x = 0.0
    for label, w in row:
        keys.append({"x": x, "y": y, "w": w, "h": h, "label": label})
        x += w
    y += h
# bottom row: modifiers, space, and the inverted-T arrows (up and down share one key's space)
x = 0.0
for label, w in [("fn", 1), ("ctrl", 1), ("\u229E", 1), ("alt", 1), ("", 5.5), ("alt", 1), ("ctrl", 1), ("\u2190", 1)]:
    keys.append({"x": x, "y": y, "w": w, "h": 1, "label": label}); x += w
keys.append({"x": x, "y": y, "w": 1, "h": 0.5, "label": "\u2191"})
keys.append({"x": x, "y": y + 0.5, "w": 1, "h": 0.5, "label": "\u2193"}); x += 1
keys.append({"x": x, "y": y, "w": 1, "h": 1, "label": "\u2192"})
width, height = 14.5, y + 1

PX = 300   # texture pixels per key unit
img = Image.new("RGBA", (int(width * PX), int(height * PX)), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
fonts = r"C:\Windows\Fonts"
big = ImageFont.truetype(os.path.join(fonts, "segoeui.ttf"), int(PX * 0.34))
small = ImageFont.truetype(os.path.join(fonts, "segoeui.ttf"), int(PX * 0.2))
sym = ImageFont.truetype(os.path.join(fonts, "seguisym.ttf"), int(PX * 0.3))
ink = (236, 236, 240, 255)
for k in keys:
    t = k["label"]
    if not t:
        continue
    cx, cy = (k["x"] + k["w"] / 2) * PX, (k["y"] + k["h"] / 2) * PX
    if len(t) == 1 and ord(t) > 0x2000:
        f = sym
    elif len(t) == 1:
        f = big
    else:
        f = small
    if len(t) > 1 and k["w"] > 1.2 and t not in ("esc",):
        # wide modifiers: the word sits low on the left, as on real keycaps
        x0, y0 = k["x"] * PX + PX * 0.16, (k["y"] + k["h"]) * PX - PX * 0.2
        d.text((x0, y0), t, font=f, fill=ink, anchor="ls")
    else:
        d.text((cx, cy), t, font=f, fill=ink, anchor="mm")
os.makedirs(ASSETS, exist_ok=True)
img.save(os.path.join(ASSETS, "keyboard_legends.png"))
json.dump({"width": width, "height": height, "keys": keys}, open(os.path.join(ASSETS, "keyboard.json"), "w"), indent=1)
print(len(keys), "keys,", img.size)
