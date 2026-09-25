using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace WhisperInk
{
    /// <summary>
    /// Cuts a long take at its own pauses before a local CrispASR server sees
    /// it, so that no piece the model transcribes is silence.
    ///
    /// The server transcribes a take longer than its chunk_seconds in pieces,
    /// cutting each at the quietest 100 ms in the last 5 s before the mark.
    /// When a take runs past a mark after the speech has stopped, that
    /// quietest spot is the silence after the last word, and the last piece is
    /// silence alone. Qwen3 answers a silent piece, even 0.3 s of one, with its
    /// whole prompt: the bias list, pasted after the dictation. With no list it
    /// invents a sentence instead. Measured 2026-09-24 (CLAUDE.md 4.3).
    ///
    /// So a preset that sets chunk_seconds has its longer takes cut here:
    ///   - every piece is shorter than chunk_seconds, so the server never cuts
    ///     one again;
    ///   - every piece holds speech;
    ///   - every cut falls in the middle of a pause, so no word is split: the
    ///     latest pause of 300 ms or more in the 25 s before the limit, which
    ///     keeps pieces long. A sentence at the start of a piece loses the ones
    ///     before it, and Qwen3 needs them: cutting at the longest pause
    ///     instead started every piece of a test dictation with the same
    ///     sentence, and ureterolithiasis came back 1-2 times in 3-7 where the
    ///     server's own cuts got 7/7 (2026-09-25).
    /// Silence after the last speech stays in the last piece while it fits and
    /// is left out past that; so is a stretch with no speech in it at all (a
    /// pause longer than a piece). "Speech" is the silence gate's own test
    /// (SpeechDetector): 90 ms runs clear of the take's noise floor.
    /// </summary>
    public static class LocalTakeSplitter
    {
        /// <summary>How far before a piece's limit a cut may fall, looking for a pause.</summary>
        public static readonly TimeSpan SearchWindow = TimeSpan.FromSeconds(25);

        /// <summary>A pause at least this long is a place to cut; the latest one wins.</summary>
        public static readonly TimeSpan MinPause = TimeSpan.FromMilliseconds(300);

        /// <summary>A stretch of the take, in samples.</summary>
        public readonly record struct Piece(int Start, int End)
        {
            public int Length => End - Start;
        }

        /// <summary>How a take was cut. Pieces are in order and each holds
        /// speech; Wavs are the same pieces as 16-bit mono WAVs.
        /// TrailingSilence is the silence after the last speech that didn't fit
        /// in the last piece, in samples; SilentStretches counts stretches with
        /// no speech at all. Both are left out.</summary>
        public sealed record Split(int SampleRate, int TotalSamples, IReadOnlyList<Piece> Pieces,
                                   IReadOnlyList<byte[]> Wavs, int TrailingSilence, int SilentStretches)
        {
            public double Seconds(int samples) => samples / (double)SampleRate;
        }

        /// <summary>The pieces a take should go out in, none longer than
        /// maxPiece. Null means send it whole: it fits in one piece, it isn't
        /// 16-bit mono PCM, or nothing in it is speech, so there's no pause to
        /// cut at.</summary>
        public static Split? Cut(byte[] wav, TimeSpan maxPiece)
        {
            short[] pcm;
            int rate;
            try
            {
                using var ms = new MemoryStream(wav, writable: false);
                using var reader = new WaveFileReader(ms);
                var fmt = reader.WaveFormat;
                if (fmt.Encoding != WaveFormatEncoding.Pcm || fmt.BitsPerSample != 16 || fmt.Channels != 1)
                    return null;
                rate = fmt.SampleRate;
                var bytes = new byte[reader.Length];
                int got = 0, n;
                while (got < bytes.Length && (n = reader.Read(bytes, got, bytes.Length - got)) > 0)
                    got += n;
                pcm = new short[got / 2];
                Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 2);
            }
            catch { return null; }

            int maxSamples = (int)(maxPiece.TotalSeconds * rate);
            int spf = Math.Max(1, rate * SpeechDetector.FrameMs / 1000);
            int maxFrames = maxSamples / spf;
            if (pcm.Length <= maxSamples || maxFrames < 2) return null;

            // 30 ms frames, marked as speech by the silence gate's test.
            int frames = (pcm.Length + spf - 1) / spf;
            var rms = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                int s = f * spf, e = Math.Min(pcm.Length, s + spf);
                double sum = 0;
                for (int i = s; i < e; i++) { double v = pcm[i] / (double)short.MaxValue; sum += v * v; }
                rms[f] = Math.Sqrt(sum / (e - s));
            }
            var sorted = (double[])rms.Clone();
            Array.Sort(sorted);
            double floor = sorted[Math.Min(frames - 1, (int)(SpeechDetector.FloorPercentile * frames))];
            double threshold = Math.Max(SpeechDetector.MinSpeechFrameRms, SpeechDetector.OverFloor * floor);
            var speech = new bool[frames];
            for (int f = 0; f < frames;)
            {
                if (rms[f] < threshold) { f++; continue; }
                int g = f;
                while (g < frames && rms[g] >= threshold) g++;
                if (g - f >= SpeechDetector.MinRunFrames)
                    for (int k = f; k < g; k++) speech[k] = true;
                f = g;
            }
            int lastSpeech = Array.LastIndexOf(speech, true);
            if (lastSpeech < 0) return null;

            int window = Math.Min(maxFrames - 1, (int)(SearchWindow.TotalMilliseconds / SpeechDetector.FrameMs));
            int minPause = Math.Max(1, (int)(MinPause.TotalMilliseconds / SpeechDetector.FrameMs));
            var pieces = new List<Piece>();
            int start = 0, trailing = 0, silent = 0;
            void Add(int from, int to)
            {
                if (Array.IndexOf(speech, true, from, to - from) >= 0)
                    pieces.Add(new Piece(from * spf, Math.Min(pcm.Length, to * spf)));
                else
                    silent++;
            }
            while (true)
            {
                if (frames - start <= maxFrames)
                {
                    Add(start, frames);
                    break;
                }
                if (lastSpeech < start + maxFrames)
                {
                    // The rest of the speech fits: fill the piece, leave out
                    // the silence beyond it.
                    Add(start, start + maxFrames);
                    trailing = pcm.Length - (start + maxFrames) * spf;
                    break;
                }
                // Speech runs past the limit: cut in the middle of the latest
                // pause of MinPause or more before it; failing that, the
                // longest pause in the window.
                int lo = start + maxFrames - window, hi = start + maxFrames;
                int cut = -1, longest = 0, longestMid = -1;
                for (int f = lo; f < hi;)
                {
                    if (speech[f]) { f++; continue; }
                    int g = f;
                    while (g < hi && !speech[g]) g++;
                    if (g - f >= minPause) cut = f + (g - f) / 2;
                    if (g - f > longest) { longest = g - f; longestMid = f + (g - f) / 2; }
                    f = g;
                }
                if (cut < 0) cut = longestMid;
                if (cut < 0)
                {
                    // 25 s without a single pause: the quietest frame.
                    cut = lo;
                    for (int f = lo + 1; f < hi; f++)
                        if (rms[f] < rms[cut]) cut = f;
                }
                Add(start, cut);
                start = cut;
            }

            var wavs = new List<byte[]>(pieces.Count);
            foreach (var p in pieces) wavs.Add(ToWav(pcm, p, rate));
            return new Split(rate, pcm.Length, pieces, wavs, trailing, silent);
        }

        private static byte[] ToWav(short[] pcm, Piece piece, int rate)
        {
            int bytes = piece.Length * 2;
            var wav = new byte[44 + bytes];
            using (var bw = new BinaryWriter(new MemoryStream(wav)))
            {
                bw.Write("RIFF"u8); bw.Write(36 + bytes); bw.Write("WAVE"u8);
                bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
                bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
                bw.Write("data"u8); bw.Write(bytes);
            }
            Buffer.BlockCopy(pcm, piece.Start * 2, wav, 44, bytes);
            return wav;
        }
    }
}
