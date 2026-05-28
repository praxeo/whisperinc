using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace WhisperInk
{
    public class HistoryItem
    {
        public DateTime Timestamp { get; set; }
        public string Text { get; set; } = "";

        public string TimeStr => Timestamp.ToString("HH:mm");
    }

    public static class HistoryService
    {
        public static ObservableCollection<HistoryItem> Items { get; private set; } = new();

        private static readonly string _historyPath;
        private static readonly string _logPath;

        static HistoryService()
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string configDirectory = Path.Combine(appDataPath, ".WhisperInk");
            if (!Directory.Exists(configDirectory)) Directory.CreateDirectory(configDirectory);
            _historyPath = Path.Combine(configDirectory, "history.json");
            _logPath     = Path.Combine(configDirectory, "debug.log");

            Load();
        }

        public static void Add(string text)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var item = new HistoryItem
                {
                    Timestamp = DateTime.Now,
                    Text = text
                };

                Items.Insert(0, item);

                if (Items.Count > 100) Items.RemoveAt(Items.Count - 1);

                Save();
            });
        }

        public static void Remove(HistoryItem item)
        {
            if (Items.Contains(item))
            {
                Items.Remove(item);
                Save();
            }
        }

        private static void Load()
        {
            try
            {
                if (!File.Exists(_historyPath)) return;
                string json = File.ReadAllText(_historyPath);
                var list = JsonSerializer.Deserialize<List<HistoryItem>>(json);
                if (list == null) return;
                list.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
                foreach (var i in list) Items.Add(i);
            }
            catch (Exception ex)
            {
                // Surface load failures so a corrupted history.json doesn't
                // silently wipe the log on next save. The previous content
                // stays on disk until Add() triggers a save that overwrites it.
                LogError($"HistoryService.Load failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_historyPath, json);
            }
            catch (Exception ex)
            {
                LogError($"HistoryService.Save failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void LogError(string msg)
        {
            try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
        }
    }
}
