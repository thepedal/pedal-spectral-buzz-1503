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
using System.Windows.Threading;
using BuzzGUI.Interfaces;   // IMachineGUIFactory, IMachineGUI, IMachineGUIHost, IMachine

namespace PedalSpectral
{
    [MachineGUIFactoryDecl(PreferWindowedGUI = false, IsGUIResizable = false, UseThemeStyles = false)]
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
        const double DefaultW = 540, MinW = 360, MaxW = 1600, H = 300;
        const double PlotL = 44, PlotT = 8, PlotB = H - 22;
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

        // Frozen drawing resources
        readonly Brush _bg = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1C)));
        readonly Brush _plotBg = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1F, 0x24)));
        readonly Pen _gridPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x35)), 1));
        readonly Pen _zeroPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x44, 0x4A, 0x54)), 1));
        readonly Brush _label = Frozen(new SolidColorBrush(Color.FromRgb(0x7D, 0x85, 0x90)));
        readonly Brush _text = Frozen(new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xD9)));
        readonly Brush _fill = Frozen(new SolidColorBrush(Color.FromArgb(0x48, 0x3F, 0xB6, 0xA8)));
        readonly Pen _line = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x3F, 0xB6, 0xA8)), 1.2));
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
                // Don't wipe the machine's peak traces just because the window reopened.
                if (_m != null)
                {
                    _lastPeakMode = Clamp(_m.PeakDecay, 0, PeakRates.Length - 1);
                    _lastDisplayKey = DisplayKey(_m);
                }
                _haveWritePos = false;
            }
        }

        public SpectralGui()
        {
            Height = H;
            MinWidth = MinW;
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

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
                ? DefaultW : ClampW(availableSize.Width);
            return new Size(w, H);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double w = ClampW(finalSize.Width);
            if (Math.Abs(w - W) > 0.5) { W = w; InvalidateVisual(); }
            return new Size(W, H);
        }

        // ── Analysis tick (UI thread) ──────────────────────────────────────
        void Tick()
        {
            var m = _m;
            if (m == null) return;

            double now = _clock.Elapsed.TotalSeconds;
            double dt = Math.Min(0.25, Math.Max(0.001, now - _lastTick));
            _lastTick = now;

            int n = FftSizes[Clamp(m.FftSize, 0, FftSizes.Length - 1)];
            int win = Clamp(m.Window, 0, 2);
            int cols = (int)(PlotR - PlotL);
            _an.Configure(n, win, cols, m.SampleRate);

            double tau = AvgTaus[Clamp(m.Average, 0, AvgTaus.Length - 1)];
            int pkIdx = Clamp(m.PeakDecay, 0, PeakRates.Length - 1);
            double peak = PeakRates[pkIdx];
            double slope = Slopes[Clamp(m.Slope, 0, Slopes.Length - 1)];
            double smooth = SmoothOcts[Clamp(m.Smoothing, 0, SmoothOcts.Length - 1)];
            int key = DisplayKey(m);
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
                InvalidateVisual();
                return;
            }

            int w = m.WritePos;
            if (!_haveWritePos) { _lastWritePos = w; _haveWritePos = true; _lastNewData = now; }

            if (w != _lastWritePos)
            {
                _lastWritePos = w;
                _lastNewData = now;
                m.CopyLatest(_frame, n, w);
                _an.Process(_frame, dt, tau, peak, slope, smooth);
                if (_pendingCapture) { _an.CaptureReference(); _an.Refresh(peak, slope, smooth); _pendingCapture = false; }
            }
            else if (now - _lastNewData > 0.15)
            {
                // Ring stopped advancing: WM_NOIO, muted, or disconnected. Let traces fall.
                // The 150 ms grace keeps large host buffers from flickering the display.
                _an.ProcessSilence(dt, tau, peak, slope, smooth);
            }

            InvalidateVisual();
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
            return PlotT + t * (PlotB - PlotT);
        }

        // ── Rendering ──────────────────────────────────────────────────────
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(_bg, null, new Rect(0, 0, W, H));
            dc.DrawRectangle(_plotBg, null, new Rect(PlotL, PlotT, PlotR - PlotL, PlotB - PlotT));

            var m = _m;
            double floor = m != null ? Floors[Clamp(m.Range, 0, Floors.Length - 1)] : -96.0;
            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            try
            {
                DrawGrid(dc, floor, ppd);
                if (m == null) return;

                float[] main = _an.Main, pk = _an.Peak, rf = _an.Reference;
                int cols = main.Length;
                if (cols > 1)
                {
                    // Filled main trace
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(new Point(PlotL, PlotB), true, true);
                        for (int c = 0; c < cols; c++)
                            ctx.LineTo(new Point(PlotL + c + 0.5, DbToY(main[c], floor)), true, false);
                        ctx.LineTo(new Point(PlotL + cols, PlotB), false, false);
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
                if (pl > 0) DrawPeakLabel(dc, pl == 1 ? 250.0 : _an.FMax, floor, ppd,
                                          Slopes[Clamp(m.Slope, 0, Slopes.Length - 1)]);

                // Status line
                int n = FftSizes[Clamp(m.FftSize, 0, FftSizes.Length - 1)];
                string status = "FFT " + n + "   " + _an.BinHz.ToString("0.0", CultureInfo.InvariantCulture) +
                                " Hz bins   " + WinNames[Clamp(m.Window, 0, 2)] + "   " +
                                ChanNames[Clamp(m.Channel, 0, 3)];
                string sm = SmoothNames[Clamp(m.Smoothing, 0, SmoothNames.Length - 1)];
                if (sm.Length > 0) status += "   " + sm;
                DrawText(dc, status, PlotL + 6, PlotT + 4, _label, 10, ppd);

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
                        string ds = main[c] <= -199f ? "-inf dB" : main[c].ToString("0.0", CultureInfo.InvariantCulture) + " dB";
                        string line = fs + "   " + NoteName(f) + "   " + ds;
                        if (m.Reference && _an.HasReference && c < rf.Length && rf[c] > -199f)
                            line += "   ref " + rf[c].ToString("0.0", CultureInfo.InvariantCulture) + " dB";
                        DrawText(dc, line, PlotL + 6, PlotT + 18, _text, 11, ppd);
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
            for (double db = 0; db >= floor; db -= step)
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
                dc.DrawLine(_gridPen, new Point(x, PlotT), new Point(x, PlotB));
                string s = f >= 1000 ? (f / 1000).ToString("0", CultureInfo.InvariantCulture) + "k"
                                     : f.ToString("0", CultureInfo.InvariantCulture);
                var ft = MakeText(s, _label, 10, ppd);
                double tx = Math.Min(Math.Max(x - ft.Width / 2, PlotL), W - ft.Width - 2);
                dc.DrawText(ft, new Point(tx, PlotB + 4));
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
            if (ty < PlotT + 34) ty = y + 6;                         // no room above: go below
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
}
