# An original score for the OpenDock ad, made from scratch in code (no samples, no loops from anywhere):
# a soft electric-piano intro over the laptop, a swell and a deep hit as the Dock pops out, a gentle pulse with a
# bright piano figure through the features (each cut gets a soft accent), and a final chord over the end card.
#   python -I video/scripts/compose.py [out.wav]
import sys, os
import numpy as np

SR = 44100
CUTS = [0.0, 4.33, 10.37, 13.37, 16.37, 19.37, 23.37, 26.37]
END = 32.37
N = int(SR * (END + 0.3))
L = np.zeros(N); R = np.zeros(N)
rng = np.random.default_rng(7)

def hz(n): return 440.0 * 2 ** ((n - 69) / 12)        # MIDI note -> Hz
def put(sig, at, pan=0.0, gain=1.0):
    i = int(at * SR); j = min(N, i + len(sig))
    if j <= i: return
    s = sig[: j - i] * gain
    L[i:j] += s * np.sqrt(0.5 * (1 - pan)); R[i:j] += s * np.sqrt(0.5 * (1 + pan))

def epiano(note, dur, vel=1.0):
    """A soft electric piano: a few sine partials with a bell-like overtone, fast attack, long decay."""
    t = np.arange(int(SR * (dur + 1.6))) / SR; f = hz(note)
    env = np.minimum(1, t / 0.006) * np.exp(-t * (1.6 + f / 2200))
    tone = (np.sin(2 * np.pi * f * t) + 0.35 * np.sin(2 * np.pi * 2 * f * t) * np.exp(-t * 3)
            + 0.12 * np.sin(2 * np.pi * 3.01 * f * t) * np.exp(-t * 6) + 0.18 * np.sin(2 * np.pi * 7.02 * f * t) * np.exp(-t * 14))
    rel = np.ones_like(t); k = int(SR * dur); rel[k:] = np.exp(-(t[k:] - dur) * 5)
    return tone * env * rel * vel * 0.22

def pad(notes, dur, attack=1.2):
    """A warm pad: detuned saw-ish tones, softened, slow in and out."""
    t = np.arange(int(SR * dur)) / SR
    env = np.minimum(1, t / attack) * np.minimum(1, (dur - t) / 1.0).clip(0)
    sig = np.zeros_like(t)
    for n in notes:
        for d in (-0.07, 0.0, 0.07):
            f = hz(n) * 2 ** (d / 12)
            sig += sum(np.sin(2 * np.pi * f * k * t + rng.random() * 6) / k ** 1.6 for k in range(1, 6))
    return sig * env * 0.028 / len(notes)

def kick(vel=1.0):
    t = np.arange(int(SR * 0.5)) / SR
    f = 48 + 90 * np.exp(-t * 32)
    return np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t * 7) * 0.55 * vel

def hat(vel=1.0):
    t = np.arange(int(SR * 0.08)) / SR
    n = rng.standard_normal(len(t)); n = np.diff(n, prepend=0)          # bright: high-passed noise
    return n * np.exp(-t * 60) * 0.05 * vel

def boom():
    """The hit when the Dock pops out: a deep sub drop with a soft noise burst."""
    t = np.arange(int(SR * 2.2)) / SR
    f = 34 + 70 * np.exp(-t * 6)
    sub = np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t * 2.2)
    air = rng.standard_normal(len(t)) * np.exp(-t * 5) * 0.12
    return (sub * 0.75 + air) * 0.8

def riser(dur):
    t = np.arange(int(SR * dur)) / SR
    n = rng.standard_normal(len(t)); n = np.convolve(n, np.ones(6) / 6, "same")
    return n * (t / dur) ** 2.5 * 0.16

def shimmer():
    """A soft accent on a cut: a high bell with a little air."""
    return epiano(96, 0.2, 0.55) + epiano(103, 0.2, 0.35)

# ---------- the music ----------
# harmony: Fmaj9 - Am7 - Dm9 - Bbmaj7(#11) (bright, open, a little wistful), one chord per bar at 108 BPM
BPM = 108; beat = 60 / BPM; bar = 4 * beat
chords = [[53, 57, 60, 64, 67], [57, 60, 64, 67], [50, 57, 60, 64, 65], [46, 57, 62, 65, 64]]

# 1. intro (0 - 4.33): pad and slow piano notes over the laptop opening
put(pad([53, 60, 64, 67], 4.9, attack=1.8), 0.0, gain=1.0)
for i, n in enumerate([72, 76, 79, 77]):
    put(epiano(n, 0.9, 0.7), 0.35 + i * 0.95, pan=-0.2 + 0.13 * i)
put(riser(1.9), 4.33 - 1.9, gain=1.0)

# 2. the Dock pops out (4.33): the hit, then the groove starts
put(boom(), 4.33)
t0 = 4.33
bars = int((26.37 - t0) / bar) + 1
for b in range(bars):
    at = t0 + b * bar
    ch = chords[b % 4]
    put(pad(ch[1:], bar + 0.6, attack=0.4), at, gain=0.9)
    # bass on the root, on 1 and the "and" of 3
    for off, ln in ((0, 1.6), (2.5 * beat, 1.2)):
        put(epiano(ch[0] - 12, ln * beat, 0.9), at + off, gain=1.3)
    # the bright piano figure: eighth notes climbing the chord
    figure = [ch[1] + 12, ch[2] + 12, ch[3] + 12, ch[2] + 24, ch[3] + 12, ch[2] + 12, ch[1] + 12, ch[2] + 12]
    for k, n in enumerate(figure):
        put(epiano(n, beat * 0.45, 0.55 if k % 2 else 0.75), at + k * beat / 2, pan=0.35 if k % 2 else -0.25)
    # a gentle pulse: soft kick on every beat, a hat on the off-beats (after the first two bars)
    if b >= 1:
        for k in range(4):
            put(kick(0.6), at + k * beat)
            put(hat(0.8), at + k * beat + beat / 2, pan=0.3)

# 3. a soft accent on each feature cut
for c in CUTS[2:7]:
    put(shimmer(), c, pan=0.15)

# 4. the end card (26.37): the groove stops, one big open chord that rings out to the end
put(riser(1.2), 26.37 - 1.2, gain=0.7)
put(boom() * 0.5, 26.37)
for n, v in zip([41, 53, 60, 64, 67, 72, 76], [1.2, 0.9, 0.8, 0.8, 0.7, 0.6, 0.55]):
    put(epiano(n, 4.5, v), 26.37, pan=(n - 60) / 40)
put(pad([53, 60, 64, 67, 72], 5.9, attack=0.8), 26.37, gain=1.1)

# ---------- mix: a little room (a short, diffuse reverb), then level and a fade at the very end ----------
def reverb(x):
    ir_t = np.arange(int(SR * 1.8)) / SR
    ir = rng.standard_normal(len(ir_t)) * np.exp(-ir_t * 3.2); ir[0] = 0
    ir /= np.sqrt((ir ** 2).sum())
    n = len(x) + len(ir)
    return np.fft.irfft(np.fft.rfft(x, n) * np.fft.rfft(ir, n), n)[: len(x)]
L2 = L + 0.22 * reverb(L); R2 = R + 0.22 * reverb(R)
mix = np.stack([L2, R2], 1)
fade = np.ones(N); fl = int(SR * 0.6); fade[-fl:] = np.linspace(1, 0, fl); mix *= fade[:, None]
mix /= np.abs(mix).max() / 0.89
mix = np.tanh(mix * 1.1) / np.tanh(1.1)               # gentle glue
out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "assets", "score.wav")
import wave
with wave.open(out, "wb") as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR)
    w.writeframes((mix * 32767).astype(np.int16).tobytes())
print("wrote", out, round(N / SR, 2), "s")
