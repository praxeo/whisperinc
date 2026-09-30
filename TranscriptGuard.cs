using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WhisperInk
{
    /// <summary>
    /// Catches a transcript that is the bias list itself. A speech-LLM given audio
    /// with no speech in it (a cough, a throat clear, a piece of silence) can answer
    /// with its own prompt: the whole Context Bias list, in the order it was sent
    /// (measured 2026-09-28: Qwen3-ASR with the list, 3 of 3 non-speech takes; the
    /// same class of failure as the recital CrispASR#471 describes for silent slices).
    /// That text must never be pasted as a note, so it is judged on what a recital has
    /// and real speech doesn't: several list terms DIRECTLY ADJACENT, in the LIST'S OWN
    /// ORDER (a term or two may be missing). A note that merely names list terms
    /// ("denies hematemesis, melena, or hematochezia") has words between them and is in
    /// the wrong order, so it never trips this.
    ///
    ///   ListRecital  a run of MinRun or more terms that makes up RecitalShare or more of
    ///                the text: the take had no speech in it. Nothing is pasted.
    ///   ListInText   such a run inside other text (a recital appended to a real
    ///                dictation, the failure CrispASR's slicer used to cause): delivered,
    ///                with a warning to check it.
    ///
    /// Fails open: a list shorter than MinRun terms can't be told from speech, and empty
    /// or missing text is never a verdict. It never edits the transcript.
    /// </summary>
    public static class TranscriptGuard
    {
        public enum Kind { None, ListRecital, ListInText }

        /// <param name="Terms">how many list terms the run holds</param>
        /// <param name="Share">the fraction of the text's words those terms account for</param>
        public readonly record struct Verdict(Kind Kind, int Terms, double Share);

        /// <summary>Terms in a row, in list order, before it counts as a recital.</summary>
        public const int MinRun = 4;

        /// <summary>The run's share of the text's words at which the text is the recital.</summary>
        public const double RecitalShare = 0.6;

        /// <summary>How far ahead in the list the next recited term may be: 2 lets a
        /// recital miss one term (a mis-heard word) and still be one.</summary>
        private const int MaxSkip = 2;

        private static readonly Regex WordRx = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

        private static List<string> Words(string s) =>
            WordRx.Matches(s.ToLowerInvariant()).Select(m => m.Value).ToList();

        public static Verdict Inspect(string? text, IReadOnlyList<string>? terms)
        {
            if (string.IsNullOrWhiteSpace(text) || terms == null) return default;

            // The list as it was sent: distinct terms, in order, as word sequences.
            var list = new List<string[]>();
            var seen = new HashSet<string>();
            foreach (string t in terms)
            {
                var w = Words(t ?? "");
                if (w.Count > 0 && seen.Add(string.Join(' ', w))) list.Add(w.ToArray());
            }
            if (list.Count < MinRun) return default;

            var words = Words(text);
            if (words.Count < MinRun) return default;

            var byFirst = new Dictionary<string, List<int>>();
            for (int i = 0; i < list.Count; i++)
            {
                if (!byFirst.TryGetValue(list[i][0], out var at)) byFirst[list[i][0]] = at = new List<int>();
                at.Add(i);
            }

            bool MatchAt(int p, int k)
            {
                var t = list[k];
                if (p + t.Length > words.Count) return false;
                for (int j = 0; j < t.Length; j++) if (words[p + j] != t[j]) return false;
                return true;
            }

            int bestTerms = 0, bestWords = 0;
            for (int p = 0; p < words.Count; p++)
            {
                if (!byFirst.TryGetValue(words[p], out var starts)) continue;
                foreach (int k0 in starts)
                {
                    if (!MatchAt(p, k0)) continue;
                    int q = p + list[k0].Length, k = k0, n = 1, covered = list[k0].Length;
                    while (true)
                    {
                        int next = -1;
                        for (int k2 = k + 1; k2 <= k + MaxSkip && k2 < list.Count; k2++)
                            if (MatchAt(q, k2)) { next = k2; break; }
                        if (next < 0) break;
                        q += list[next].Length; covered += list[next].Length; n++; k = next;
                    }
                    if (n > bestTerms || (n == bestTerms && covered > bestWords)) { bestTerms = n; bestWords = covered; }
                }
            }

            if (bestTerms < MinRun) return default;
            double share = (double)bestWords / words.Count;
            return new Verdict(share >= RecitalShare ? Kind.ListRecital : Kind.ListInText, bestTerms, share);
        }
    }
}
