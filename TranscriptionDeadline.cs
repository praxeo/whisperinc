using System;

namespace WhisperInk
{
    /// <summary>
    /// How long one dictation's transcription may take before it is declared
    /// failed — scaled to that recording, never a flat number.
    ///
    /// A flat deadline is a length limit in disguise. The old shared 15 s
    /// HttpClient timeout failed any cloud take whose upload + inference ran
    /// past 15 s, which depends only on how long you talked (the WAV is
    /// 1.8 MB per minute) and how fast the uplink is — and the same shape of
    /// bug made long dictations "time out" in the elevenlabs-web app until
    /// its deadline was made duration-aware. Likewise the 120 s CrispASR
    /// client and Soniox's 120 s poll ceiling capped local and async takes.
    ///
    ///   Cloud: 20 s + 20 s per minute of audio, capped at 15 min. Measured
    ///     on the desktop, ElevenLabs takes ~0.45 s + 1.1 s per minute of
    ///     audio, and a 2 Mbps uplink adds ~7.5 s per minute of WAV — so
    ///     20 s/min leaves roughly 2x margin even on a poor connection.
    ///   Local (CrispASR, or any provider whose endpoint is loopback): 180 s +
    ///     1x the audio length, capped at 2 h. The floor covers a cold server
    ///     start (its /health wait alone may take 120 s); the per-audio term
    ///     assumes only that the model runs at least in real time (measured
    ///     RTFx here is 3-9x on CUDA). The cap exists only so the backstop
    ///     below can be longer than every deadline; no dictation gets near it.
    ///
    /// Everything still ends: a genuinely hung request fails loudly when its
    /// deadline passes, with the recording kept for a retry (UnsentTakes).
    /// </summary>
    public static class TranscriptionDeadline
    {
        public static readonly TimeSpan CloudFloor = TimeSpan.FromSeconds(20);
        public static readonly TimeSpan CloudPerMinute = TimeSpan.FromSeconds(20);
        public static readonly TimeSpan CloudCap = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan LocalFloor = TimeSpan.FromSeconds(180);
        public static readonly TimeSpan LocalCap = TimeSpan.FromHours(2);

        /// <summary>Timeout for the HttpClients themselves (the shared cloud
        /// client in MainWindow and CrispASR's). A backstop only: longer than
        /// any deadline above — the shared client also carries loopback
        /// providers, which get the LOCAL budget — so the per-take token is
        /// what actually governs, while a request that somehow carries no
        /// token still ends.</summary>
        public static readonly TimeSpan HttpBackstop = LocalCap + TimeSpan.FromMinutes(10);

        public static bool IsLocal(ApiProvider provider)
        {
            if (provider.TranscriberKind == TranscriberKind.LocalCrispAsrServer) return true;
            // User-managed local servers (an Http provider pointed at
            // localhost) are the same speed class as CrispASR, so they get
            // the local budget, not the cloud one.
            return Uri.TryCreate(provider.ResolvedTranscriptionUrl, UriKind.Absolute, out var u) && u.IsLoopback;
        }

        public static TimeSpan For(ApiProvider provider, double audioSeconds)
        {
            // Clamped before any TimeSpan math: both results are capped well
            // below this anyway, and an absurd duration (a corrupt header)
            // must not overflow TimeSpan and throw on the dictation path.
            double s = double.IsFinite(audioSeconds) ? Math.Clamp(audioSeconds, 0, LocalCap.TotalSeconds) : 0;
            if (IsLocal(provider))
            {
                var local = LocalFloor + TimeSpan.FromSeconds(s);
                return local < LocalCap ? local : LocalCap;
            }

            var cloud = CloudFloor + TimeSpan.FromSeconds(CloudPerMinute.TotalSeconds * s / 60.0);
            return cloud < CloudCap ? cloud : CloudCap;
        }
    }
}
