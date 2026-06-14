using System;
using System.Collections.Generic;
using System.Net.Http;

namespace WhisperInk
{
    /// <summary>
    /// Owns one <see cref="ITranscriber"/> instance per provider id, lazily
    /// constructed on first use. Local servers stay resident (so we don't
    /// pay the 2-GB model-load cost on every dictation); switching providers
    /// drops the outgoing instance to free its memory.
    ///
    /// Anything that mutates the <see cref="ApiProvider"/> config (port, key,
    /// GGUF glob, GPU backend) must call <see cref="DropAll"/> so the next
    /// dictation re-creates against the new values. Live-edited fields aren't
    /// tracked individually — the dialog save path always calls DropAll.
    /// </summary>
    public sealed class TranscriberFactory : IDisposable
    {
        private readonly HttpClient _http;
        private readonly Func<string> _resolveGlobalGpuBackend;
        private readonly Action<string> _log;
        private readonly Dictionary<string, ITranscriber> _byProviderId = new(StringComparer.Ordinal);
        private bool _disposed;

        public TranscriberFactory(
            HttpClient http,
            Func<string> resolveGlobalGpuBackend,
            Action<string> log)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _resolveGlobalGpuBackend = resolveGlobalGpuBackend ?? (() => "auto");
            _log = log ?? (_ => { });
        }

        public ITranscriber GetOrCreate(ApiProvider provider)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TranscriberFactory));
            if (provider == null) throw new ArgumentNullException(nameof(provider));

            if (_byProviderId.TryGetValue(provider.Id, out var existing)) return existing;

            var fresh = Create(provider);
            _byProviderId[provider.Id] = fresh;
            return fresh;
        }

        private ITranscriber Create(ApiProvider provider) => provider.TranscriberKind switch
        {
            TranscriberKind.LocalCrispAsrServer => new CrispAsrServerTranscriber(provider, _resolveGlobalGpuBackend, _log),
            TranscriberKind.GoogleChirp3        => new GoogleChirp3Transcriber(provider, _log),
            TranscriberKind.Soniox              => new SonioxTranscriber(provider, _http, _log),
            TranscriberKind.Deepgram            => new DeepgramTranscriber(provider, _http, _log),
            _                                   => new HttpTranscriber(provider, _http, _log),
        };

        /// <summary>Drop a single provider's cached transcriber — e.g. when
        /// switching away so we don't keep its model resident.</summary>
        public void Drop(string? providerId)
        {
            if (string.IsNullOrEmpty(providerId)) return;
            if (_byProviderId.Remove(providerId, out var existing))
            {
                try { existing.Dispose(); }
                catch (Exception ex) { _log($"TranscriberFactory.Drop({providerId}) dispose failed: {ex.Message}"); }
            }
        }

        /// <summary>Drop every cached transcriber. Call after settings save
        /// when any provider's config might have been edited.</summary>
        public void DropAll()
        {
            foreach (var kv in _byProviderId)
            {
                try { kv.Value.Dispose(); }
                catch (Exception ex) { _log($"TranscriberFactory.DropAll dispose of {kv.Key} failed: {ex.Message}"); }
            }
            _byProviderId.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DropAll();
        }
    }
}
