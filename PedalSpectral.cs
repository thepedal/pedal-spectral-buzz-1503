// Pedal Spectral — spectrum analyser for Jeskola Buzz
// Copyright (C) 2026 thepedal
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version. See LICENSE.

using System.Collections.Generic;
using System.Threading;
using System.Windows;
using Buzz.MachineInterface;   // IBuzzMachine, IBuzzMachineHost, MachineDecl, ParameterDecl, Sample, WorkModes
using BuzzGUI.Interfaces;      // IMenuItem
using BuzzGUI.Common;          // MenuItemVM, SimpleCommand

namespace PedalSpectral
{
    [MachineDecl(Name = "Pedal Spectral", ShortName = "Spectral", Author = "thepedal")]
    public class PedalSpectralMachine : IBuzzMachine
    {
        internal const string Version = "1.1.2";

        // ── Audio-thread handoff ───────────────────────────────────────────
        // Work() writes the selected channel, normalised to ±1.0, into this ring.
        // The GUI copies the newest N samples at its own frame rate and does all
        // the FFT work on the UI thread. Instance fields only, never static, so
        // several instances in one song don't share buffers.
        //
        // _writePos is a free-running int counter. It is deliberately NOT a long:
        // this is a 32-bit process, where a long read/write is not atomic. An int
        // wraps after 2^31 samples, but since RingSize divides 2^32 the masked
        // index stays continuous across the wrap.
        internal const int RingSize = 32768;                 // 4x the largest FFT
        const int RingMask = RingSize - 1;
        readonly float[] _ring = new float[RingSize];
        int _writePos;                                       // Volatile read/write only
        volatile int _sampleRate = 44100;

        readonly IBuzzMachineHost host;

        public PedalSpectralMachine(IBuzzMachineHost host)
        {
            this.host = host;   // nothing else: host.Machine is not ready yet
        }

        // ── Parameters ─────────────────────────────────────────────────────
        // Display-only: none affect the audio, so no parameter smoothing.
        // No IsStateless (it hides a parameter from the window). No slash or angle
        // brackets in names or descriptions (they break preset XML and tooltips).
        // APPEND new parameters at the end only: presets and songs store parameters
        // by index, so inserting one would shift every later value.

        [ParameterDecl(Name = "FFT Size", DefValue = 2,
            Description = "Transform length. Larger gives finer low-end resolution but slower response",
            ValueDescriptions = new[] { "1024", "2048", "4096", "8192" })]
        public int FftSize { get; set; } = 2;

        [ParameterDecl(Name = "Window", DefValue = 1,
            Description = "Analysis window. Blackman-Harris for low leakage, Flat Top for accurate peak levels",
            ValueDescriptions = new[] { "Hann", "Blackman-Harris", "Flat Top" })]
        public int Window { get; set; } = 1;

        [ParameterDecl(Name = "Channel", DefValue = 0,
            Description = "Which signal to analyse. Mid is L plus R halved, Side is L minus R halved",
            ValueDescriptions = new[] { "Mid", "Left", "Right", "Side" })]
        public int Channel { get; set; } = 0;

        [ParameterDecl(Name = "Average", DefValue = 3,
            Description = "Averaging time of the main trace",
            ValueDescriptions = new[] { "Off", "50 ms", "100 ms", "250 ms", "500 ms", "1 sec", "2 sec" })]
        public int Average { get; set; } = 3;

        [ParameterDecl(Name = "Peak Decay", DefValue = 4,
            Description = "Peak-hold trace. Off hides it, Hold never falls",
            ValueDescriptions = new[] { "Off", "Hold", "3 dB per sec", "6 dB per sec", "12 dB per sec", "24 dB per sec" })]
        public int PeakDecay { get; set; } = 4;

        [ParameterDecl(Name = "Slope", DefValue = 0,
            Description = "Display tilt pivoting at 1 kHz. 4.5 dB per octave makes a typical mix read roughly flat",
            ValueDescriptions = new[] { "0 dB per oct", "3 dB per oct", "4.5 dB per oct", "6 dB per oct" })]
        public int Slope { get; set; } = 0;

        [ParameterDecl(Name = "Range", DefValue = 2,
            Description = "Bottom of the display. The top is fixed at plus 6 dBFS",
            ValueDescriptions = new[] { "-60 dB", "-72 dB", "-96 dB", "-120 dB" })]
        public int Range { get; set; } = 2;

        [ParameterDecl(Name = "Freeze", DefValue = false,
            Description = "Hold the display. Audio keeps passing through")]
        public bool Freeze { get; set; } = false;

        // ── New in v1.1 — appended at the end so v1.0 parameter indices stay valid ──

        [ParameterDecl(Name = "Smoothing", DefValue = 0,
            Description = "Fractional-octave smoothing. A sixth or a third of an octave shows the tonal balance of a mix",
            ValueDescriptions = new[] { "Off", "24th oct", "12th oct", "6th oct", "3rd oct" })]
        public int Smoothing { get; set; } = 0;

        bool _reference;
        volatile bool _captureRequested;
        volatile bool _started;

        [ParameterDecl(Name = "Reference", DefValue = false,
            Description = "Switching on captures the current trace as a dimmed overlay. Switch off and on again to recapture")]
        public bool Reference
        {
            get => _reference;
            set
            {
                // Rising edge requests a capture. Ignored before the first Work(),
                // so restoring a saved song with Reference on doesn't capture
                // whatever happens to be in the display at load time.
                if (value && !_reference && _started) _captureRequested = true;
                _reference = value;
            }
        }

        // ── Display state ──────────────────────────────────────────────────
        // Owned by the machine, used only on the GUI thread, so the reference,
        // averages and peaks survive closing and reopening the parameter window.
        internal readonly SpectrumAnalyser Analyser = new SpectrumAnalyser();

        /// <summary>GUI thread: returns true once per requested capture.</summary>
        internal bool TakeCaptureRequest()
        {
            if (!_captureRequested) return false;
            _captureRequested = false;
            return true;
        }

        // ── Audio ──────────────────────────────────────────────────────────
        // Transparent pass-through. The only per-sample work is one channel
        // combine and one ring store.
        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            // Input is only valid when the READ flag is set. Buzz does not send
            // WM_NOIO for a muted upstream: it keeps calling Work() with WM_WRITE
            // and an input buffer still holding the LAST block before the mute. Treating that as audio loops it to the output
            // (an audible buzz) and freezes the display on it (fixed in v1.0.2).
            //
            // Returning false tells the host the output is silent. The ring stops
            // advancing, so the GUI lets the traces fall — zero cost here.
            _started = true;
            if ((mode & WorkModes.WM_READ) == 0 || input == null) return false;

            var mi = host.MasterInfo;
            if (mi != null && mi.SamplesPerSec > 0) _sampleRate = mi.SamplesPerSec;  // read every call; it can change at runtime

            const float scale = 1f / 32768f;   // Buzz full scale is ±32768
            int ch = Channel;
            float[] ring = _ring;
            int w = Volatile.Read(ref _writePos);

            for (int i = 0; i < n; i++)
            {
                Sample s = input[i];
                float v;
                if (ch == 1)      v = s.L;
                else if (ch == 2) v = s.R;
                else if (ch == 3) v = (s.L - s.R) * 0.5f;
                else              v = (s.L + s.R) * 0.5f;
                ring[w & RingMask] = v * scale;
                w++;
            }
            Volatile.Write(ref _writePos, w);

            // WM_READ without WM_WRITE: the host wants us to see the input but
            // doesn't want output. Analyse, don't write, report silence.
            if ((mode & WorkModes.WM_WRITE) == 0) return false;

            if (!ReferenceEquals(output, input))
                for (int i = 0; i < n; i++) output[i] = input[i];
            return true;
        }

        // ── GUI-thread accessors ───────────────────────────────────────────
        internal int WritePos => Volatile.Read(ref _writePos);
        internal int SampleRate => _sampleRate;

        /// <summary>Copy the n samples ending at write position w into dst[0..n), oldest first.</summary>
        internal void CopyLatest(float[] dst, int n, int w)
        {
            float[] ring = _ring;
            int start = w - n;
            for (int i = 0; i < n; i++) dst[i] = ring[(start + i) & RingMask];
        }

        // ── Right-click menu ───────────────────────────────────────────────
        public IEnumerable<IMenuItem> Commands
        {
            get
            {
                yield return new MenuItemVM()
                {
                    Text = "About...",
                    Command = new SimpleCommand()
                    {
                        CanExecuteDelegate = p => true,
                        ExecuteDelegate = p => MessageBox.Show(
                            "Pedal Spectral   v" + Version + "\n\n" +
                            "Real-time spectrum analyser. Inline pass-through effect;\n" +
                            "the audio is not altered.\n\n" +
                            "Built for Jeskola Buzz 1503 (32-bit).\n\n" +
                            "github.com/thepedal/pedal-spectral-buzz-1503\n" +
                            "GNU General Public License v3.0",
                            "About Pedal Spectral")
                    }
                };
            }
        }
    }
}
