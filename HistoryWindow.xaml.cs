using System.Windows;
using System.Windows.Controls;

namespace WhisperInk
{
    public partial class HistoryWindow : Window
    {
        public HistoryWindow()
        {
            InitializeComponent();
            HistoryList.ItemsSource = HistoryService.Items;
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is HistoryItem item)
            {
                try { Clipboard.SetText(item.Text); } catch { }
            }
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is HistoryItem item)
            {
                HistoryService.Remove(item);
            }
        }
    }
}
