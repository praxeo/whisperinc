using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperInk
{
    /// <summary>
    /// Common surface every batch-transcription backend exposes — cloud HTTP,
    /// local CrispASR server, Google Chirp 3, Soniox, Deepgram. The dispatch site
    /// in MainWindow doesn't care which backend handled the audio; it just feeds
    /// WAV bytes in and gets text back.
    ///
    /// Implementations are constructed once per <see cref="ApiProvider"/> by
    /// <see cref="TranscriberFactory"/> and live until that provider's config
    /// changes (dropped on settings save) or the app shuts down.
    /// </summary>
    public interface ITranscriber : IDisposable
    {
        /// <summary>Human-readable name for log lines and error UI.</summary>
        string DisplayName { get; }

        /// <summary>
        /// True when the transcriber has everything it needs to run a call.
        /// For local models this checks that the GGUF files and exe
        /// exist; for cloud providers it usually returns true (errors surface
        /// at request time). When false, <paramref name="diagnostic"/> contains
        /// a human-readable reason suitable for the setup banner.
        /// </summary>
        bool IsReady(out string? diagnostic);

        /// <summary>
        /// Transcribe a single utterance. <paramref name="wavBytes"/> is a
        /// complete WAV file in memory (header + PCM). <paramref name="biasTerms"/>
        /// is the global <c>ContextBiasTerms</c> list — each transcriber routes it
        /// to its provider's native field per <c>ApiProvider.ResolvedBiasMechanism</c>.
        /// Returns the transcript on success, or null on any failure (failure
        /// is logged via the action passed at construction).
        /// </summary>
        Task<string?> TranscribeAsync(
            byte[] wavBytes,
            IReadOnlyList<string> biasTerms,
            CancellationToken ct = default);
    }
}
