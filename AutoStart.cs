using System;
using Microsoft.Win32;

namespace WhisperInk
{
    /// <summary>
    /// HKCU Run-at-login registry helper. User-level, no admin needed.
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName  = "WhisperInk";

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string;
            }
            catch { return false; }
        }

        public static void SetEnabled(bool enabled, string exePath)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                if (key == null) return;
                if (enabled)
                {
                    string quoted = "\"" + exePath + "\"";
                    key.SetValue(ValueName, quoted, RegistryValueKind.String);
                }
                else
                {
                    if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                }
            }
            catch { }
        }
    }
}
