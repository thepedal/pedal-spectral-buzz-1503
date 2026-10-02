# Pedal Spectral

Real-time spectrum analyser for **Jeskola Buzz 1503 (32-bit)**. It is an inline pass-through effect: insert it anywhere in the graph (just before Master to watch the whole mix, or after any single machine) and the audio passes through untouched while the analyser shows the spectrum in its own window.

Version 1.4.2. Licensed under the GNU General Public License v3.0 (see `LICENSE`).

## Target — read this first

Built for **Jeskola Buzz 1503, 32-bit**. Buzz hosts managed machines on .NET Framework, so the machine targets `net48` and `x86`. It will not load in a 64-bit Buzz.

## Build and install

Requirements: the .NET SDK (any recent version) or Visual Studio 2022. The .NET Framework 4.8 reference assemblies are pulled in automatically at build time via the `Microsoft.NETFramework.ReferenceAssemblies` package; nothing extra is deployed.

```
dotnet build -c Release
```

The post-build step copies `Pedal Spectral.NET.dll` to `C:\Program Files (x86)\Jeskola\Buzz\Gear\Effects\`. Two things can make that copy silently fail (the build stays green by design):

- **Buzz is running** and holds the dll open. Close Buzz and rebuild.
- **No write permission** to `Program Files (x86)`. Build from an elevated prompt, or copy the dll by hand.

Check for the `Deploying ...` line in the build output and the dll's timestamp in the gear folder. If Buzz lives elsewhere, build with `-p:BuzzDir="D:\path\to\Buzz"`.

Only the dll is produced: `.pdb` and `.deps.json` generation is disabled.

## Using it

Insert it after the signal you want to see and open its parameter window: the analyser opens in a separate window alongside the sliders. **Drag the window's edge to set the width**, and use **Display Height** to set the height (Buzz sizes the window height from the display, not from dragging). Raising Display Height grows an open window, but lowering it does not shrink it: close and reopen the window to get the smaller size. The width you drag to is kept when you reopen the window during the session. The main trace is teal and filled; the amber line is peak hold. Hover over the plot to read the frequency, the nearest note (A4 = 440 Hz, with the offset in cents) and the level. Double-click to clear the peak traces.

**Spectrogram.** Set **View** to Spectrogram or Both. Each display frame (about 30 a second) adds a row at the top, so time runs downwards and a 500 px window shows roughly the last 10 seconds. Colour runs from dark at the Range floor through blue, teal and amber to near white at +6 dB, using the same trace as the spectrum, so Smoothing, Slope and Range affect both. Time markers down the left edge show how old each part is, taken from when each row was actually drawn. Hovering over the spectrogram shows the frequency, the note and how long ago that row was drawn. Freeze stops the scrolling. The history is kept when you close and reopen the analyser window (nothing is recorded while it is closed, and the markers show the jump); it restarts when its size changes, i.e. when you drag the width, change Display Height, or switch between Spectrogram and Both.

**Peak label.** A marker sits on the strongest peak with its frequency and nearest note, e.g. `58.3 Hz   A#1 +2 ct`. The frequency is refined between bins, so it is accurate to a small fraction of a hertz on steady tones. Use Hann or Blackman-Harris for tuning: Flat Top's deliberately flat peak makes the frequency estimate drift by a few cents.

**Reference overlay.** Switch **Reference** on to capture the current main trace as a dashed overlay; the live trace keeps running over it, and the hover readout shows both levels. Switch it off and on again to recapture. If Freeze is on, the frozen trace is captured. The reference stores the captured spectrum, not the drawn line, so it is always drawn through the current Smoothing and Slope: both traces are processed the same way even if you change those after capturing. Capture and compare at the **same FFT size**, though: tone levels read the same at any size, but noise and dense mixes read about 3 dB lower per doubling of FFT size, because the same energy is spread over twice as many bins. It is not saved with the song: after reloading, the badge reads REF empty until you switch Reference off and on again.

The display state (reference, averages and peaks) now belongs to the machine, so it survives closing and reopening the analyser window. Analysis itself only runs while the window is open.

A full-scale sine reads 0 dBFS at its peak. The display runs from +6 dBFS at the top down to the Range setting.

| Parameter | Values | Notes |
|---|---|---|
| FFT Size | 1024, 2048, **4096**, 8192 | Bin width at 44.1 kHz: 43, 21.5, 10.8, 5.4 Hz |
| Window | Hann, **Blackman-Harris**, Flat Top | Flat Top reads tone levels exactly; Blackman-Harris separates close tones best |
| Channel | **Mid**, Left, Right, Side | Mid = (L+R)/2, Side = (L−R)/2 |
| Average | Off … **250 ms** … 2 sec | Power averaging of the main trace |
| Peak Decay | Off, Hold, 3/6/**12**/24 dB per sec | |
| Slope | **0**, 3, 4.5, 6 dB per oct | Display tilt around 1 kHz; 4.5 makes a typical mix read roughly flat |
| Range | −60, −72, **−96**, −120 dB | Bottom of the display |
| Freeze | off/on | Holds the display; audio still passes |
| Smoothing | **Off**, 24th, 12th, 6th, 3rd oct | Averages power across that fraction of an octave. Great for tonal balance; tones read lower (a full-scale sine is about −7 dB at 1/6 oct), so switch it off to read exact tone levels |
| Reference | off/on | Switching on captures the current trace as an overlay |
| Peak Label | Off, **Low End**, Full Range | Marks the strongest peak with frequency and nearest note. Low End searches 20–250 Hz, for kick and bass tuning |
| Display Height | 200, **300**, 400, 500, 600 px | Height of the analyser window. The width is set by dragging |
| View | **Spectrum**, Spectrogram, Both | Both stacks the spectrum above a scrolling spectrogram; use 500 or 600 px |

When the input goes silent (upstream muted, disconnected, or sending no audio), the traces fall away instead of freezing on the last frame.

## Presets

Right-click the machine to choose a preset. The bundle `Pedal Spectral.NET.prs.xml` is deployed next to the dll.

| Preset | For |
|---|---|
| Init | All defaults |
| Mix Check | Tonal balance of a full mix: 1/6 oct smoothing, 4.5 dB per oct slope, 1 sec average |
| Mix Detail | The same with more detail: 8192 points, 1/24 oct smoothing |
| Kick and Bass | Low end at 8192 points, fast average, low-end peak label |
| Bass Tuning | A steady peak label for tuning kick and bass to key: Hann, 8192, 500 ms |
| Exact Levels | Flat Top for accurate tone levels, peak hold, full-range peak label |
| Transients | Fast response for drums: 1024 points, no averaging, fast peak decay |
| Waterfall | Spectrum above a scrolling spectrogram at 500 px, 50 ms average, 1/12 oct |
| Side Check | Side signal only, smoothed and sloped like Mix Check |

Every preset sets Freeze and Reference off and Display Height to 300 px (500 for Waterfall), so choosing one also unfreezes the display and clears the reference.

To change the bank, edit `tools/make_presets.py` and run `python tools/make_presets.py`; the build deploys the result. The script lists parameters in declaration order, because presets store values by index.

## Design notes

- The audio thread does one channel mix and one ring-buffer store per sample, nothing else. All FFT work happens on the GUI thread at about 30 frames per second, and only while the analyser window is open.
- The ring's write position is an `int`, not a `long`, because 64-bit reads and writes are not atomic in a 32-bit process.
- Each frame is zero-padded to 4× the FFT size before transforming. That draws the true curve between bins (rather than straight lines at the low end), removes the up-to-1.4 dB scalloping loss between bins, and makes peak frequencies precise, all without changing levels or response time. It does not separate close tones; only a larger FFT Size does that.
- Levels are normalised by the window's coherent gain, which is correct for reading tone peaks. (An energy normalisation would be right for summing bands, but misreads tone levels on a line display.)
- No `MathF`, `Span` or other .NET Core-only APIs, so no extra dlls are needed on .NET Framework.

## Tested

Checked in Jeskola Buzz 1503 (32-bit): loads under Effects, the analyser opens in its own window (v1.3; earlier versions embedded it in the parameter window), its width follows dragging and its height follows Display Height, the About entry works, audio passes through unchanged, and muting the input silences the output and lets the traces fall away. Reference capture, smoothing, slope and peak reset were checked on live and frozen displays.

Version 1.2 in Buzz: the low end draws as a true curve, the peak label sits on a kick's body and follows a bass line note by note, and the hover readout works. Headless: tones between bins read 0.00 dB, peak frequency exact for 41.2–110 Hz tones, noise levels unchanged from 1.1.2, reference still matches the live trace exactly.

Checked headless against stub interfaces: calibration (Flat Top reads 0.00 dBFS for a full-scale sine, a −20 dB sine reads −20.1), slope, averaging and peak decay, smoothing continuity at the low end, reference re-mapping, and the ring buffer across integer wraparound.

## Files

- `PedalSpectral.cs`: the machine, parameters, audio pass-through and About menu
- `SpectrumAnalyser.cs`: FFT, windows, averaging, log-frequency mapping, peak hold (pure BCL)
- `SpectralGui.cs`: the WPF display
- `Pedal Spectral.NET.prs.xml`: the preset bundle
- `tools/make_presets.py`: generates the preset bundle
- `PedalSpectral.NET.csproj`: `net48`, `x86`, deploy to the Buzz gear folder

## Buzz behaviour worth knowing: muted input

When an upstream machine is muted, Buzz 1503 does not send `WM_NOIO`. It keeps calling `Work()` with a mode that lacks the READ flag, and the input buffer still holds the last block from before the mute. Any effect that reads `input` without checking `(mode & WM_READ)` will loop that block to its output as a buzz. This machine checks the flag (since v1.0.2), and any managed effect for Buzz should do the same.

## Changelog

- **1.4.2**: Time markers down the left of the spectrogram. Spectrogram history now survives closing and reopening the window. README notes that lowering Display Height needs a reopen.

- **1.4.1**: Frequency grid lines are drawn faintly over the spectrogram instead of as dark lines cutting through it. Fixed the analyser window opening at its minimum height instead of the Display Height (since 1.3.0): Buzz opens the window at the display's minimum height, so the minimum now follows Display Height.

- **1.4.0**: Spectrogram. New View parameter (appended): Spectrum, Spectrogram, or Both stacked with a shared frequency axis. Circular bitmap, one row upload per frame. Hover shows frequency, note and age of the row. New Waterfall preset.

- **1.3.1**: Preset bundle with eight presets (Init, Mix Check, Mix Detail, Kick and Bass, Bass Tuning, Exact Levels, Transients, Side Check), generated by `tools/make_presets.py` and deployed with the dll.

- **1.3.0**: The analyser opens in its own window instead of above the sliders. Width follows dragging (remembered for the session); height comes from the new Display Height parameter (appended), because Buzz sizes the window height from the display.

- **1.2.0**: 4× zero-padding: true curve shape at the low end instead of straight lines, no scalloping loss between bins, precise peak frequencies; levels and response time unchanged. New Peak Label parameter (appended) marks the strongest peak with frequency and note.

- **1.1.2**: Peak-hold traces are cleared when FFT Size, Window, Slope or Smoothing changes; they previously kept the old levels (most visible on a frozen display). REF badge now reads REF, REF pending or REF empty.

- **1.1.1**: The reference now stores the spectrum and is drawn through the current Smoothing and Slope, so a reference captured unsmoothed matches a smoothed live trace. Removed flat steps at the low end with smoothing on (bands narrower than one bin now interpolate continuously). REF and FROZEN badges moved to the second row so a long status line can't overlap them.

- **1.1.0**: Note name and cents in the hover readout. Reference overlay (new Reference switch). Fractional-octave smoothing (new Smoothing parameter). Both parameters are appended, so presets and songs from 1.0.x load unchanged. Display state now survives closing the parameter window.

- **1.0.2**: Fixed a buzz and a frozen display when the input is muted. Jeskola Buzz keeps calling `Work()` without the READ flag, and 1.0.1 treated the stale input buffer as audio, looping it to the output. Input is now used only when the READ flag is set.

- **1.0.1**: The display now fits the width of Buzz's parameter window. 1.0.0 was fixed at 640 px, and Buzz clipped it, hiding everything above about 1.2 kHz.
- **1.0.0**: First release. Confirmed loading and running in Jeskola Buzz 1503 (32-bit).
