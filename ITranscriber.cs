using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperInk
{
    /// <summary>
    /// Common surface every batch-transcription backend exposes — cloud HTTP,
    /// local CrispASR server, Google Chirp 3, Soniox, Deepgram, Modulate,
    /// Smallest.ai, Reson8. The dispatch site
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

    /// <summary>
    /// Optional, for transcribers whose provider returns word timestamps:
    /// how far into the audio the most recent transcript reached, for the
    /// incomplete-transcript check (<see cref="TranscriptCoverage"/>). Both
    /// values describe the LAST <see cref="ITranscriber.TranscribeAsync"/>
    /// call and are null when the provider didn't report them. Dictations
    /// are serialized by MainWindow's recording state machine, so "the last
    /// call" is always the take in hand.
    /// </summary>
    public interface ITranscriptCoverage
    {
        /// <summary>End time (s) of the last transcribed word.</summary>
        double? LastWordEndSeconds { get; }

        /// <summary>Seconds of audio the service says it decoded.</summary>
        double? DecodedAudioSeconds { get; }
    }
}
