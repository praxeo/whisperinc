using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WhisperInk
{
    /// <summary>
    /// Which capture device to open: the microphone the user chose, found by
    /// its NAME.
    ///
    /// The choice used to be saved as a position in Windows' device list only.
    /// On 2026-10-12 the desktop's dictation mic dropped off USB, position 0
    /// became a different input with nothing on it, and every take for three
    /// hours was recorded from that one without a word about it. A position
    /// says nothing about which device sits there.
    ///
    /// So the name is saved as well, and the chosen mic is looked for by name
    /// on every open. When it isn't connected, the first device Windows lists
    /// (its default input, in practice) stands in, so a dictation is still
    /// recorded, and <see cref="Choice.Missing"/> tells the caller to say so.
    ///
    /// Pure, so the harness can test it without a microphone.
    /// </summary>
    public static class MicSelection
    {
        // winmm cuts a device name to 31 characters.
        private const int MaxWinmmName = 31;

        // "Microphone (2- VEC GN-USB)": Windows numbers a device it has seen
        // on another USB port. It's the same microphone.
        private static readonly Regex InstanceNumber = new(@"\(\d+- ", RegexOptions.Compiled);

        /// <summary><paramref name="Index"/> is the device to open, -1 when
        /// there is none. <paramref name="Missing"/> is the chosen mic's name
        /// when it isn't connected and Index is only standing in for it; null
        /// when Index is the chosen mic, or none was ever chosen.</summary>
        public readonly record struct Choice(int Index, string Name, string? Missing);

        /// <param name="devices">The capture devices as Windows lists them now.</param>
        /// <param name="savedIndex">The position saved with the choice: a tie-break between identical mics, and all an older config has.</param>
        /// <param name="savedName">The chosen mic's name; empty when the config predates names.</param>
        public static Choice Resolve(IReadOnlyList<string> devices, int savedIndex, string? savedName)
        {
            bool named = !string.IsNullOrWhiteSpace(savedName);
            if (devices.Count == 0) return new Choice(-1, "", named ? savedName : null);

            bool indexValid = savedIndex >= 0 && savedIndex < devices.Count;
            if (!named)
            {
                int index = indexValid ? savedIndex : 0;
                return new Choice(index, devices[index], null);
            }

            if (indexValid && Same(devices[savedIndex], savedName!))
                return new Choice(savedIndex, devices[savedIndex], null);
            for (int i = 0; i < devices.Count; i++)
                if (Same(devices[i], savedName!)) return new Choice(i, devices[i], null);

            return new Choice(0, devices[0], savedName);
        }

        /// <summary>True when two device names are the same microphone: the
        /// same name whatever USB port it's on, allowing for winmm having cut
        /// either one short.</summary>
        public static bool Same(string a, string b)
        {
            string na = Normalize(a), nb = Normalize(b);
            if (na.Length == 0 || nb.Length == 0) return false;
            if (string.Equals(na, nb, StringComparison.OrdinalIgnoreCase)) return true;
            return (IsCut(a) && nb.StartsWith(na, StringComparison.OrdinalIgnoreCase))
                || (IsCut(b) && na.StartsWith(nb, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsCut(string name) => (name ?? "").Trim().Length >= MaxWinmmName;

        private static string Normalize(string? name) => InstanceNumber.Replace((name ?? "").Trim(), "(");
    }
}
