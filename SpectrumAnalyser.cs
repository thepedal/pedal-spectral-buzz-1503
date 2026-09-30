// Pedal Spectral — spectrum analyser for Jeskola Buzz
// Copyright (C) 2026 thepedal
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version. See LICENSE.

using System;

namespace PedalSpectral
{
    /// <summary>
    /// Pure-BCL analysis core. No SDK types, no WPF — runs on the GUI thread,
    /// never on the audio thread. Targets .NET Framework 4.8, so it uses Math
    /// (double) rather than MathF, and no Span / BinaryPrimitives.
    ///
    /// Calibration: a full-scale sine (amplitude 1.0 after the /32768 scale)
    /// reads 0 dBFS at its peak, via the window's COHERENT gain (sum of w).
    /// This is the right normalisation for a line spectrum; an energy
    /// normalisation (right for summing bands) would misread tone levels.
    /// </summary>
    internal sealed class SpectrumAnalyser
    {
        public const int MaxN = 8192;

        public const int WinHann = 0;
        public const int WinBlackmanHarris = 1;
        public const int WinFlatTop = 2;

        // FFT scratch + tables, sized once for MaxN
        readonly double[] _re = new double[MaxN];
        readonly double[] _im = new double[MaxN];
        readonly double[] _cosTab = new double[MaxN / 2];
        readonly double[] _sinTab = new double[MaxN / 2];

        // Current configuration
        int _n;
        int _winType = -1;
        readonly double[] _win = new double[MaxN];
        double _coherentSum;          // sum of window samples
        double _sr;
        int _cols;

        // Per-bin averaged power (amplitude^2, sine peak = A^2)
        readonly double[] _avgPow = new double[MaxN / 2 + 1];
        bool _havePow;

        // Per-column output
        float[] _colDb = new float[0];
        float[] _peakDb = new float[0];
        double[] _colF0 = new double[0];   // column start frequency
        double[] _colF1 = new double[0];   // column end frequency
        double[] _colTilt = new double[0]; // slope offset basis: log2(fc / 1000)
        double[] _colFc = new double[0];   // column centre frequency

        // Prefix sums of _avgPow, for O(1) band means when smoothing
        readonly double[] _cum = new double[MaxN / 2 + 2];

        // Reference overlay. Since v1.1.1 it stores the captured SPECTRUM (per-bin
        // power), not the drawn trace, and is drawn through the current column
        // mapping, smoothing and slope — so both traces are always processed the
        // same way, whatever changes after the capture.
        readonly double[] _refPow = new double[MaxN / 2 + 1];
        readonly double[] _refCum = new double[MaxN / 2 + 2];
        int _refHalf;
        double _refBinHz;
        float[] _refOut = new float[0];
        public bool HasReference { get; private set; }
        public float[] Reference => _refOut;

        public const double FMin = 20.0;
        public double FMax { get; private set; } = 20000.0;

        public float[] Main => _colDb;
        public float[] Peak => _peakDb;
        public int N => _n;
        public double BinHz => _n > 0 ? _sr / _n : 0.0;

        const float Silent = -200f;

        public SpectrumAnalyser()
        {
            for (int i = 0; i < MaxN / 2; i++)
            {
                double a = -2.0 * Math.PI * i / MaxN;
                _cosTab[i] = Math.Cos(a);
                _sinTab[i] = Math.Sin(a);
            }
        }

        /// <summary>Reconfigure if anything changed. Cheap no-op otherwise.</summary>
        public void Configure(int n, int winType, int cols, double sr)
        {
            if (sr < 1000.0) sr = 44100.0;
            bool nChanged = n != _n;
            bool srChanged = Math.Abs(sr - _sr) > 0.5;
            bool colsChanged = cols != _cols;

            if (nChanged || winType != _winType)
            {
                BuildWindow(n, winType);
                _winType = winType;
            }
            if (nChanged)
            {
                _n = n;
                Array.Clear(_avgPow, 0, _avgPow.Length);
                _havePow = false;
            }
            if (nChanged || srChanged || colsChanged)
            {
                _sr = sr;
                _cols = cols;
                FMax = Math.Min(20000.0, sr * 0.5);
                BuildColumns();
            }
        }

        void BuildWindow(int n, int type)
        {
            double sum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double x = 2.0 * Math.PI * i / n;   // periodic form
                double w;
                switch (type)
                {
                    case WinHann:
                        w = 0.5 - 0.5 * Math.Cos(x);
                        break;
                    case WinFlatTop:
                        w = 0.21557895 - 0.41663158 * Math.Cos(x) + 0.277263158 * Math.Cos(2 * x)
                          - 0.083578947 * Math.Cos(3 * x) + 0.006947368 * Math.Cos(4 * x);
                        break;
                    default: // Blackman-Harris 4-term
                        w = 0.35875 - 0.48829 * Math.Cos(x) + 0.14128 * Math.Cos(2 * x)
                          - 0.01168 * Math.Cos(3 * x);
                        break;
                }
                _win[i] = w;
                sum += w;
            }
            _coherentSum = sum;
        }

        void BuildColumns()
        {
            int c = Math.Max(1, _cols);
            if (_colDb.Length != c)
            {
                _colDb = new float[c];
                _peakDb = new float[c];
                _colF0 = new double[c];
                _colF1 = new double[c];
                _colTilt = new double[c];
                _colFc = new double[c];
                _refOut = new float[c];
            }
            double ratio = FMax / FMin;
            for (int i = 0; i < c; i++)
            {
                double f0 = FMin * Math.Pow(ratio, (double)i / c);
                double f1 = FMin * Math.Pow(ratio, (double)(i + 1) / c);
                _colF0[i] = f0;
                _colF1[i] = f1;
                _colFc[i] = Math.Sqrt(f0 * f1);
                _colTilt[i] = Math.Log(_colFc[i] / 1000.0, 2.0);
                _colDb[i] = Silent;
                _peakDb[i] = Silent;
                _refOut[i] = Silent;
            }
        }

        /// <summary>
        /// Analyse one frame. frame[0..n) is oldest..newest, already in ±1.0 scale.
        /// avgTau: seconds (0 = no averaging). peakDecay: dB/s, 0 = hold forever,
        /// negative = peak trace disabled. slope: dB/oct, pivot 1 kHz.
        /// smoothOct: fractional-octave smoothing width in octaves (0 = off).
        /// </summary>
        public void Process(float[] frame, double dt, double avgTau, double peakDecay, double slope, double smoothOct)
        {
            int n = _n;
            for (int i = 0; i < n; i++) { _re[i] = frame[i] * _win[i]; _im[i] = 0.0; }
            Fft(n);

            double norm = 2.0 / _coherentSum;
            double a = avgTau > 0.0 ? Math.Exp(-dt / avgTau) : 0.0;
            if (!_havePow) a = 0.0;   // first frame: snap, don't fade in from silence
            int half = n / 2;
            for (int k = 0; k <= half; k++)
            {
                double mr = _re[k] * norm, mi = _im[k] * norm;
                double p = mr * mr + mi * mi;
                _avgPow[k] = a * _avgPow[k] + (1.0 - a) * p;
            }
            _havePow = true;
            MapColumns(dt, peakDecay, slope, smoothOct);
        }

        /// <summary>No input arrived (WM_NOIO / not being called): let traces fall.</summary>
        public void ProcessSilence(double dt, double avgTau, double peakDecay, double slope, double smoothOct)
        {
            double a = avgTau > 0.0 ? Math.Exp(-dt / avgTau) : 0.0;
            int half = _n / 2;
            for (int k = 0; k <= half; k++) _avgPow[k] *= a;
            MapColumns(dt, peakDecay, slope, smoothOct);
        }

        void MapColumns(double dt, double peakDecay, double slope, double smoothOct)
        {
            int half = _n / 2;
            double binHz = _sr / Math.Max(1, _n);
            double smoothHalf = smoothOct > 0.0 ? Math.Pow(2.0, smoothOct * 0.5) : 0.0;
            BuildCum(_avgPow, _cum, half);

            for (int c = 0; c < _colDb.Length; c++)
            {
                float tilt = (float)(slope * _colTilt[c]);

                double p = ColumnPower(_avgPow, _cum, half, binHz, c, smoothHalf);
                float db = p > 1e-20 ? (float)(10.0 * Math.Log10(p)) + tilt : Silent;
                _colDb[c] = db;

                if (HasReference)
                {
                    double rp = ColumnPower(_refPow, _refCum, _refHalf, _refBinHz, c, smoothHalf);
                    _refOut[c] = rp > 1e-20 ? (float)(10.0 * Math.Log10(rp)) + tilt : Silent;
                }

                if (peakDecay < 0.0) { _peakDb[c] = Silent; continue; }
                float pk = _peakDb[c];
                if (peakDecay > 0.0) pk -= (float)(peakDecay * dt);
                if (db > pk) pk = db;
                if (pk < Silent) pk = Silent;
                _peakDb[c] = pk;
            }
        }

        static void BuildCum(double[] pow, double[] cum, int half)
        {
            double acc = 0.0;
            cum[0] = 0.0;
            for (int k = 0; k <= half; k++) { acc += pow[k]; cum[k + 1] = acc; }
        }

        /// <summary>
        /// Power for column c from a per-bin spectrum. smoothHalf = 2^(oct/2), or 0 for off.
        ///
        /// Bins are treated as boxes one bin wide, and any band narrower than one bin
        /// is widened to one bin about its centre. A one-bin box mean IS linear
        /// interpolation between bin centres, so narrow columns interpolate smoothly
        /// and smoothing blends in continuously — no plateaus where a narrow band
        /// happens to contain a bin centre (the v1.1.0 low-end flat-step artefact).
        /// Wide unsmoothed columns take the max, so narrow peaks survive the log axis.
        /// </summary>
        double ColumnPower(double[] pow, double[] cum, int half, double binHz, int c, double smoothHalf)
        {
            double k0 = _colF0[c] / binHz, k1 = _colF1[c] / binHz;
            bool smooth = smoothHalf > 0.0;
            if (smooth)
            {
                double fc = _colFc[c];
                k0 = Math.Min(k0, fc / smoothHalf / binHz);
                k1 = Math.Max(k1, fc * smoothHalf / binHz);
            }

            if (smooth || k1 - k0 < 1.0)
            {
                double kc = 0.5 * (k0 + k1);
                double w = Math.Max(k1 - k0, 1.0);
                return BoxMean(pow, cum, half, kc - 0.5 * w, kc + 0.5 * w);
            }

            int ka = (int)Math.Ceiling(k0), kb = (int)Math.Floor(k1);
            if (kb > half) kb = half;
            if (ka > kb) return pow[Math.Min(half, Math.Max(0, ka))];
            double p = 0.0;
            for (int k = ka; k <= kb; k++) if (pow[k] > p) p = pow[k];
            return p;
        }

        // Mean of the bin-box step function over [a, b] (bin units; bin k spans k-0.5..k+0.5).
        static double BoxMean(double[] pow, double[] cum, int half, double a, double b)
        {
            double lo = -0.5, hi = half + 0.5;
            if (a < lo) a = lo;
            if (b > hi) b = hi;
            if (b <= a) return pow[Math.Min(half, Math.Max(0, (int)Math.Round(a)))];
            return (StepIntegral(pow, cum, half, b) - StepIntegral(pow, cum, half, a)) / (b - a);
        }

        // Integral of the step function from -0.5 up to x.
        static double StepIntegral(double[] pow, double[] cum, int half, double x)
        {
            double y = x + 0.5;
            int j = (int)Math.Floor(y);
            if (j < 0) return 0.0;
            if (j > half) return cum[half + 1];
            return cum[j] + (y - j) * pow[j];
        }

        // ── Reference overlay ─────────────────────────────────────────────
        /// <summary>Capture the current averaged spectrum as the reference.</summary>
        public void CaptureReference()
        {
            if (_n == 0) return;
            int half = _n / 2;
            Array.Copy(_avgPow, _refPow, half + 1);
            BuildCum(_refPow, _refCum, half);
            _refHalf = half;
            _refBinHz = _sr / _n;
            HasReference = true;
        }

        /// <summary>Re-map the held spectrum with current settings, no new audio (frozen, or just captured).</summary>
        public void Refresh(double peakDecay, double slope, double smoothOct)
        {
            if (_n > 0) MapColumns(0.0, peakDecay, slope, smoothOct);
        }

        public void ClearReference()
        {
            HasReference = false;
            for (int i = 0; i < _refOut.Length; i++) _refOut[i] = Silent;
        }

        public void ResetPeaks()
        {
            for (int i = 0; i < _peakDb.Length; i++) _peakDb[i] = Silent;
        }

        /// <summary>Column centre frequency, for the hover readout.</summary>
        public double ColumnFreq(int c)
        {
            if (_colDb.Length == 0) return 0.0;
            double ratio = FMax / FMin;
            return FMin * Math.Pow(ratio, (c + 0.5) / _colDb.Length);
        }

        // In-place iterative radix-2 complex FFT on _re/_im, length n (power of 2, <= MaxN)
        void Fft(int n)
        {
            // bit reversal
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double tr = _re[i]; _re[i] = _re[j]; _re[j] = tr;
                    double ti = _im[i]; _im[i] = _im[j]; _im[j] = ti;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                int halfLen = len >> 1;
                int step = MaxN / len;
                for (int i = 0; i < n; i += len)
                {
                    for (int k = 0; k < halfLen; k++)
                    {
                        double wr = _cosTab[k * step], wi = _sinTab[k * step];
                        int a = i + k, b = a + halfLen;
                        double xr = _re[b] * wr - _im[b] * wi;
                        double xi = _re[b] * wi + _im[b] * wr;
                        _re[b] = _re[a] - xr; _im[b] = _im[a] - xi;
                        _re[a] += xr;         _im[a] += xi;
                    }
                }
            }
        }
    }
}
