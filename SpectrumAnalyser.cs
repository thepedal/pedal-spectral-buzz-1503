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
    /// normalisation (as used for band sums in Pedal OSC) would be wrong here.
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
        double[] _colK0 = new double[0];   // fractional bin at column start
        double[] _colK1 = new double[0];   // fractional bin at column end
        double[] _colTilt = new double[0]; // slope offset basis: log2(fc / 1000)

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
                _colK0 = new double[c];
                _colK1 = new double[c];
                _colTilt = new double[c];
            }
            double binHz = _sr / Math.Max(1, _n);
            double ratio = FMax / FMin;
            for (int i = 0; i < c; i++)
            {
                double f0 = FMin * Math.Pow(ratio, (double)i / c);
                double f1 = FMin * Math.Pow(ratio, (double)(i + 1) / c);
                _colK0[i] = f0 / binHz;
                _colK1[i] = f1 / binHz;
                _colTilt[i] = Math.Log(Math.Sqrt(f0 * f1) / 1000.0, 2.0);
                _colDb[i] = Silent;
                _peakDb[i] = Silent;
            }
        }

        /// <summary>
        /// Analyse one frame. frame[0..n) is oldest..newest, already in ±1.0 scale.
        /// avgTau: seconds (0 = no averaging). peakDecay: dB/s, 0 = hold forever,
        /// negative = peak trace disabled. slope: dB/oct, pivot 1 kHz.
        /// </summary>
        public void Process(float[] frame, double dt, double avgTau, double peakDecay, double slope)
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
            MapColumns(dt, peakDecay, slope);
        }

        /// <summary>No input arrived (WM_NOIO / not being called): let traces fall.</summary>
        public void ProcessSilence(double dt, double avgTau, double peakDecay, double slope)
        {
            double a = avgTau > 0.0 ? Math.Exp(-dt / avgTau) : 0.0;
            int half = _n / 2;
            for (int k = 0; k <= half; k++) _avgPow[k] *= a;
            MapColumns(dt, peakDecay, slope);
        }

        void MapColumns(double dt, double peakDecay, double slope)
        {
            int half = _n / 2;
            for (int c = 0; c < _colDb.Length; c++)
            {
                double k0 = _colK0[c], k1 = _colK1[c];
                int ka = (int)Math.Ceiling(k0), kb = (int)Math.Floor(k1);
                double p;
                if (kb >= ka)
                {
                    // Column spans one or more whole bins: take the max so
                    // narrow peaks survive the log-axis squeeze at the top end.
                    if (kb > half) kb = half;
                    p = 0.0;
                    for (int k = ka; k <= kb; k++) if (_avgPow[k] > p) p = _avgPow[k];
                }
                else
                {
                    // Column narrower than a bin (low end): interpolate.
                    double kc = 0.5 * (k0 + k1);
                    int lo = (int)Math.Floor(kc);
                    if (lo >= half) { p = _avgPow[half]; }
                    else
                    {
                        double t = kc - lo;
                        p = _avgPow[lo] * (1.0 - t) + _avgPow[lo + 1] * t;
                    }
                }

                float db = p > 1e-20 ? (float)(10.0 * Math.Log10(p) + slope * _colTilt[c]) : Silent;
                _colDb[c] = db;

                if (peakDecay < 0.0) { _peakDb[c] = Silent; continue; }
                float pk = _peakDb[c];
                if (peakDecay > 0.0) pk -= (float)(peakDecay * dt);
                if (db > pk) pk = db;
                if (pk < Silent) pk = Silent;
                _peakDb[c] = pk;
            }
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
