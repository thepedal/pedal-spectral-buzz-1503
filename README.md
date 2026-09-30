# Pedal Spectral

Real-time spectrum analyser for **Jeskola Buzz 1503 (32-bit)**. It is an inline pass-through effect: insert it anywhere in the graph (just before Master to watch the whole mix, or after any single machine) and the audio passes through untouched while the parameter window shows the spectrum.

Version 1.1.1. Licensed under the GNU General Public License v3.0 (see `LICENSE`).

## Target — read this first

Unlike every other Pedal machine, this one is built for **Jeskola Buzz, not ReBuzz**. Jeskola Buzz hosts managed machines on .NET Framework, so the machine targets `net48` and `x86`, and deploys to the Buzz install rather than to `C:\Program Files\ReBuzz`. It will not load in a 64-bit Buzz, and this dll is not meant for ReBuzz.

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

Insert it after the signal you want to see and open its parameter window. The main trace is teal and filled; the amber line is peak hold. Hover over the plot to read the frequency, the nearest note (A4 = 440 Hz, with the offset in cents) and the level. Double-click to clear the peak traces.

**Reference overlay.** Switch **Reference** on to capture the current main trace as a dashed overlay; the live trace keeps running over it, and the hover readout shows both levels. Switch it off and on again to recapture. If Freeze is on, the frozen trace is captured. The reference stores the captured spectrum, not the drawn line, so it is always drawn through the current Smoothing and Slope: both traces are processed the same way even if you change those after capturing. Capture and compare at the **same FFT size**, though: tone levels read the same at any size, but noise and dense mixes read about 3 dB lower per doubling of FFT size, because the same energy is spread over twice as many bins. It is not saved with the song: after reloading, the overlay is empty until you switch Reference on again.

The display state (reference, averages and peaks) now belongs to the machine, so it survives closing and reopening the parameter window. Analysis itself only runs while the window is open.

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

When the input goes silent (upstream muted, disconnected, or sending no audio), the traces fall away instead of freezing on the last frame.

## Design notes

- The audio thread does one channel mix and one ring-buffer store per sample, nothing else. All FFT work happens on the GUI thread at about 30 frames per second, and only while the parameter window is open.
- The ring's write position is an `int`, not a `long`, because 64-bit reads and writes are not atomic in a 32-bit process.
- Levels are normalised by the window's coherent gain, which is correct for reading tone peaks. (Pedal OSC's band energies use an energy normalisation instead; that is right for band sums, wrong for a line display.)
- No `MathF`, `Span` or other .NET Core-only APIs, so no extra dlls are needed on .NET Framework.

## Verified vs. not yet verified

Verified off-target (headless, against stub interfaces): the analyser's calibration (Flat Top reads 0.00 dBFS for a full-scale sine, −20.02 for a −20 dB sine), slope, averaging and peak decay, the ring buffer across integer wraparound, pass-through, and the WM_NOIO path.

**Not yet verified in Jeskola Buzz 1503.** The project's managed-machine notes were all written against ReBuzz source, so check these on first load:

1. The machine appears in the Effects list (if not, check the dll is in `Gear\Effects` and look for a load error).
2. The GUI is embedded at the top of the parameter window and updates live.
3. The right-click **About...** entry appears. If it doesn't, the machine still works; only the menu entry is missing.
4. Audio passes through bit-identical (A/B with the machine bypassed).
5. Muting the upstream machine makes the traces fall.

## Files

- `PedalSpectral.cs`: the machine, parameters, audio pass-through and About menu
- `SpectrumAnalyser.cs`: FFT, windows, averaging, log-frequency mapping, peak hold (pure BCL)
- `SpectralGui.cs`: the WPF display
- `PedalSpectral.NET.csproj`: `net48`, `x86`, deploy to the Buzz gear folder

## Jeskola Buzz vs ReBuzz: muted input

In ReBuzz, a muted upstream machine produces `WM_NOIO` (Core §33). Jeskola Buzz 1503 instead keeps calling `Work()` with a mode that lacks the READ flag, and the input buffer still holds the last block from before the mute. Any effect that reads `input` without checking `(mode & WM_READ)` will loop that block to its output as a buzz. This machine checks the flag (v1.0.2); any future Jeskola-target effect must do the same.

## Changelog

- **1.1.1**: The reference now stores the spectrum and is drawn through the current Smoothing and Slope, so a reference captured unsmoothed matches a smoothed live trace. Removed flat steps at the low end with smoothing on (bands narrower than one bin now interpolate continuously). REF and FROZEN badges moved to the second row so a long status line can't overlap them.

- **1.1.0**: Note name and cents in the hover readout. Reference overlay (new Reference switch). Fractional-octave smoothing (new Smoothing parameter). Both parameters are appended, so presets and songs from 1.0.x load unchanged. Display state now survives closing the parameter window.

- **1.0.2**: Fixed a buzz and a frozen display when the input is muted. Jeskola Buzz keeps calling `Work()` without the READ flag, and 1.0.1 treated the stale input buffer as audio, looping it to the output. Input is now used only when the READ flag is set.

- **1.0.1**: The display now fits the width of Buzz's parameter window. 1.0.0 was fixed at 640 px, and Buzz clipped it, hiding everything above about 1.2 kHz.
- **1.0.0**: First release. Confirmed loading and running in Jeskola Buzz 1503 (32-bit).
