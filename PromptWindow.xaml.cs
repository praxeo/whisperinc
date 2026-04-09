using System.Windows;

namespace WhisperInk
{
    public partial class PromptWindow : Window
    {
        public string PromptText { get; private set; } = "";

        public PromptWindow(string currentPrompt)
        {
            InitializeComponent();
            txtPrompt.Text = currentPrompt;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtPrompt.Text))
            {
                MessageBox.Show("The system prompt cannot be empty. Please enter text or click 'Reset to Default'.",
                                "Attention",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                return;
            }

            PromptText = txtPrompt.Text;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            var defaultConfig = new AppConfig();
            txtPrompt.Text = defaultConfig.SystemPrompt;
        }   
    }
}
