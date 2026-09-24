using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace WhisperInk
{
    /// <summary>
    /// Decides whether a take has speech in it, before any API call.
    ///
    /// The gate this replaced averaged RMS over the whole hold and compared it
    /// with 0.003, on the assumption that speech averages 0.01-0.1 RMS. On
    /// 2026-09-23 the owner's accepted takes averaged 0.0032-0.030 (most of
    /// them 0.003-0.007) on the VEC USB mic at a 94% input level, and three
    /// takes of real, quiet speech (0.0017-0.0026) were discarded as silence.
    /// Discarded takes weren't journaled, so those were lost. An average also
    /// dilutes: a long hold with a short phrase in it averages down toward the
    /// noise floor.
    ///
    /// So a take now counts as silent only when BOTH hold:
    ///   - its whole-take RMS is below the silence threshold (as before), and
    ///   - it has no sustained speech: no run of three 30 ms frames (90 ms)
    ///     that stands clear of the take's own noise floor — each at least 3x
    ///     the 5th-percentile frame and at least 0.002 RMS.
    /// That can only let MORE takes through than the old gate, never fewer.
    /// The floor is measured per take, so steady noise (a fan) never reads as
    /// speech however loud it is, and a click (one frame) never makes a run.
    ///
    /// Calibrated 2026-09-23 on nine recordings of the owner's voice (three
    /// takes from that evening and the six clinical clips):
    ///   - found in all nine at full, half and quarter volume, and buried in
    ///     20 s of their own noise floor;
    ///   - found in all nine trimmed to just the speech (a cold-mic take: no
    ///     pre-roll, released on the last word) at a whole-take RMS of 0.003,
    ///     0.0026, 0.002 and 0.0015;
    ///   - found in 34 of 36 one-second windows cut from INSIDE the speech
    ///     (no edges at all) at 0.0026 and 0.002. A 10th-percentile floor at
    ///     4x found 31 of 36;
    ///   - no speech found in steady noise up to 0.0025 RMS, noise swelling
    ///     +/-50%, or clicks.
    /// A signal with no quiet moments at all (a steady hum of a voice) can't
    /// be told from steady noise this way; real speech has gaps.
    /// </summary>
    public static class SpeechDetector
    {
        public const int FrameMs = 30;
        public const int MinRunFrames = 3;
        public const double FloorPercentile = 0.05;
        public const double OverFloor = 3.0;
        public const double MinSpeechFrameRms = 0.002;

        /// <summary>Levels as 0..1 fractions of full scale. SpeechMs is the
        /// total length of the sustained-speech runs found; NoiseFloor is the
        /// take's 5th-percentile frame RMS.</summary>
        public readonly record struct Level(double Peak, double Rms, double NoiseFloor, double SpeechMs, bool Measured = true)
        {
            public string Describe() => Measured
                ? $"RMS {Rms:F5}, peak {Peak:F4}, floor {NoiseFloor:F5}, speech {SpeechMs:F0}ms"
                : "unmeasurable (treated as speech)";
        }

        /// <summary>What an unreadable take measures as: full scale, and never
        /// silent. The gates must not drop audio they couldn't measure.</summary>
        public static readonly Level Unmeasured = new(1.0, 1.0, 0.0, 0.0, Measured: false);

        /// <summary>True only when the whole take is quiet AND nothing in it
        /// sounds like speech. A threshold of 0 (the gate disabled) is never
        /// silent.</summary>
        public static bool IsSilent(Level level, double silenceThreshold) =>
            level.Measured && level.Rms < silenceThreshold && level.SpeechMs <= 0;

        /// <summary>Peak, RMS, noise floor and sustained speech of a 16-bit
        /// PCM WAV, in one pass.</summary>
        public static Level Measure(byte[] wavBytes)
        {
            try
            {
                using var ms = new MemoryStream(wavBytes, writable: false);
                using var reader = new WaveFileReader(ms);
                var fmt = reader.WaveFormat;
                if (fmt.Encoding != WaveFormatEncoding.Pcm || fmt.BitsPerSample != 16 || fmt.Channels < 1)
                    return Unmeasured;

                int samplesPerFrame = Math.Max(1, fmt.SampleRate * FrameMs / 1000) * fmt.Channels;
                var frames = new List<double>();
                var buffer = new byte[8192];
                int peak = 0, read, inFrame = 0;
                double sumSquares = 0, frameSquares = 0;
                long count = 0;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i + 1 < read; i += 2)
                    {
                        int sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                        int magnitude = Math.Abs(sample);
                        if (magnitude > peak) peak = magnitude;
                        double norm = sample / (double)short.MaxValue;
                        double square = norm * norm;
                        sumSquares += square;
                        frameSquares += square;
                        count++;
                        if (++inFrame == samplesPerFrame)
                        {
                            frames.Add(Math.Sqrt(frameSquares / inFrame));
                            frameSquares = 0;
                            inFrame = 0;
                        }
                    }
                }
                if (inFrame > 0) frames.Add(Math.Sqrt(frameSquares / inFrame));
                if (count == 0) return new Level(0, 0, 0, 0);

                double rms = Math.Sqrt(sumSquares / count);
                var sorted = new List<double>(frames);
                sorted.Sort();
                double floor = sorted[Math.Min(sorted.Count - 1, (int)(FloorPercentile * sorted.Count))];
                double threshold = Math.Max(MinSpeechFrameRms, OverFloor * floor);

                int run = 0, speechFrames = 0;
                foreach (double frame in frames)
                {
                    if (frame >= threshold)
                    {
                        run++;
                        if (run == MinRunFrames) speechFrames += MinRunFrames;
                        else if (run > MinRunFrames) speechFrames++;
                    }
                    else run = 0;
                }
                return new Level(peak / (double)short.MaxValue, rms, floor, speechFrames * (double)FrameMs);
            }
            catch { return Unmeasured; }
        }
    }
}
