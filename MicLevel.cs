// MicLevel.cs
// The microphone's input level ("gain"): the same setting as the input-volume
// slider in Windows' Sound settings, read and set from the 🎙 Microphone menu.
//
// It changes the device's own gain through Core Audio
// (IAudioEndpointVolume), not a multiplier in WhisperInk. A multiplier would
// turn the room noise up exactly as much as the voice, so it can't make a take
// clearer, and it would fight the Windows slider. Windows keeps the setting
// per device, so WhisperInk stores nothing.
//
// WhisperInk records through winmm (WaveIn), which numbers devices; Core Audio
// names them by endpoint id. The driver answers which endpoint a winmm device
// is (DRV_QUERYFUNCTIONINSTANCEID); when it can't, the names are matched, which
// works because winmm's name is the endpoint's own, cut at 31 characters.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace WhisperInk
{
    /// <summary>One read of a microphone's input level.</summary>
    public sealed record MicLevelInfo(string EndpointName, float Scalar, float Db, float MinDb, float MaxDb, bool Muted)
    {
        /// <summary>Windows' own percent, as its slider shows it.</summary>
        public int Percent => (int)Math.Round(Scalar * 100f);
        public bool AtMax => Scalar >= 0.995f;
    }

    public static class MicLevel
    {
        /// <summary>The levels the menu offers, in Windows' percent. Finer near
        /// the top, where a gooseneck mic usually sits: on the desktop's VEC
        /// 5 % is about 3 dB up there.</summary>
        public static readonly int[] Steps = { 100, 95, 90, 85, 80, 75, 70, 60, 50, 40 };

        public static bool IsCurrent(int stepPercent, float scalar) => Math.Abs(scalar * 100f - stepPercent) < 0.5f;

        public static string Describe(MicLevelInfo i) => $"{i.Percent}% ({i.Db:+0.0;-0.0;0.0} dB)";

        /// <summary>winmm cuts device names to 31 characters (MAXPNAMELEN 32).</summary>
        public const int WaveInNameLimit = 31;

        /// <summary>Which of the capture endpoints a winmm device name belongs
        /// to: the one with that name, or, for a name winmm cut short, the one
        /// whose name starts with it. -1 when none or more than one fit, so an
        /// ambiguous name never sets the wrong mic.</summary>
        public static int MatchByName(string waveInName, IReadOnlyList<string> endpointNames)
        {
            if (string.IsNullOrEmpty(waveInName)) return -1;
            var exact = Enumerable.Range(0, endpointNames.Count)
                                  .Where(i => string.Equals(endpointNames[i], waveInName, StringComparison.Ordinal)).ToList();
            if (exact.Count == 1) return exact[0];
            if (exact.Count > 1) return -1;
            if (waveInName.Length < WaveInNameLimit) return -1;
            var prefix = Enumerable.Range(0, endpointNames.Count)
                                   .Where(i => endpointNames[i].StartsWith(waveInName, StringComparison.Ordinal)).ToList();
            return prefix.Count == 1 ? prefix[0] : -1;
        }

        /// <summary>The level of the mic WhisperInk records from, or null when
        /// Windows can't say (no such device, or Core Audio failed; logged).</summary>
        public static MicLevelInfo? Read(int waveInIndex, string waveInName, Action<string>? log = null)
            => With(waveInIndex, waveInName, log, "read", _ => { });

        /// <summary>Sets the level in Windows' percent and returns the level read
        /// back, or null on failure (logged).</summary>
        public static MicLevelInfo? Set(int waveInIndex, string waveInName, int percent, Action<string>? log = null)
            => With(waveInIndex, waveInName, log, "set",
                    v => v.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f);

        public static MicLevelInfo? Unmute(int waveInIndex, string waveInName, Action<string>? log = null)
            => With(waveInIndex, waveInName, log, "unmute", v => v.Mute = false);

        private static MicLevelInfo? With(int waveInIndex, string waveInName, Action<string>? log, string what,
                                          Action<AudioEndpointVolume> act)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                var (dev, name) = Find(en, waveInIndex, waveInName);
                using var _ = dev;
                if (dev == null)
                {
                    log?.Invoke($"[mic] couldn't {what} the input level: Windows lists no capture endpoint for device [{waveInIndex}] {waveInName}");
                    return null;
                }
                var v = dev.AudioEndpointVolume;
                act(v);
                var r = v.VolumeRange;
                return new MicLevelInfo(name, v.MasterVolumeLevelScalar, v.MasterVolumeLevel,
                                        r.MinDecibels, r.MaxDecibels, v.Mute);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[mic] couldn't {what} the input level of [{waveInIndex}] {waveInName}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        // FriendlyName opens the endpoint's property store, which took 58 ms a
        // read on the desktop's USB mic (everything else together: about 1 ms
        // warm). The menu is built on the UI thread, where the keyboard hook
        // runs, so the endpoint found by id is named with winmm's name instead.
        // The name fallback has to read every endpoint's name; it's the rare path.
        private static (MMDevice? Device, string Name) Find(MMDeviceEnumerator en, int waveInIndex, string waveInName)
        {
            string? id = EndpointIdOf(waveInIndex);
            if (id != null)
            {
                try
                {
                    var d = en.GetDevice(id);
                    if (d.State == DeviceState.Active) return (d, waveInName);
                    d.Dispose();
                }
                catch (COMException) { /* not an endpoint id after all: fall back to the name */ }
            }
            var all = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
            var names = all.Select(d => d.FriendlyName).ToList();
            int pick = MatchByName(waveInName, names);
            for (int i = 0; i < all.Count; i++) if (i != pick) all[i].Dispose();
            return pick >= 0 ? (all[pick], names[pick]) : (null, waveInName);
        }

        // ── winmm: which endpoint a waveIn device number is ─────────────────
        [DllImport("winmm.dll")]
        private static extern int waveInMessage(IntPtr hwi, int uMsg, IntPtr dw1, IntPtr dw2);
        private const int DRV_RESERVED = 0x0800;
        private const int DRV_QUERYFUNCTIONINSTANCEID = DRV_RESERVED + 17;
        private const int DRV_QUERYFUNCTIONINSTANCEIDSIZE = DRV_RESERVED + 18;

        /// <summary>The Core Audio endpoint id of a winmm capture device, or
        /// null if the driver won't say.</summary>
        public static string? EndpointIdOf(int waveInIndex)
        {
            if (waveInIndex < 0) return null;
            IntPtr size = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(size, 0);
                if (waveInMessage((IntPtr)waveInIndex, DRV_QUERYFUNCTIONINSTANCEIDSIZE, size, IntPtr.Zero) != 0) return null;
                int bytes = Marshal.ReadInt32(size);
                if (bytes <= 2 || bytes > 8192) return null;
                IntPtr buf = Marshal.AllocHGlobal(bytes);
                try
                {
                    if (waveInMessage((IntPtr)waveInIndex, DRV_QUERYFUNCTIONINSTANCEID, buf, (IntPtr)bytes) != 0) return null;
                    string s = Marshal.PtrToStringUni(buf) ?? "";
                    return s.Length > 0 ? s : null;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { Marshal.FreeHGlobal(size); }
        }
    }
}
