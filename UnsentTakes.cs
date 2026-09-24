using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace WhisperInk
{
    /// <summary>
    /// Every dictation's audio, kept on disk until its text has been
    /// delivered — so no failure can cost a dictation.
    ///
    /// Before this, the only copy of a take outside memory was
    /// MyRecordings\temp_audio.wav, overwritten by the very next press: a
    /// timeout, a provider outage or a crash mid-transcription meant saying
    /// the whole note again from memory. Now each take is journaled here
    /// BEFORE it is sent (WAV + a small JSON sidecar), deleted once its text
    /// is pasted, and otherwise kept with the reason it failed. A take still
    /// marked "pending" at startup is one the app never finished — it was
    /// closed, crashed, or the PC restarted mid-take — and is recovered as
    /// such. Kept takes can be retried from the menu (↻ Unsent dictations),
    /// with the active provider or, when the cloud is down, a local model.
    ///
    /// Lives under %APPDATA%\.WhisperInk (not OneDrive-synced like
    /// MyRecordings) because these are clinical recordings. Retention is
    /// bounded — 14 days, 50 takes — so a forgotten failure can't grow
    /// without limit.
    ///
    /// Disk writes run off the dictation path; each take's later
    /// delete/update is chained after its own write, so the two can't race.
    /// </summary>
    public sealed class UnsentTakes
    {
        public const string Pending = "pending";
        public const string Failed = "failed";
        public const string Incomplete = "incomplete";
        public const string Interrupted = "interrupted";
        // Judged silent and never sent. Kept (the newest few) because that
        // judgement was once wrong for real, quiet speech — and a take the
        // gate drops is otherwise gone. Listed apart from real failures.
        public const string Quiet = "quiet";

        public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
        public const int MaxCount = 50;
        public const int MaxQuietCount = 5;

        public sealed class Take
        {
            public string Id { get; set; } = "";
            public DateTime RecordedAt { get; set; }
            public string ProviderId { get; set; } = "";
            public string ProviderName { get; set; } = "";
            public double AudioSeconds { get; set; }
            public string Status { get; set; } = Pending;
            public string Reason { get; set; } = "";

            [JsonIgnore] public string WavPath { get; set; } = "";
            [JsonIgnore] internal Task Saved { get; set; } = Task.CompletedTask;
            // Set the moment Delivered/Keep is called (their disk work runs
            // later), so an error path can tell whether this take still needs
            // an outcome.
            [JsonIgnore] public bool Resolved { get; internal set; }

            [JsonIgnore]
            public string Label =>
                $"{RecordedAt.ToString("MMM d HH:mm", CultureInfo.InvariantCulture)} · {AudioSeconds:F0}s · {Reason}";
        }

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly Action<string> _log;
        private readonly object _gate = new();
        private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

        public string Folder { get; }

        public UnsentTakes(string folder, Action<string>? log = null)
        {
            Folder = folder;
            _log = log ?? (_ => { });
        }

        /// <summary>Journals a take that is about to be transcribed. Returns
        /// at once; the write happens on the thread pool.</summary>
        public Take Begin(byte[] wav, ApiProvider? provider, double audioSeconds)
        {
            var now = DateTime.Now;
            var take = new Take
            {
                Id = "take-" + now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture),
                RecordedAt = now,
                ProviderId = provider?.Id ?? "",
                ProviderName = provider?.Name ?? "",
                AudioSeconds = audioSeconds,
                Status = Pending,
                Reason = "in flight",
            };
            take.WavPath = Path.Combine(Folder, take.Id + ".wav");
            lock (_gate) _inFlight.Add(take.Id);
            take.Saved = Task.Run(() =>
            {
                lock (_gate)
                {
                    try
                    {
                        Directory.CreateDirectory(Folder);
                        // Audio first: a crash between the two writes then
                        // leaves a recoverable WAV, never a sidecar with no audio.
                        File.WriteAllBytes(take.WavPath, wav);
                        WriteMeta(take);
                    }
                    catch (Exception ex) { _log($"[unsent] journal write failed for {take.Id}: {ex.GetType().Name}: {ex.Message}"); }
                }
            });
            return take;
        }

        /// <summary>The take's text was delivered: its audio is no longer needed.</summary>
        public void Delivered(Take take)
        {
            take.Resolved = true;
            take.Saved.ContinueWith(_ =>
            {
                lock (_gate)
                {
                    _inFlight.Remove(take.Id);
                    DeleteFiles(take.Id);
                }
            }, TaskScheduler.Default);
        }

        /// <summary>The take failed (or came back incomplete): keep its audio
        /// for a retry, with the reason shown in the menu.</summary>
        public void Keep(Take take, string status, string reason)
        {
            take.Resolved = true;
            take.Saved.ContinueWith(_ =>
            {
                lock (_gate)
                {
                    _inFlight.Remove(take.Id);
                    take.Status = status;
                    take.Reason = reason;
                    try
                    {
                        if (File.Exists(take.WavPath)) WriteMeta(take);
                        _log($"[unsent] kept {take.Id} ({take.AudioSeconds:F1}s, {reason}) for a retry: {take.WavPath}");
                    }
                    catch (Exception ex) { _log($"[unsent] could not update {take.Id}: {ex.GetType().Name}: {ex.Message}"); }
                    PruneLocked();
                }
            }, TaskScheduler.Default);
        }

        /// <summary>Keeps a take the silence gate dropped without sending,
        /// so a misjudged one can still be retried. Only the newest
        /// <see cref="MaxQuietCount"/> are kept.</summary>
        public Take KeepQuiet(byte[] wav, ApiProvider? provider, double audioSeconds, string reason)
        {
            var take = Begin(wav, provider, audioSeconds);
            Keep(take, Quiet, reason);
            return take;
        }

        /// <summary>Kept takes, newest first. Takes still being transcribed
        /// are not listed.</summary>
        public List<Take> List()
        {
            lock (_gate)
            {
                var takes = new List<Take>();
                if (!Directory.Exists(Folder)) return takes;
                foreach (var wav in Directory.EnumerateFiles(Folder, "take-*.wav"))
                {
                    string id = Path.GetFileNameWithoutExtension(wav);
                    if (_inFlight.Contains(id)) continue;
                    takes.Add(ReadMeta(id, wav));
                }
                return takes.OrderByDescending(t => t.RecordedAt).ToList();
            }
        }

        public byte[]? ReadAudio(Take take)
        {
            try { return File.ReadAllBytes(take.WavPath); }
            catch (Exception ex)
            {
                _log($"[unsent] could not read {take.WavPath}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>A retry delivered this take: forget it.</summary>
        public void Remove(Take take)
        {
            lock (_gate) DeleteFiles(take.Id);
        }

        /// <summary>Startup: a take still marked pending was never finished
        /// (the app closed, crashed or the PC restarted mid-take). Marks those
        /// as interrupted, applies retention, and returns how many takes are
        /// waiting (not counting the ones judged silent).</summary>
        public int Recover()
        {
            lock (_gate)
            {
                if (!Directory.Exists(Folder)) return 0;
                int interrupted = 0;
                foreach (var wav in Directory.EnumerateFiles(Folder, "take-*.wav").ToList())
                {
                    string id = Path.GetFileNameWithoutExtension(wav);
                    if (_inFlight.Contains(id)) continue;
                    var take = ReadMeta(id, wav);
                    if (take.Status != Pending) continue;
                    take.Status = Interrupted;
                    take.Reason = "WhisperInk stopped mid-take";
                    try { WriteMeta(take); interrupted++; } catch { }
                }
                if (interrupted > 0)
                    _log($"[unsent] {interrupted} take(s) were interrupted mid-transcription last session — kept for a retry");
                PruneLocked();
                return Directory.EnumerateFiles(Folder, "take-*.wav")
                    .Count(w => ReadMeta(Path.GetFileNameWithoutExtension(w), w).Status != Quiet);
            }
        }

        private void PruneLocked()
        {
            try
            {
                if (!Directory.Exists(Folder)) return;
                var takes = Directory.EnumerateFiles(Folder, "take-*.wav")
                    .Select(w => ReadMeta(Path.GetFileNameWithoutExtension(w), w))
                    .Where(t => !_inFlight.Contains(t.Id))
                    .OrderByDescending(t => t.RecordedAt)
                    .ToList();
                // Takes judged silent have their own small allowance, so a run
                // of them can never push a real failure out of the list.
                var cutoff = DateTime.Now - MaxAge;
                int kept = 0, keptQuiet = 0;
                foreach (var t in takes)
                {
                    bool keep = t.RecordedAt >= cutoff &&
                                (t.Status == Quiet ? keptQuiet++ < MaxQuietCount : kept++ < MaxCount);
                    if (keep) continue;
                    _log($"[unsent] retention: removing {t.Id} ({t.RecordedAt:yyyy-MM-dd HH:mm}, {t.Reason})");
                    DeleteFiles(t.Id);
                }
                // A sidecar whose audio is gone has nothing left to retry.
                foreach (var json in Directory.EnumerateFiles(Folder, "take-*.json"))
                    if (!File.Exists(Path.ChangeExtension(json, ".wav")) &&
                        !_inFlight.Contains(Path.GetFileNameWithoutExtension(json)))
                        File.Delete(json);
            }
            catch (Exception ex) { _log($"[unsent] retention pass failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        private void WriteMeta(Take take) =>
            File.WriteAllText(Path.Combine(Folder, take.Id + ".json"), JsonSerializer.Serialize(take, JsonOptions));

        /// <summary>Reads a take's sidecar. A WAV without one (a crash landed
        /// between the two writes) is still a take: it comes back as
        /// interrupted, dated by the file.</summary>
        private Take ReadMeta(string id, string wavPath)
        {
            Take? take = null;
            try
            {
                string json = Path.Combine(Folder, id + ".json");
                if (File.Exists(json)) take = JsonSerializer.Deserialize<Take>(File.ReadAllText(json));
            }
            catch { }
            take ??= new Take
            {
                Id = id,
                RecordedAt = File.GetLastWriteTime(wavPath),
                Status = Interrupted,
                Reason = "WhisperInk stopped mid-take",
            };
            take.Id = id;
            take.WavPath = wavPath;
            return take;
        }

        private void DeleteFiles(string id)
        {
            foreach (var ext in new[] { ".wav", ".json" })
            {
                string path = Path.Combine(Folder, id + ext);
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception ex) { _log($"[unsent] could not delete {path}: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
    }
}
