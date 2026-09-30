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
    /// a UserControl's template would paint over everything (Core §26.7).
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
        static readonly double[] GridFreqs = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };

        IMachine _iMachine;
        PedalSpectralMachine _m;

        readonly SpectrumAnalyser _an = new SpectrumAnalyser();
        readonly float[] _frame = new float[SpectrumAnalyser.MaxN];
        readonly DispatcherTimer _timer;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        double _lastTick, _lastNewData;
        int _lastWritePos;
        bool _haveWritePos;
        int _lastPeakMode = -99;
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
        readonly Typeface _face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

        public IMachine Machine
        {
            get => _iMachine;
            set
            {
                _iMachine = value;
                _m = value?.ManagedMachine as PedalSpectralMachine;
                _haveWritePos = false;
            }
        }

        public SpectralGui()
        {
            Height = H;
            MinWidth = MinW;
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
            if (pkIdx != _lastPeakMode) { _an.ResetPeaks(); _lastPeakMode = pkIdx; }

            if (m.Freeze) { InvalidateVisual(); return; }

            int w = m.WritePos;
            if (!_haveWritePos) { _lastWritePos = w; _haveWritePos = true; _lastNewData = now; }

            if (w != _lastWritePos)
            {
                _lastWritePos = w;
                _lastNewData = now;
                m.CopyLatest(_frame, n, w);
                _an.Process(_frame, dt, tau, peak, slope);
            }
            else if (now - _lastNewData > 0.15)
            {
                // Ring stopped advancing: WM_NOIO, muted, or disconnected. Let traces fall.
                // The 150 ms grace keeps large host buffers from flickering the display.
                _an.ProcessSilence(dt, tau, peak, slope);
            }

            InvalidateVisual();
        }

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

                float[] main = _an.Main, pk = _an.Peak;
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

                // Status line
                int n = FftSizes[Clamp(m.FftSize, 0, FftSizes.Length - 1)];
                string status = "FFT " + n + "   " + _an.BinHz.ToString("0.0", CultureInfo.InvariantCulture) +
                                " Hz bins   " + WinNames[Clamp(m.Window, 0, 2)] + "   " +
                                ChanNames[Clamp(m.Channel, 0, 3)];
                DrawText(dc, status, PlotL + 6, PlotT + 4, _label, 10, ppd);

                if (m.Freeze)
                    DrawText(dc, "FROZEN", PlotR - 50, PlotT + 4, _frozenBadge, 10, ppd);

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
                        DrawText(dc, fs + "   " + ds, PlotL + 6, PlotT + 18, _text, 11, ppd);
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
