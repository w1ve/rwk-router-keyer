/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace MorseTest;

/// <summary>
/// Generates broadcast-quality Morse code audio as PCM (short[]).
///
/// Key design points:
///   • Continuous-phase sine wave — the oscillator never resets, so there
///     are zero discontinuities even at tone boundaries.
///   • Raised-cosine keying envelope (5ms rise/fall) — eliminates key clicks
///     by smoothly ramping the amplitude at the start and end of each element.
///     This is the same shaping used by real CW transmitters per ITU standards.
///   • Correct PARIS timing at the specified WPM.
///
/// Timing at 25 WPM (PARIS standard):
///   1 dot-unit = 1200/WPM = 48ms
///   Dot:  1 unit (48ms), Dash: 3 units (144ms)
///   Intra-char gap: 1 unit, Inter-char gap: 3 units, Word gap: 7 units
/// </summary>
public sealed class MorseGenerator
{
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly double _toneHz;
    private readonly double _amplitude;

    private readonly int _dotSamples;
    private readonly int _dashSamples;
    private readonly int _intraGapSamples;
    private readonly int _charGapSamples;
    private readonly int _wordGapSamples;
    private readonly int _rampSamples; // 5ms raised-cosine ramp

    private static readonly Dictionary<char, string> MorseTable = new()
    {
        ['A'] = ".-",     ['B'] = "-...",   ['C'] = "-.-.",   ['D'] = "-..",
        ['E'] = ".",      ['F'] = "..-.",   ['G'] = "--.",    ['H'] = "....",
        ['I'] = "..",     ['J'] = ".---",   ['K'] = "-.-",    ['L'] = ".-..",
        ['M'] = "--",     ['N'] = "-.",     ['O'] = "---",    ['P'] = ".--.",
        ['Q'] = "--.-",   ['R'] = ".-.",    ['S'] = "...",    ['T'] = "-",
        ['U'] = "..-",    ['V'] = "...-",   ['W'] = ".--",    ['X'] = "-..-",
        ['Y'] = "-.--",   ['Z'] = "--..",
        ['0'] = "-----",  ['1'] = ".----",  ['2'] = "..---",  ['3'] = "...--",
        ['4'] = "....-",  ['5'] = ".....",  ['6'] = "-....",  ['7'] = "--...",
        ['8'] = "---..",   ['9'] = "----.",
        ['/'] = "-..-.",  ['?'] = "..--..", ['='] = "-...-",  ['.'] = ".-.-.-",
        [','] = "--..--", [':'] = "---...", ['-'] = "-....-",
    };

    public MorseGenerator(int sampleRate, int channels = 2, int wpm = 25,
        double toneHz = 750, double amplitude = 0.5)
    {
        _sampleRate = sampleRate;
        _channels   = channels;
        _toneHz     = toneHz;
        _amplitude  = amplitude;

        double dotMs = 1200.0 / wpm;
        _dotSamples      = (int)(sampleRate * dotMs / 1000.0);
        _dashSamples     = _dotSamples * 3;
        _intraGapSamples = _dotSamples;
        _charGapSamples  = _dotSamples * 3;
        _wordGapSamples  = _dotSamples * 7;
        _rampSamples     = (int)(sampleRate * 0.005); // 5ms rise/fall
    }

    public int SampleRate => _sampleRate;
    public int Channels => _channels;

    /// <summary>
    /// Generates a shaped tone for the specified duration in milliseconds.
    /// This is used for keyed CW operation where we just need tone on/off.
    /// </summary>
    public short[] GenerateTone(int durationMs, bool leftChannel = true, bool rightChannel = true)
    {
        int totalSamples = (int)(_sampleRate * durationMs / 1000.0);
        var pcm = new short[totalSamples * _channels];

        int ramp = Math.Min(_rampSamples, totalSamples / 2);

        for (int i = 0; i < totalSamples; i++)
        {
            double t = (double)i / _sampleRate;
            double sine = Math.Sin(2.0 * Math.PI * _toneHz * t);

            // Raised-cosine envelope
            double env = 1.0;
            if (i < ramp)
            {
                env = 0.5 * (1.0 - Math.Cos(Math.PI * i / ramp));
            }
            else if (i >= totalSamples - ramp)
            {
                int j = totalSamples - 1 - i;
                env = 0.5 * (1.0 - Math.Cos(Math.PI * j / ramp));
            }

            double sample = sine * env * _amplitude * short.MaxValue;
            short s = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);

            if (_channels == 2)
            {
                pcm[i * 2]     = leftChannel  ? s : (short)0;
                pcm[i * 2 + 1] = rightChannel ? s : (short)0;
            }
            else
            {
                pcm[i] = s;
            }
        }

        return pcm;
    }

    /// <summary>
    /// Generates silence for the specified duration in milliseconds.
    /// </summary>
    public short[] GenerateSilence(int durationMs)
    {
        int totalSamples = (int)(_sampleRate * durationMs / 1000.0);
        return new short[totalSamples * _channels];
    }

    public short[] Generate(string text, bool leftChannel = true, bool rightChannel = true)
    {
        var segments = BuildSegments(text);

        // Total mono samples + tail silence
        int totalMono = _charGapSamples;
        foreach (var seg in segments)
            totalMono += seg.Samples;

        var pcm = new short[totalMono * _channels];
        int pos = 0; // global sample counter (continuous phase)

        foreach (var seg in segments)
        {
            for (int i = 0; i < seg.Samples; i++)
            {
                double sample = 0.0;

                if (seg.IsTone)
                {
                    // Continuous-phase sine (pos never resets)
                    double t = (double)pos / _sampleRate;
                    double sine = Math.Sin(2.0 * Math.PI * _toneHz * t);

                    // Raised-cosine envelope for this element:
                    //   First _rampSamples: ramp up from 0 to 1
                    //   Last _rampSamples:  ramp down from 1 to 0
                    //   Middle: full amplitude (1.0)
                    double env = 1.0;
                    int ramp = Math.Min(_rampSamples, seg.Samples / 2);

                    if (i < ramp)
                    {
                        // Rising edge: 0.5 * (1 - cos(π * i / ramp))
                        env = 0.5 * (1.0 - Math.Cos(Math.PI * i / ramp));
                    }
                    else if (i >= seg.Samples - ramp)
                    {
                        // Falling edge
                        int j = seg.Samples - 1 - i;
                        env = 0.5 * (1.0 - Math.Cos(Math.PI * j / ramp));
                    }

                    sample = sine * env * _amplitude * short.MaxValue;
                }

                short s = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);

                if (_channels == 2)
                {
                    pcm[pos * 2]     = leftChannel  ? s : (short)0;
                    pcm[pos * 2 + 1] = rightChannel ? s : (short)0;
                }
                else
                {
                    pcm[pos] = s;
                }

                pos++;
            }
        }

        return pcm;
    }

    public double GetDurationSeconds(string text)
    {
        var segments = BuildSegments(text);
        int total = _charGapSamples;
        foreach (var seg in segments)
            total += seg.Samples;
        return (double)total / _sampleRate;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private record struct Segment(bool IsTone, int Samples);

    private List<Segment> BuildSegments(string text)
    {
        var segments = new List<Segment>();
        text = text.ToUpperInvariant();
        bool needCharGap = false;

        for (int ci = 0; ci < text.Length; ci++)
        {
            char c = text[ci];

            if (c == ' ')
            {
                if (segments.Count > 0 && !segments[^1].IsTone)
                    segments.RemoveAt(segments.Count - 1);
                segments.Add(new Segment(false, _wordGapSamples));
                needCharGap = false;
                continue;
            }

            if (!MorseTable.TryGetValue(c, out var code))
                continue;

            if (needCharGap)
                segments.Add(new Segment(false, _charGapSamples));

            for (int si = 0; si < code.Length; si++)
            {
                int toneSamples = code[si] == '.' ? _dotSamples : _dashSamples;
                segments.Add(new Segment(true, toneSamples));

                if (si < code.Length - 1)
                    segments.Add(new Segment(false, _intraGapSamples));
            }

            needCharGap = true;
        }

        return segments;
    }
}
