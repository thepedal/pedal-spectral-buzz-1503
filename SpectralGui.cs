// Pedal Spectral — spectrum analyser for Jeskola Buzz
// Copyright (C) 2026 thepedal
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version. See LICENSE.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BuzzGUI.Interfaces;   // IMachineGUIFactory, IMachineGUI, IMachineGUIHost, IMachine

namespace PedalSpectral
{
    // Separate, resizable window (v1.3). In Buzz 1503 the window width follows
    // dragging; its height follows the Height this element declares, which comes
    // from the Display Height parameter.
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = true, UseThemeStyles = false)]
    public class SpectralGuiFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new SpectralGui();
    }

    /// <summary>
    /// OnRender-painted surface, so FrameworkElement and not UserControl —
    /// a UserControl's default template paints its Border over OnRender output.
    /// </summary>
    public class SpectralGui : FrameworkElement, IMachineGUI
    {
        // Width follows the host: Buzz's parameter window clips rather than widens,
        // so the GUI takes whatever width it is arranged at (v1.0.1).
        const double DefaultW = 540, MinW = 340, MaxW = 2400;
        const double WidthBase = 340, WidthStep = 40;   // Display Width parameter: 340 + 40 * value
        const int WidthMaxIdx = 50;
        const double DefaultH = 300, MinH = 150, MaxH = 1600;
        static readonly double[] Heights = { 200, 300, 400, 500, 600 };
        const double PlotL = 44, PlotT = 8;
        double H = DefaultH;
        double PlotB => H - 22;


        double W = DefaultW;
        double PlotR => W - 10;
        const double TopDb = 6.0;

        static readonly int[] FftSizes = { 1024, 2048, 4096, 8192 };
        static readonly double[] AvgTaus = { 0.0, 0.05, 0.1, 0.25, 0.5, 1.0, 2.0 };
        static readonly double[] PeakRates = { -1.0, 0.0, 3.0, 6.0, 12.0, 24.0 };  // -1 off, 0 hold
        static readonly double[] Slopes = { 0.0, 3.0, 4.5, 6.0 };
        static readonly double[] Floors = { -60.0, -72.0, -96.0, -120.0 };
        static readonly string[] WinNames = { "Hann", "Blackman-Harris", "Flat Top" };
        static readonly string[] ChanNames = { "Mid", "Left", "Right", "Side" };
        static readonly double[] SmoothOcts = { 0.0, 1.0 / 24, 1.0 / 12, 1.0 / 6, 1.0 / 3 };
        static readonly string[] SmoothNames = { "", "1/24 oct", "1/12 oct", "1/6 oct", "1/3 oct" };
        static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        static readonly double[] GridFreqs = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };

        IMachine _iMachine;
        PedalSpectralMachine _m;

        // The analyser lives on the machine (v1.1) so its state outlives this window.
        readonly SpectrumAnalyser _fallback = new SpectrumAnalyser();
        SpectrumAnalyser _an;
        bool _pendingCapture;
        readonly float[] _frame = new float[SpectrumAnalyser.MaxN];
        readonly float[] _frameB = new float[SpectrumAnalyser.MaxN];   // Stereo: trace B
        int _lastChKey = -1;
        readonly DispatcherTimer _timer;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        double _lastTick, _lastNewData;
        int _lastWritePos;
        bool _haveWritePos;
        int _lastPeakMode = -99;
        // Peak traces record what was DISPLAYED, so any change to how the display
        // is computed invalidates them (v1.1.2). Packed: fft, window, slope, smoothing.
        int _lastDisplayKey = -1;
        Point? _mouse;

        // ── Layout regions (v1.4) ──
        // Spectrum region _sT.._sB, spectrogram region _gT.._gB. The frequency axis
        // is shared and sits below PlotB.
        bool _showSpec = true, _showGram;
        double _sT, _sB, _gT, _gB;

        // ── Spectrogram (v1.4) ──
        // Newest row at the top. One row per display frame. Kept in this GUI, so
        // closing the window clears the history; resizing or changing View resets it.
        // Circular: each frame writes ONE bitmap row at _head, and drawing shows the
        // bitmap in two slices so the newest row is on top. Cost per frame is one
        // row upload, whatever the window size.
        //
        // Since v1.4.2 the state lives on the machine (SpectrogramState), so the
        // history survives closing and reopening the window. These properties keep
        // the drawing code unchanged.
        SpectrogramState _gs = new SpectrogramState();
        WriteableBitmap _gram { get => _gs.Bmp; set => _gs.Bmp = value; }
        int[] _rowPx { get => _gs.RowPx; set => _gs.RowPx = value; }
        double[] _rowTime { get => _gs.RowTime; set => _gs.RowTime = value; }   // by bitmap row
        int _gW { get => _gs.W; set => _gs.W = value; }
        int _gH { get => _gs.H; set => _gs.H = value; }
        int _head { get => _gs.Head; set => _gs.Head = value; }

        // Row times use the machine's clock, so ages stay right across reopens.
        double Now => _m != null ? _m.Clock.Elapsed.TotalSeconds : _clock.Elapsed.TotalSeconds;
        static readonly int[] Palette = BuildPalette();

        // Frozen drawing resources
        readonly Brush _bg = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1C)));
        readonly Brush _plotBg = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1F, 0x24)));
        readonly Pen _gridPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x35)), 1));
        // Over the spectrogram the normal grid colour reads as dark lines cutting
        // through bright bands; a faint light line reads as a grid instead (v1.4.1).
        readonly Pen _gramGridPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1));
        readonly Pen _zeroPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x54)), 1));
        readonly Brush _label = Frozen(new SolidColorBrush(Color.FromRgb(0x7D, 0x85, 0x90)));
        readonly Brush _text = Frozen(new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xD9)));
        readonly Brush _fill = Frozen(new SolidColorBrush(Color.FromArgb(0x48, 0x3F, 0xB6, 0xA8)));
        readonly Pen _line = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x3F, 0xB6, 0xA8)), 1.2));
        readonly Pen _linePenB = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x9F)), 1.2));   // Stereo trace B
        readonly Brush _legendA = Frozen(new SolidColorBrush(Color.FromRgb(0x3F, 0xB6, 0xA8)));
        readonly Brush _legendB = Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x9F)));
        readonly Pen _peakPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xE0, 0xA0, 0x40)), 1));
        readonly Pen _cursorPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xC9, 0xD1, 0xD9)), 1));
        readonly Brush _frozenBadge = Frozen(new SolidColorBrush(Color.FromRgb(0x5A, 0x8D, 0xEE)));
        readonly Pen _refPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0xB8, 0xC0, 0xCC)), 1)
                                      { DashStyle = DashStyles.Dash });
        readonly Brush _labelBg = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0x16, 0x18, 0x1C)));
        readonly Brush _marker = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF0)));
        readonly Brush _refBadge = Frozen(new SolidColorBrush(Color.FromRgb(0xB8, 0xC0, 0xCC)));
        readonly Typeface _face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

        public IMachine Machine
        {
            get => _iMachine;
            set
            {
                _iMachine = value;
                _m = value?.ManagedMachine as PedalSpectralMachine;
                _an = _m != null ? _m.Analyser : _fallback;
                if (_m != null)
                {
                    if (_m.Gram == null) _m.Gram = new SpectrogramState();
                    _gs = _m.Gram;
                    // A bitmap belongs to the thread that made it; start afresh if
                    // this window lives on another one.
                    if (_gs.Bmp != null && !_gs.Bmp.CheckAccess()) _gs.Bmp = null;
                }
                else _gs = new SpectrogramState();
                // Don't wipe the machine's peak traces just because the window reopened.
                if (_m != null)
                {
                    // Buzz opens the window at the element's MinHeight (seen as exactly
                    // 200, then exactly 150, in test builds), so MinHeight carries the
                    // Display Height too. Height alone was not enough (1.3.0 to 1.4.0).
                    ApplyHeight(Heights[Clamp(_m.DisplayHeight, 0, Heights.Length - 1)]);
                    _lastPeakMode = Clamp(_m.PeakDecay, 0, PeakRates.Length - 1);
                    _lastDisplayKey = DisplayKey(_m);
                }
                _haveWritePos = false;
            }
        }

        public SpectralGui()
        {
            MinWidth = MinW;
            MinHeight = MinH;
            _an = _fallback;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            ClipToBounds = true;

            _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (s, e) => Tick();
            Loaded += (s, e) => { _lastTick = _clock.Elapsed.TotalSeconds; _timer.Start(); };
            Unloaded += (s, e) => _timer.Stop();
        }

        static double ClampW(double w) => w < MinW ? MinW : (w > MaxW ? MaxW : w);

        static double ClampH(double h) => h < MinH ? MinH : (h > MaxH ? MaxH : h);
        static bool Unbounded(double v) => double.IsInfinity(v) || double.IsNaN(v);

        // Measure asks for the preferred width (default, or the last dragged width),
        // or less if less is offered. It must NOT take a large offer: Buzz offers about
        // the whole screen and sizes the window to whatever Measure returns. Arrange
        // then fills whatever the host actually gives, so a dragged window still fills.
        protected override Size MeasureOverride(Size availableSize)
        {
            double pref = _m != null ? WidthBase + WidthStep * Clamp(_m.DisplayWidth, 0, WidthMaxIdx) : DefaultW;
            double w = Unbounded(availableSize.Width) ? pref : Math.Min(pref, Math.Max(availableSize.Width, MinW));
            // Height is set explicitly from the Display Height parameter, so WPF
            // already clamps the offer to it; take what arrives.
            double h = Unbounded(availableSize.Height) ? DefaultH : ClampH(availableSize.Height);
            return new Size(w, h);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double w = ClampW(finalSize.Width), h = ClampH(finalSize.Height);
            if (Math.Abs(w - W) > 0.5 || Math.Abs(h - H) > 0.5) { W = w; H = h; InvalidateVisual(); }
            RecordWidth(W);
            return new Size(W, H);
        }



        // ── Analysis tick (UI thread) ──────────────────────────────────────
        void Tick()
        {
            var m = _m;
            if (m == null) return;

            // Height comes from the parameter, set as an explicit Height.
            double wantH = Heights[Clamp(m.DisplayHeight, 0, Heights.Length - 1)];
            if (double.IsNaN(Height) || Math.Abs(Height - wantH) > 0.5) ApplyHeight(wantH);
            _ticked = true;   // window is up: dragged widths may now be recorded

            double now = _clock.Elapsed.TotalSeconds;
            double dt = Math.Min(0.25, Math.Max(0.001, now - _lastTick));
            _lastTick = now;

            int n = FftSizes[Clamp(m.FftSize, 0, FftSizes.Length - 1)];
            int win = Clamp(m.Window, 0, 2);
            int cols = (int)(PlotR - PlotL);
            _an.Configure(n, win, cols, m.SampleRate);
            Layout(m);

            // Channels: trace A always; trace B only in Stereo view (v1.5).
            int stereo = Clamp(m.Stereo, 0, 2);
            int chA = stereo == 0 ? Clamp(m.Channel, 0, 3) : (stereo == 1 ? 1 : 0);
            int chB = stereo == 1 ? 2 : 3;
            var anB = m.Analyser2;
            if (stereo > 0) anB.Configure(n, win, cols, m.SampleRate);

            double tau = AvgTaus[Clamp(m.Average, 0, AvgTaus.Length - 1)];
            int pkIdx = Clamp(m.PeakDecay, 0, PeakRates.Length - 1);
            double peak = PeakRates[pkIdx];
            double slope = Slopes[Clamp(m.Slope, 0, Slopes.Length - 1)];
            double smooth = SmoothOcts[Clamp(m.Smoothing, 0, SmoothOcts.Length - 1)];
            int key = DisplayKey(m);
            // Trace A changing channel also invalidates its peak record
            int chKey = stereo * 4 + chA;
            if (chKey != _lastChKey) { _an.ResetPeaks(); _lastChKey = chKey; }
            if (pkIdx != _lastPeakMode || key != _lastDisplayKey)
            {
                _an.ResetPeaks();
                _lastPeakMode = pkIdx;
                _lastDisplayKey = key;
            }

            // Reference: a rising edge on the parameter queues a capture, taken
            // from the next frame of live audio (or immediately, if frozen).
            if (m.TakeCaptureRequest()) _pendingCapture = true;
            if (!m.Reference)
            {
                _pendingCapture = false;
                if (_an.HasReference) _an.ClearReference();
            }

            if (m.Freeze)
            {
                if (_pendingCapture) { _an.CaptureReference(); _pendingCapture = false; }
                // Re-map the held spectrum so Slope, Smoothing and a new reference
                // still take effect on a frozen display.
                _an.Refresh(peak, slope, smooth);
                if (stereo > 0) anB.Refresh(-1, slope, smooth);
                InvalidateVisual();
                return;
            }

            int w = m.WritePos;
            if (!_haveWritePos) { _lastWritePos = w; _haveWritePos = true; _lastNewData = now; }

            if (w != _lastWritePos)
            {
                _lastWritePos = w;
                _lastNewData = now;
                m.CopyLatest(_frame, n, w, chA);
                _an.Process(_frame, dt, tau, peak, slope, smooth);
                if (stereo > 0)
                {
                    m.CopyLatest(_frameB, n, w, chB);
                    anB.Process(_frameB, dt, tau, -1, slope, smooth);   // no peak trace for B
                }
                if (_pendingCapture) { _an.CaptureReference(); _an.Refresh(peak, slope, smooth); _pendingCapture = false; }
            }
            else if (now - _lastNewData > 0.15)
            {
                // Ring stopped advancing: WM_NOIO, muted, or disconnected. Let traces fall.
                // The 150 ms grace keeps large host buffers from flickering the display.
                _an.ProcessSilence(dt, tau, peak, slope, smooth);
                if (stereo > 0) anB.ProcessSilence(dt, tau, -1, slope, smooth);
            }

            if (_showGram) GramTick(m);

            InvalidateVisual();
        }

        void Layout(PedalSpectralMachine m)
        {
            int view = m != null ? Clamp(m.View, 0, 2) : 0;
            _showSpec = view != 1;
            _showGram = view != 0;
            _sT = PlotT; _sB = PlotB;
            if (view == 1) { _gT = PlotT + 34; _gB = PlotB; }        // two text rows above
            else if (view == 2)
            {
                double total = PlotB - PlotT;
                _sT = PlotT;
                _sB = Math.Round(PlotT + total * 0.45);
                _gT = _sB + 4;
                _gB = PlotB;
            }
        }

        // ── Spectrogram ──
        static int[] BuildPalette()
        {
            // floor -> dark, then deep blue, teal, amber, near white at the top
            double[] pos = { 0.0, 0.25, 0.5, 0.75, 1.0 };
            int[,] rgb = { { 0x16, 0x18, 0x1C }, { 0x1E, 0x3A, 0x6E }, { 0x2E, 0x9E, 0x9A },
                           { 0xE0, 0xB0, 0x40 }, { 0xFF, 0xF6, 0xD8 } };
            var p = new int[256];
            for (int i = 0; i < 256; i++)
            {
                double t = i / 255.0;
                int k = 0;
                while (k < pos.Length - 2 && t > pos[k + 1]) k++;
                double u = (t - pos[k]) / (pos[k + 1] - pos[k]);
                int r = (int)Math.Round(rgb[k, 0] + (rgb[k + 1, 0] - rgb[k, 0]) * u);
                int g = (int)Math.Round(rgb[k, 1] + (rgb[k + 1, 1] - rgb[k, 1]) * u);
                int b = (int)Math.Round(rgb[k, 2] + (rgb[k + 1, 2] - rgb[k, 2]) * u);
                p[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
            }
            return p;
        }

        static readonly double[] RowIntervals = { 0.0, 0.1, 1.0 / 3.0 };   // Normal (every frame), Slow, Very Slow

        bool EnsureGram()
        {
            int w = _an.Main.Length;
            int h = (int)Math.Max(0, Math.Floor(_gB - _gT));
            if (w < 2 || h < 2) return false;
            if (_gram != null && w == _gW && h == _gH) return true;
            _gW = w; _gH = h; _head = 0;
            _gram = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            var fill = new int[w * h];
            for (int i = 0; i < fill.Length; i++) fill[i] = Palette[0];
            _gram.WritePixels(new Int32Rect(0, 0, w, h), fill, w * 4, 0);
            _rowPx = new int[w];
            _rowTime = new double[h];
            for (int i = 0; i < h; i++) _rowTime[i] = double.NaN;
            _gs.Levels = new float[w * h];
            for (int i = 0; i < _gs.Levels.Length; i++) _gs.Levels[i] = -200f;
            _gs.Acc = new float[w];
            for (int i = 0; i < w; i++) _gs.Acc[i] = -200f;
            _gs.LastRow = double.NegativeInfinity;
            return true;
        }

        /// <summary>
        /// Every frame: fold the current trace into the row accumulator (max per
        /// column), and write a row when the Scroll Speed interval has passed.
        /// </summary>
        void GramTick(PedalSpectralMachine m)
        {
            if (!EnsureGram()) return;
            float[] main = _an.Main, acc = _gs.Acc;
            for (int c = 0; c < _gW; c++) if (main[c] > acc[c]) acc[c] = main[c];

            double interval = RowIntervals[Clamp(m.ScrollSpeed, 0, RowIntervals.Length - 1)];
            double t = Now;
            if (interval > 0.0 && t - _gs.LastRow < interval) return;
            // Advance on a fixed grid so the rate doesn't drift, but don't try to
            // catch up after a long pause (window closed, frozen).
            _gs.LastRow = (interval <= 0.0 || t - _gs.LastRow > 3 * interval) ? t : _gs.LastRow + interval;
            AddGramRow(Floors[Clamp(m.Range, 0, Floors.Length - 1)], t);
        }

        void AddGramRow(double floor, double now)
        {
            int w = _gW, h = _gH;
            // Newest row goes one above the previous newest (wrapping), so reading
            // the bitmap from _head downwards runs newest to oldest.
            _head = (_head - 1 + h) % h;
            _rowTime[_head] = now;
            float[] acc = _gs.Acc, lv = _gs.Levels;
            double span = TopDb - floor;
            int o = _head * w;
            for (int c = 0; c < w; c++)
            {
                float v = acc[c];
                lv[o + c] = v;                  // kept for the hover readout
                acc[c] = -200f;
                double t = (v - floor) / span;
                int idx = t <= 0 ? 0 : (t >= 1 ? 255 : (int)(t * 255.0));
                _rowPx[c] = Palette[idx];
            }
            _gram.WritePixels(new Int32Rect(0, _head, w, 1), _rowPx, w * 4, 0);
        }

        /// <summary>Nearest equal-tempered note, A4 = 440 Hz, e.g. A1 +6 ct.</summary>
        static string NoteName(double f)
        {
            if (f <= 0) return "";
            double midi = 69.0 + 12.0 * Math.Log(f / 440.0, 2.0);
            int n = (int)Math.Round(midi);
            int cents = (int)Math.Round((midi - n) * 100.0);
            int oct = (int)Math.Floor(n / 12.0) - 1;
            string name = NoteNames[((n % 12) + 12) % 12] + oct.ToString(CultureInfo.InvariantCulture);
            return name + (cents >= 0 ? " +" : " ") + cents.ToString(CultureInfo.InvariantCulture) + " ct";
        }

        // Set MinHeight and Height together. A change while the window is open
        // resizes it (confirmed); MinHeight is what the window opens at.
        void ApplyHeight(double h)
        {
            MinHeight = h;
            Height = h;
        }

        // ── Display Width parameter (v1.5) ──
        // Dragging the window records the width in the Display Width parameter, so it
        // is saved with the song and used when the window next opens. Written through
        // the host's IParameter, so Buzz stores it like any slider change; deferred
        // out of the layout pass; ignored until the window has run a tick, so early
        // layout passes can't overwrite the saved width.
        bool _ticked;
        bool _widthWritePending;

        void RecordWidth(double w)
        {
            var m = _m;
            if (m == null || !_ticked || _widthWritePending) return;
            int idx = (int)Math.Round((w - WidthBase) / WidthStep);
            idx = Clamp(idx, 0, WidthMaxIdx);
            if (idx == m.DisplayWidth) return;
            _widthWritePending = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _widthWritePending = false;
                int now = Clamp((int)Math.Round((W - WidthBase) / WidthStep), 0, WidthMaxIdx);
                if (_m != null && now != _m.DisplayWidth) SetOwnParameter("Display Width", now);
            }), DispatcherPriority.Background);
        }

        void SetOwnParameter(string name, int value)
        {
            try
            {
                var groups = _iMachine?.ParameterGroups;
                if (groups != null)
                    foreach (var g in groups)
                        foreach (var p in g.Parameters)
                            if (p.Name == name) { p.SetValue(0, value); return; }
            }
            catch { }
            // Fallback: session only (not saved with the song)
            if (_m != null && name == "Display Width") _m.DisplayWidth = value;
        }

        static int DisplayKey(PedalSpectralMachine m) =>
            (m.FftSize & 0xFF) | ((m.Window & 0xFF) << 8) | ((m.Slope & 0xFF) << 16) | ((m.Smoothing & 0xFF) << 24);

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // ── Coordinate mapping ─────────────────────────────────────────────
        double FreqToX(double f)
        {
            double t = Math.Log(f / SpectrumAnalyser.FMin) / Math.Log(_an.FMax / SpectrumAnalyser.FMin);
            return PlotL + t * (PlotR - PlotL);
        }

        double DbToY(double db, double floor)
        {
            double t = (TopDb - db) / (TopDb - floor);
            if (t < 0) t = 0; else if (t > 1) t = 1;
            return _sT + t * (_sB - _sT);
        }

        // ── Rendering ──────────────────────────────────────────────────────
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(_bg, null, new Rect(0, 0, W, H));
            dc.DrawRectangle(_plotBg, null, new Rect(PlotL, PlotT, PlotR - PlotL, PlotB - PlotT));

            var m = _m;
            Layout(m);
            double floor = m != null ? Floors[Clamp(m.Range, 0, Floors.Length - 1)] : -96.0;
            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            try
            {
                if (_showGram && _gram != null && _gW == _an.Main.Length)
                {
                    // Bitmap rows _head..h-1 are newest..; rows 0.._head-1 continue below.
                    int upper = _gH - _head;
                    dc.PushClip(new RectangleGeometry(new Rect(PlotL, _gT, _gW, upper)));
                    dc.DrawImage(_gram, new Rect(PlotL, _gT - _head, _gW, _gH));
                    dc.Pop();
                    if (_head > 0)
                    {
                        dc.PushClip(new RectangleGeometry(new Rect(PlotL, _gT + upper, _gW, _head)));
                        dc.DrawImage(_gram, new Rect(PlotL, _gT + upper, _gW, _gH));
                        dc.Pop();
                    }
                }

                DrawGrid(dc, floor, ppd);
                if (_showGram) DrawTimeAxis(dc, ppd);
                if (m == null) return;

                float[] main = _an.Main, pk = _an.Peak, rf = _an.Reference;
                int cols = main.Length;
                if (cols > 1 && _showSpec)
                {
                    // Filled main trace
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(new Point(PlotL, _sB), true, true);
                        for (int c = 0; c < cols; c++)
                            ctx.LineTo(new Point(PlotL + c + 0.5, DbToY(main[c], floor)), true, false);
                        ctx.LineTo(new Point(PlotL + cols, _sB), false, false);
                    }
                    g.Freeze();
                    dc.DrawGeometry(_fill, null, g);

                    var lineG = new StreamGeometry();
                    using (var ctx = lineG.Open())
                    {
                        ctx.BeginFigure(new Point(PlotL + 0.5, DbToY(main[0], floor)), false, false);
                        for (int c = 1; c < cols; c++)
                            ctx.LineTo(new Point(PlotL + c + 0.5, DbToY(main[c], floor)), true, false);
                    }
                    lineG.Freeze();
                    dc.DrawGeometry(null, _line, lineG);

                    // Stereo: trace B as a line over trace A (v1.5)
                    int st = Clamp(m.Stereo, 0, 2);
                    float[] mb = m.Analyser2.Main;
                    if (st > 0 && mb.Length == cols)
                    {
                        var bG = new StreamGeometry();
                        using (var ctx = bG.Open())
                        {
                            ctx.BeginFigure(new Point(PlotL + 0.5, DbToY(mb[0], floor)), false, false);
                            for (int c = 1; c < cols; c++)
                                ctx.LineTo(new Point(PlotL + c + 0.5, DbToY(mb[c], floor)), true, false);
                        }
                        bG.Freeze();
                        dc.DrawGeometry(null, _linePenB, bG);
                    }

                    // Reference overlay
                    if (m.Reference && _an.HasReference && rf.Length == cols)
                    {
                        var refG = new StreamGeometry();
                        using (var ctx = refG.Open())
                        {
                            bool open = false;
                            for (int c = 0; c < cols; c++)
                            {
                                if (rf[c] <= -199f) { open = false; continue; }
                                var pt = new Point(PlotL + c + 0.5, DbToY(rf[c], floor));
                                if (!open) { ctx.BeginFigure(pt, false, false); open = true; }
                                else ctx.LineTo(pt, true, false);
                            }
                        }
                        refG.Freeze();
                        dc.DrawGeometry(null, _refPen, refG);
                    }

                    // Peak-hold trace
                    if (Clamp(m.PeakDecay, 0, PeakRates.Length - 1) != 0)
                    {
                        var pkG = new StreamGeometry();
                        using (var ctx = pkG.Open())
                        {
                            ctx.BeginFigure(new Point(PlotL + 0.5, DbToY(pk[0], floor)), false, false);
                            for (int c = 1; c < cols; c++)
                                ctx.LineTo(new Point(PlotL + c + 0.5, DbToY(pk[c], floor)), true, false);
                        }
                        pkG.Freeze();
                        dc.DrawGeometry(null, _peakPen, pkG);
                    }
                }

                // Peak label (v1.2)
                int pl = Clamp(m.PeakLabel, 0, 2);
                if (pl > 0 && _showSpec) DrawPeakLabel(dc, pl == 1 ? 250.0 : _an.FMax, floor, ppd,
                                          Slopes[Clamp(m.Slope, 0, Slopes.Length - 1)]);

                // Status line
                int n = FftSizes[Clamp(m.FftSize, 0, FftSizes.Length - 1)];
                int stv = Clamp(m.Stereo, 0, 2);
                string status = "FFT " + n + "   " + _an.BinHz.ToString("0.0", CultureInfo.InvariantCulture) +
                                " Hz bins   " + WinNames[Clamp(m.Window, 0, 2)] +
                                (stv == 0 ? "   " + ChanNames[Clamp(m.Channel, 0, 3)] : "");
                string sm = SmoothNames[Clamp(m.Smoothing, 0, SmoothNames.Length - 1)];
                if (sm.Length > 0) status += "   " + sm;
                var stt = MakeText(status, _label, 10, ppd);
                dc.DrawText(stt, new Point(PlotL + 6, PlotT + 4));
                if (stv > 0)
                {
                    // Colour legend for the two traces, right after the status text
                    double lx = PlotL + 6 + stt.Width + 12;
                    var la = MakeText(stv == 1 ? "Left" : "Mid", _legendA, 10, ppd);
                    dc.DrawText(la, new Point(lx, PlotT + 4));
                    var lb = MakeText(stv == 1 ? "Right" : "Side", _legendB, 10, ppd);
                    dc.DrawText(lb, new Point(lx + la.Width + 8, PlotT + 4));
                }
                if (_showGram && !_showSpec)
                    DrawText(dc, "Spectrogram   newest at top", PlotL + 6, PlotT + 18, _label, 10, ppd);

                // Badges sit on the second row, right-aligned, so a long status
                // line can never run into them (v1.1.1).
                double badgeX = PlotR - 6, badgeY = PlotT + 18;
                if (m.Freeze)
                {
                    var ft = MakeText("FROZEN", _frozenBadge, 10, ppd);
                    badgeX -= ft.Width;
                    dc.DrawText(ft, new Point(badgeX, badgeY));
                    badgeX -= 10;
                }
                if (m.Reference)
                {
                    string rt = _an.HasReference ? "REF" : (_pendingCapture ? "REF pending" : "REF empty");
                    var ft = MakeText(rt, _refBadge, 10, ppd);
                    badgeX -= ft.Width;
                    dc.DrawText(ft, new Point(badgeX, badgeY));
                }

                // Hover readout
                if (_mouse.HasValue && cols > 0)
                {
                    Point p = _mouse.Value;
                    if (p.X >= PlotL && p.X < PlotL + cols && p.Y >= PlotT && p.Y <= PlotB)
                    {
                        int c = (int)(p.X - PlotL);
                        dc.DrawLine(_cursorPen, new Point(PlotL + c + 0.5, PlotT), new Point(PlotL + c + 0.5, PlotB));
                        double f = _an.ColumnFreq(c);
                        string fs = f >= 1000 ? (f / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " kHz"
                                              : f.ToString("0", CultureInfo.InvariantCulture) + " Hz";
                        string line = fs + "   " + NoteName(f);
                        bool inGram = _showGram && p.Y >= _gT && p.Y < _gB;
                        if (inGram)
                        {
                            // Spectrogram: how long ago this row was drawn
                            int r = (int)(p.Y - _gT);
                            if (r >= 0 && r < _gH && _rowTime.Length == _gH)
                            {
                                int b = (_head + r) % _gH;
                                double t0 = _rowTime[b];
                                if (!double.IsNaN(t0))
                                {
                                    if (_gs.Levels.Length == _gW * _gH && c < _gW)
                                    {
                                        float lvl = _gs.Levels[b * _gW + c];
                                        line += "   " + (lvl <= -199f ? "-inf" : lvl.ToString("0.0", CultureInfo.InvariantCulture)) + " dB";
                                    }
                                    line += "   " + (Now - t0).ToString("0.0", CultureInfo.InvariantCulture) + " s ago";
                                }
                            }
                            dc.DrawLine(_cursorPen, new Point(PlotL, Math.Round(p.Y) + 0.5), new Point(PlotL + cols, Math.Round(p.Y) + 0.5));
                        }
                        else if (_showSpec)
                        {
                            string ds = main[c] <= -199f ? "-inf dB" : main[c].ToString("0.0", CultureInfo.InvariantCulture) + " dB";
                            int sth = Clamp(m.Stereo, 0, 2);
                            float[] mbh = m.Analyser2.Main;
                            if (sth > 0 && c < mbh.Length)
                            {
                                string db2 = mbh[c] <= -199f ? "-inf dB" : mbh[c].ToString("0.0", CultureInfo.InvariantCulture) + " dB";
                                line += "   " + (sth == 1 ? "L " : "M ") + ds + "   " + (sth == 1 ? "R " : "S ") + db2;
                            }
                            else line += "   " + ds;
                            if (m.Reference && _an.HasReference && c < rf.Length && rf[c] > -199f)
                                line += "   ref " + rf[c].ToString("0.0", CultureInfo.InvariantCulture) + " dB";
                        }
                        var lt = MakeText(line, _text, 11, ppd);
                        dc.DrawRectangle(_labelBg, null, new Rect(PlotL + 3, PlotT + 17, lt.Width + 6, lt.Height + 2));
                        dc.DrawText(lt, new Point(PlotL + 6, PlotT + 18));
                    }
                }
            }
            catch
            {
                // Never let a render glitch take the host GUI down.
            }
        }

        void DrawGrid(DrawingContext dc, double floor, double ppd)
        {
            // dB lines every 12 dB from 0 down (6 dB for the -60 range)
            double step = floor >= -60.0 ? 6.0 : 12.0;
            for (double db = 0; _showSpec && db >= floor; db -= step)
            {
                double y = Math.Round(DbToY(db, floor)) + 0.5;
                dc.DrawLine(db == 0 ? _zeroPen : _gridPen, new Point(PlotL, y), new Point(PlotR, y));
                string s = db == 0 ? "0" : db.ToString("0", CultureInfo.InvariantCulture);
                DrawTextRight(dc, s, PlotL - 5, y - 7, _label, 10, ppd);
            }

            foreach (double f in GridFreqs)
            {
                if (f > _an.FMax + 1) continue;
                double x = Math.Round(FreqToX(f)) + 0.5;
                if (_showGram)
                {
                    // Normal grid above the spectrogram, faint grid over it
                    if (_gT > PlotT) dc.DrawLine(_gridPen, new Point(x, PlotT), new Point(x, _gT));
                    dc.DrawLine(_gramGridPen, new Point(x, _gT), new Point(x, _gB));
                }
                else
                    dc.DrawLine(_gridPen, new Point(x, PlotT), new Point(x, PlotB));
                string s = f >= 1000 ? (f / 1000).ToString("0", CultureInfo.InvariantCulture) + "k"
                                     : f.ToString("0", CultureInfo.InvariantCulture);
                var ft = MakeText(s, _label, 10, ppd);
                double tx = Math.Min(Math.Max(x - ft.Width / 2, PlotL), W - ft.Width - 2);
                dc.DrawText(ft, new Point(tx, PlotB + 4));
            }
        }

        static readonly double[] TimeSteps = { 1, 2, 5, 10, 15, 30, 60, 120, 300 };

        /// <summary>
        /// Time markers down the left of the spectrogram, from the recorded time of
        /// each row (so they stay right if the frame rate dips, and a gap while the
        /// window was closed shows as a jump).
        /// </summary>
        void DrawTimeAxis(DrawingContext dc, double ppd)
        {
            if (_gram == null || _gW != _an.Main.Length || _rowTime.Length != _gH) return;
            int h = _gH;
            double now = Now;

            int last = -1;
            for (int r = h - 1; r >= 0; r--)
                if (!double.IsNaN(_rowTime[(_head + r) % h])) { last = r; break; }
            if (last < 0) return;
            // Scroll rate from the newest rows only (up to 60), so a gap further
            // down (window was closed) doesn't make the labels coarse. ~30 until
            // there is enough history.
            int r0 = Math.Min(last, 60);
            double span = _rowTime[_head % h] - _rowTime[(_head + r0) % h];
            double pxPerSec = (r0 > 10 && span > 0.2) ? r0 / span : 30.0;

            double step = TimeSteps[TimeSteps.Length - 1];
            foreach (double s in TimeSteps) if (s * pxPerSec >= 40.0) { step = s; break; }

            double lastY = double.NegativeInfinity;
            long prevK = -1;
            for (int r = 0; r <= last; r++)
            {
                double age = now - _rowTime[(_head + r) % h];
                long k = (long)Math.Floor(age / step);
                if (r > 0 && k == prevK) continue;
                prevK = k;
                double y = _gT + r;
                if (y - lastY < 14) continue;
                lastY = y;
                // Top row: its real age (non-zero while frozen); others: the step crossed
                string s = r == 0
                    ? (age < 0.5 ? "0" : Math.Round(age).ToString("0", CultureInfo.InvariantCulture)) + " s"
                    : (k * step).ToString("0", CultureInfo.InvariantCulture) + " s";
                dc.DrawLine(_gridPen, new Point(PlotL - 3, y + 0.5), new Point(PlotL, y + 0.5));
                DrawTextRight(dc, s, PlotL - 5, r == 0 ? y + 4 : y - 7, _label, 10, ppd);
            }
        }

        void DrawPeakLabel(DrawingContext dc, double fHi, double floor, double ppd, double slope)
        {
            if (!_an.FindPeak(SpectrumAnalyser.FMin, fHi, -200.0, out double f, out double db0)) return;
            double db = db0 + slope * Math.Log(f / 1000.0, 2.0);   // as displayed
            if (db < floor + 6.0) return;                           // nothing worth labelling

            double x = FreqToX(f), y = DbToY(db, floor);
            if (x < PlotL || x > PlotR) return;

            // Small downward triangle just above the curve
            var tri = new StreamGeometry();
            using (var ctx = tri.Open())
            {
                ctx.BeginFigure(new Point(x, y - 2), true, true);
                ctx.LineTo(new Point(x - 4, y - 9), false, false);
                ctx.LineTo(new Point(x + 4, y - 9), false, false);
            }
            tri.Freeze();
            dc.DrawGeometry(_marker, null, tri);

            string s = (f >= 1000 ? (f / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " kHz"
                                  : f.ToString("0.0", CultureInfo.InvariantCulture) + " Hz") +
                       "   " + NoteName(f);
            var ft = MakeText(s, _text, 11, ppd);
            double tx = Math.Min(Math.Max(x - ft.Width / 2, PlotL + 2), PlotR - ft.Width - 2);
            double ty = y - 12 - ft.Height;
            if (ty < _sT + 34) ty = y + 6;                         // no room above: go below
            dc.DrawRectangle(_labelBg, null, new Rect(tx - 3, ty - 1, ft.Width + 6, ft.Height + 2));
            dc.DrawText(ft, new Point(tx, ty));
        }

        FormattedText MakeText(string s, Brush b, double size, double ppd) =>
            new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, size, b, ppd);

        void DrawText(DrawingContext dc, string s, double x, double y, Brush b, double size, double ppd) =>
            dc.DrawText(MakeText(s, b, size, ppd), new Point(x, y));

        void DrawTextRight(DrawingContext dc, string s, double right, double y, Brush b, double size, double ppd)
        {
            var ft = MakeText(s, b, size, ppd);
            dc.DrawText(ft, new Point(right - ft.Width, y));
        }

        // ── Mouse ──────────────────────────────────────────────────────────
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mouse = e.GetPosition(this);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _mouse = null;
        }

        // Double-click clears peak-hold traces
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e.ClickCount == 2) _an.ResetPeaks();
        }
    }

    /// <summary>
    /// Spectrogram history, owned by the machine so it outlives the window (v1.4.2).
    /// Used only on the GUI thread.
    /// </summary>
    internal sealed class SpectrogramState
    {
        public WriteableBitmap Bmp;
        public int[] RowPx = new int[0];
        public double[] RowTime = new double[0];
        public int W, H, Head;
        public float[] Levels = new float[0];   // displayed dB per row and column (v1.5)
        public float[] Acc = new float[0];      // max since the last row (Scroll Speed)
        public double LastRow = double.NegativeInfinity;
    }
}
