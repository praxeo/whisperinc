using System;
using System.IO;
using NAudio.Wave;

namespace WhisperInk
{
    /// <summary>
    /// The incomplete-transcript check, ported from elevenlabs-web's
    /// coverageShortfall. A result whose last word ends well before the last
    /// speech the microphone heard — or a service that decoded far less audio
    /// than was recorded — came back TRUNCATED: real text, but not all of it.
    /// Pasting that as a clean success is how the end of a note goes missing
    /// without anyone noticing, so the caller pastes it with a warning and
    /// keeps the recording for a retry instead.
    ///
    /// Baselined on the last SPEECH in the recording, never on how long the
    /// key was held: finishing a sentence and then keeping the key down must
    /// not read as a truncated transcript. The slack (20 s, or a quarter of
    /// the take when longer) absorbs trailing silence and normal pauses;
    /// on long takes the fraction dominates, so a proportionally small tail
    /// never warns. Missing data (no word timestamps, no duration) can only
    /// mean "no warning", never a false one.
    /// </summary>
    public static class TranscriptCoverage
    {
        public const double MinSlackSeconds = 20;
        public const double SlackFraction = 0.25;

        // Speech detection on the captured WAV. A 30 ms frame counts as speech
        // above 0.01 RMS: measured silence on this mic is 0.0006-0.0012 RMS
        // and speech 0.01-0.1 averaged over a whole take (voiced frames run
        // higher), so this sits ~10x over the floor. Three frames in a row are
        // required so a single click or bump can't pass for the last speech.
        public const int FrameMs = 30;
        public const double SpeechFrameRms = 0.01;
        public const int MinSpeechFrames = 3;

        public readonly record struct Shortfall(double CoveredSeconds, double ExpectedSeconds, string Basis)
        {
            public string Describe() =>
                $"covered {CoveredSeconds:F1}s of {ExpectedSeconds:F1}s of speech (by {Basis})";
        }

        /// <summary>Null when the transcript plausibly covers the recording;
        /// otherwise how far it got and how far it should have.</summary>
        public static Shortfall? Check(double recordedSeconds, double? lastSpeechSeconds,
                                       double? lastWordEndSeconds, double? decodedAudioSeconds)
        {
            if (!double.IsFinite(recordedSeconds) || recordedSeconds <= 0) return null;

            double expected = lastSpeechSeconds is double speech && double.IsFinite(speech) && speech > 0
                ? Math.Min(recordedSeconds, speech)
                : recordedSeconds;
            double slack = Math.Max(MinSlackSeconds, SlackFraction * recordedSeconds);

            bool transcriptShort = lastWordEndSeconds is double lastWord && double.IsFinite(lastWord)
                                   && lastWord > 0 && expected - lastWord > slack;
            bool decodedShort = decodedAudioSeconds is double decoded && double.IsFinite(decoded)
                                && decoded >= 0 && recordedSeconds - decoded > slack;

            if (transcriptShort) return new Shortfall(lastWordEndSeconds!.Value, expected, "last word");
            if (decodedShort) return new Shortfall(decodedAudioSeconds!.Value, recordedSeconds, "audio the service decoded");
            return null;
        }

        /// <summary>End (s) of the last run of speech in a 16-bit PCM WAV, or
        /// null when there is none or the audio can't be read.</summary>
        public static double? LastSpeechSeconds(byte[] wavBytes)
        {
            try
            {
                using var ms = new MemoryStream(wavBytes, writable: false);
                using var reader = new WaveFileReader(ms);
                var fmt = reader.WaveFormat;
                if (fmt.Encoding != WaveFormatEncoding.Pcm || fmt.BitsPerSample != 16 || fmt.Channels < 1) return null;

                int samplesPerFrame = Math.Max(1, fmt.SampleRate * FrameMs / 1000);
                int bytesPerFrame = samplesPerFrame * fmt.Channels * 2;
                var buffer = new byte[bytesPerFrame];
                double threshold = SpeechFrameRms * SpeechFrameRms;

                int frame = 0, run = 0;
                int lastSpeechFrameEnd = -1;
                int read;
                while ((read = ReadFull(reader, buffer)) > 0)
                {
                    double sum = 0;
                    int n = 0;
                    for (int i = 0; i + 1 < read; i += 2)
                    {
                        double s = (short)(buffer[i] | (buffer[i + 1] << 8)) / (double)short.MaxValue;
                        sum += s * s;
                        n++;
                    }
                    frame++;
                    if (n > 0 && sum / n >= threshold)
                    {
                        if (++run >= MinSpeechFrames) lastSpeechFrameEnd = frame;
                    }
                    else run = 0;
                }
                return lastSpeechFrameEnd < 0 ? null : lastSpeechFrameEnd * (double)FrameMs / 1000.0;
            }
            catch { return null; }
        }

        private static int ReadFull(WaveFileReader reader, byte[] buffer)
        {
            int total = 0, r;
            while (total < buffer.Length && (r = reader.Read(buffer, total, buffer.Length - total)) > 0)
                total += r;
            return total;
        }
    }
}
