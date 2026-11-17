using System.Threading;
using NAudio.Dsp;
using NAudio.Wave;

namespace DynamicIsland;

/// <summary>Спектр звука, который сейчас воспроизводит Windows (захват системного звука).</summary>
public sealed class AudioSpectrum : IDisposable
{
    public const int Bands = 5;
    private const int FftSize = 1024;
    private const int FftPower = 10;
    private const int Hop = 480;

    // Границы полос в герцах: бас, низ, середина, верх, воздух.
    private static readonly double[] Edges = { 40, 150, 400, 1100, 3200, 9000 };

    private readonly object _lock = new();
    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _levels = new float[Bands];

    private int _pos;
    private int _filled;
    private int _sinceAnalysis;
    private int _sampleRate = 48000;
    private int _channels = 2;
    private int _bitsPerSample = 32;
    private double _gain = 0.35;
    private long _lastDataTick;
    private WasapiLoopbackCapture? _capture;

    public AudioSpectrum()
    {
        for (var i = 0; i < FftSize; i++)
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
    }

    public bool IsRunning => _capture is not null;

    public long LastDataTick => Interlocked.Read(ref _lastDataTick);

    public bool Start()
    {
        if (_capture is not null) return true;
        try
        {
            var capture = new WasapiLoopbackCapture();
            _sampleRate = capture.WaveFormat.SampleRate;
            _channels = Math.Max(1, capture.WaveFormat.Channels);
            _bitsPerSample = capture.WaveFormat.BitsPerSample;
            if (_bitsPerSample != 32 && _bitsPerSample != 16)
            {
                capture.Dispose();
                return false;
            }

            capture.DataAvailable += OnData;
            capture.StartRecording();
            _capture = capture;
            return true;
        }
        catch
        {
            _capture = null;
            return false;
        }
    }

    public void Stop()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null) return;

        try
        {
            capture.DataAvailable -= OnData;
            capture.StopRecording();
        }
        catch { }
        try { capture.Dispose(); } catch { }

        lock (_lock) { Array.Clear(_levels); }
    }

    public void GetLevels(float[] destination)
    {
        lock (_lock)
        {
            Array.Copy(_levels, destination, Math.Min(Bands, destination.Length));
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            var bytesPerSample = _bitsPerSample / 8;
            var frameSize = bytesPerSample * _channels;
            var frames = e.BytesRecorded / frameSize;

            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < _channels; channel++)
                {
                    var offset = frame * frameSize + channel * bytesPerSample;
                    sum += _bitsPerSample == 32
                        ? BitConverter.ToSingle(e.Buffer, offset)
                        : BitConverter.ToInt16(e.Buffer, offset) / 32768f;
                }
                PushSample(sum / _channels);
            }

            Interlocked.Exchange(ref _lastDataTick, Environment.TickCount64);
        }
        catch { }
    }

    private void PushSample(float sample)
    {
        _ring[_pos] = sample;
        _pos = (_pos + 1) % FftSize;
        if (_filled < FftSize) _filled++;

        if (++_sinceAnalysis >= Hop && _filled >= FftSize)
        {
            _sinceAnalysis = 0;
            Analyze();
        }
    }

    private void Analyze()
    {
        for (var i = 0; i < FftSize; i++)
        {
            var index = (_pos + i) % FftSize;
            _fft[i].X = _ring[index] * _window[i];
            _fft[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, FftPower, _fft);

        var binWidth = _sampleRate / (double)FftSize;
        var values = new double[Bands];
        var peak = 0.0;

        for (var band = 0; band < Bands; band++)
        {
            var low = Math.Max(1, (int)Math.Floor(Edges[band] / binWidth));
            var high = Math.Min(FftSize / 2 - 1, (int)Math.Ceiling(Edges[band + 1] / binWidth));
            if (high < low) high = low;

            double energy = 0;
            for (var bin = low; bin <= high; bin++)
                energy += _fft[bin].X * _fft[bin].X + _fft[bin].Y * _fft[bin].Y;

            var magnitude = Math.Sqrt(energy / (high - low + 1));
            var decibels = 20.0 * Math.Log10(magnitude + 1e-9);
            var value = Math.Clamp((decibels + 62.0 + band * 3.0) / 42.0, 0.0, 1.0);
            values[band] = value;
            if (value > peak) peak = value;
        }

        // Автоусиление: рисунок не зависит от громкости системы, но тишина остаётся тишиной.
        _gain = Math.Max(peak, _gain * 0.997);
        var norm = Math.Max(_gain, 0.35);

        lock (_lock)
        {
            for (var band = 0; band < Bands; band++)
                _levels[band] = (float)Math.Clamp(values[band] / norm, 0.0, 1.0);
        }
    }

    public void Dispose() => Stop();
}
