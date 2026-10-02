#!/usr/bin/env python3
"""Generate the Pedal Spectral preset bundle.

Writes  Pedal Spectral.NET.prs.xml  next to the project file (same base name as
the dll, which is how Buzz pairs a preset bundle with its machine). The csproj
copies it to the gear folder on build.

Run from the repo root:   python tools/make_presets.py
"""
import os
from xml.sax.saxutils import quoteattr

MACHINE = "Pedal Spectral"            # MachineDecl.Name
OUT = os.path.join(os.path.dirname(__file__), "..", "Pedal Spectral.NET.prs.xml")

# Global parameters in DECLARATION ORDER. The index is the position here.
# New parameters must be appended, never inserted (presets store by index).
PARAMS = [
    # name,            default
    ("FFT Size",        2),   # 1024, 2048, 4096, 8192
    ("Window",          1),   # Hann, Blackman-Harris, Flat Top
    ("Channel",         0),   # Mid, Left, Right, Side
    ("Average",         3),   # Off, 50 ms, 100 ms, 250 ms, 500 ms, 1 sec, 2 sec
    ("Peak Decay",      4),   # Off, Hold, 3, 6, 12, 24 dB per sec
    ("Slope",           0),   # 0, 3, 4.5, 6 dB per oct
    ("Range",           2),   # -60, -72, -96, -120 dB
    ("Freeze",          0),   # bool
    ("Smoothing",       0),   # Off, 24th, 12th, 6th, 3rd oct
    ("Reference",       0),   # bool
    ("Peak Label",      1),   # Off, Low End, Full Range
    ("Display Height",  1),   # 200, 300, 400, 500, 600 px
    ("View",            0),   # Spectrum, Spectrogram, Both          (v1.4)
    ("Scroll Speed",    0),   # Normal, Slow, Very Slow              (v1.5)
    ("Stereo",          0),   # Off, Left and Right, Mid and Side    (v1.5)
    ("Display Width",   5),   # 340 + 40 * value px (5 = 540)        (v1.5)
]

# Sparse overrides: only what differs from the defaults above.
PRESETS = [
    ("Init", {}, "All parameters at their defaults."),
    ("Mix Check",
     {"Average": 5, "Peak Decay": 3, "Slope": 2, "Smoothing": 3, "Peak Label": 0},
     "Tonal balance of a full mix: 1/6 oct smoothing, 4.5 dB per oct slope, 1 sec average."),
    ("Mix Detail",
     {"FFT Size": 3, "Average": 4, "Slope": 2, "Smoothing": 1, "Peak Label": 0},
     "Mix with finer detail: 8192 points, light 1/24 oct smoothing, 4.5 dB per oct slope."),
    ("Kick and Bass",
     {"FFT Size": 3, "Average": 2, "Range": 1, "Peak Label": 1},
     "Low end at 8192 points with a fast average and the low-end peak label."),
    ("Bass Tuning",
     {"FFT Size": 3, "Window": 0, "Average": 4, "Peak Decay": 0, "Range": 1, "Peak Label": 1},
     "Steady peak label for tuning kick and bass to key: Hann, 8192 points, 500 ms average."),
    ("Exact Levels",
     {"Window": 2, "Peak Decay": 1, "Peak Label": 2},
     "Flat Top window for accurate tone levels, peak hold, full-range peak label."),
    ("Transients",
     {"FFT Size": 0, "Average": 0, "Peak Decay": 5, "Peak Label": 0},
     "Fast response for drums and attacks: 1024 points, no averaging, fast peak decay."),
    ("Waterfall",
     {"View": 2, "Display Height": 3, "Average": 1, "Peak Decay": 0, "Smoothing": 2, "Peak Label": 0},
     "Spectrum above a scrolling spectrogram at 500 px: 50 ms average, 1/12 oct smoothing."),
    ("Song Overview",
     {"View": 1, "Display Height": 3, "Scroll Speed": 2, "Average": 3, "Smoothing": 3, "Slope": 2, "Peak Label": 0},
     "Spectrogram of about the last 2 to 3 minutes: Very Slow scroll, 1/6 oct, 4.5 dB per oct, 500 px."),
    ("Stereo Check",
     {"Stereo": 2, "Average": 5, "Peak Decay": 0, "Slope": 2, "Smoothing": 3, "Peak Label": 0},
     "Mid and Side as two traces, smoothed and sloped like Mix Check."),
    ("Side Check",
     {"Channel": 3, "Average": 5, "Peak Decay": 3, "Slope": 2, "Smoothing": 3, "Peak Label": 0},
     "Side signal only: where the stereo width is, smoothed and sloped like Mix Check."),
]

def main():
    names = [p[0] for p in PARAMS]
    lines = ['<?xml version="1.0" encoding="utf-8"?>', "<PresetDictionary>"]
    seen = set()
    for key, over, comment in PRESETS:
        assert key not in seen, key; seen.add(key)
        for n in over: assert n in names, (key, n)
        lines.append(f"  <Item Key={quoteattr(key)}>")
        lines.append(f"    <Preset Machine={quoteattr(MACHINE)}>")
        lines.append("      <Parameters>")
        for i, (name, default) in enumerate(PARAMS):
            v = over.get(name, default)
            lines.append(f'        <Parameter Name={quoteattr(name)} Group="1" Index="{i}" Track="0" Value="{v}" />')
        lines.append("      </Parameters>")
        lines.append("      <Attributes />")
        lines.append(f"      <Comment>{comment}</Comment>")
        lines.append("    </Preset>")
        lines.append("  </Item>")
    lines.append("</PresetDictionary>")
    with open(OUT, "w", encoding="utf-8-sig", newline="\r\n") as f:
        f.write("\n".join(lines) + "\n")
    print(f"wrote {len(PRESETS)} presets to {os.path.normpath(OUT)}")

if __name__ == "__main__":
    main()
