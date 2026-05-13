using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace WhisperInk
{
    public partial class ProviderSettingsWindow : Window
    {
        private List<ApiProvider> _providers;
        private ApiProvider? _current;

        /// <summary>On save, returns the edited provider list.</summary>
        public List<ApiProvider> ResultProviders { get; private set; } = new();

        /// <summary>The provider visible in the combo when Save was pressed.
        /// MainWindow uses this as the new active provider.</summary>
        public string? ResultActiveProviderId { get; private set; }

        /// <summary>Normalized CrispASR GPU backend selected when Save was pressed.</summary>
        public string ResultGpuBackend { get; private set; } = "auto";

        public ProviderSettingsWindow(List<ApiProvider> providers, string activeId,
            string currentGpuBackend, string detectedGpuSummary)
        {
            InitializeComponent();

            // Deep-copy so edits don't mutate the caller until Save
            _providers = providers.Select(CloneProvider).ToList();

            CmbProviders.ItemsSource = _providers;

            var active = _providers.FirstOrDefault(p => p.Id == activeId) ?? _providers.FirstOrDefault();
            if (active != null)
                CmbProviders.SelectedItem = active;

            // GPU backend combo — set the saved value, or fall back to Auto
            string gpu = (currentGpuBackend ?? "auto").Trim().ToLowerInvariant();
            var gpuItem = CmbGpuBackend.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), gpu, StringComparison.OrdinalIgnoreCase));
            CmbGpuBackend.SelectedItem = gpuItem ?? CmbGpuBackend.Items[0];

            if (!string.IsNullOrWhiteSpace(detectedGpuSummary))
                GpuBackendStatusLabel.Text = detectedGpuSummary;
        }

        private void CmbProviders_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Save current fields before switching
            CommitCurrentFields();

            _current = CmbProviders.SelectedItem as ApiProvider;
            if (_current == null) return;

            TxtName.Text = _current.Name;
            TxtBaseUrl.Text = _current.BaseUrl;
            TxtApiKey.Text = _current.ApiKey;
            TxtTranscriptionEndpoint.Text = _current.TranscriptionEndpoint;
            TxtAuthHeaderName.Text = _current.AuthHeaderName;
            TxtModelFieldName.Text = _current.ModelFieldName;
            TxtTranscriptionModel.Text = _current.TranscriptionModel;
            TxtChatModel.Text = _current.ChatModel;
            TxtPostProcessModel.Text = _current.PostProcessModel;
            ChkSupportsTranscription.IsChecked = _current.SupportsTranscription;
            ChkSupportsRealtime.IsChecked = _current.SupportsRealtime;
            TxtTranscriptionTemperature.Text = _current.TranscriptionTemperature?.ToString() ?? "";

            // Set language dropdown
            var langItem = CmbLanguage.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag?.ToString() == _current.Language);
            if (langItem != null)
                CmbLanguage.SelectedItem = langItem;
            else
                CmbLanguage.SelectedIndex = 0;

            // Set context bias mode dropdown
            var biasItem = CmbContextBiasMode.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag?.ToString() == _current.ContextBiasMode);
            if (biasItem != null)
                CmbContextBiasMode.SelectedItem = biasItem;
            else
                CmbContextBiasMode.SelectedIndex = 0;

            TxtScribeKeyterms.Text = _current.ScribeKeytermsRaw;
            UpdateKeytermsCount();
        }

        private void CommitCurrentFields()
        {
            if (_current == null) return;
            _current.Name = TxtName.Text.Trim();
            _current.BaseUrl = TxtBaseUrl.Text.Trim().TrimEnd('/');
            _current.ApiKey = TxtApiKey.Text.Trim();
            _current.TranscriptionEndpoint = TxtTranscriptionEndpoint.Text.Trim().TrimEnd('/');
            _current.AuthHeaderName = TxtAuthHeaderName.Text.Trim();
            _current.ModelFieldName = TxtModelFieldName.Text.Trim();
            _current.TranscriptionModel = TxtTranscriptionModel.Text.Trim();
            _current.ChatModel = TxtChatModel.Text.Trim();
            _current.PostProcessModel = TxtPostProcessModel.Text.Trim();
            _current.SupportsTranscription = ChkSupportsTranscription.IsChecked == true;
            _current.SupportsRealtime = ChkSupportsRealtime.IsChecked == true;

            // Parse temperature
            if (double.TryParse(TxtTranscriptionTemperature.Text, out double temp))
                _current.TranscriptionTemperature = temp;
            else
                _current.TranscriptionTemperature = null;

            // Get language from dropdown
            _current.Language = (CmbLanguage.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "en";

            // Get context bias mode from dropdown. ComboBox has no SelectedValuePath set,
            // so SelectedValue returns the entire ComboBoxItem; read its Tag explicitly to
            // get the canonical value ("none" / "whisper_prompt" / "cohere_terms").
            _current.ContextBiasMode = (CmbContextBiasMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "none";

            _current.ScribeKeytermsRaw = TxtScribeKeyterms.Text;
        }

        private void TxtScribeKeyterms_TextChanged(object sender, TextChangedEventArgs e) =>
            UpdateKeytermsCount();

        private void UpdateKeytermsCount()
        {
            int count = TxtScribeKeyterms.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Count(t => !string.IsNullOrWhiteSpace(t));
            KeytermsCountLabel.Text = count == 0 ? "" : $"{count} term{(count == 1 ? "" : "s")}";
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            CommitCurrentFields();

            var newProvider = new ApiProvider
            {
                Name = "New Provider",
                BaseUrl = "http://localhost:8080",
                SupportsTranscription = true
            };
            _providers.Add(newProvider);

            // Refresh ComboBox
            CmbProviders.ItemsSource = null;
            CmbProviders.ItemsSource = _providers;
            CmbProviders.SelectedItem = newProvider;
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || _providers.Count <= 1)
            {
                MessageBox.Show("You must keep at least one provider.", "Cannot Delete",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"Delete provider \"{_current.Name}\"?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            _providers.Remove(_current);
            _current = null;

            CmbProviders.ItemsSource = null;
            CmbProviders.ItemsSource = _providers;
            CmbProviders.SelectedIndex = 0;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            CommitCurrentFields();

            // Validate
            foreach (var p in _providers)
            {
                if (string.IsNullOrWhiteSpace(p.Name))
                {
                    MessageBox.Show("Each provider needs a name.", "Validation",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrWhiteSpace(p.BaseUrl))
                {
                    MessageBox.Show($"Provider \"{p.Name}\" needs a Base URL.", "Validation",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            ResultProviders = _providers;
            // The provider currently shown in the combo is the one the user
            // wants active. MainWindow re-applies this on close.
            ResultActiveProviderId = (CmbProviders.SelectedItem as ApiProvider)?.Id ?? _current?.Id;
            ResultGpuBackend = (CmbGpuBackend.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static ApiProvider CloneProvider(ApiProvider src) => new()
        {
            Id = src.Id,
            Name = src.Name,
            BaseUrl = src.BaseUrl,
            ApiKey = src.ApiKey,
            TranscriptionEndpoint = src.TranscriptionEndpoint,
            AuthHeaderName = src.AuthHeaderName,
            ModelFieldName = src.ModelFieldName,
            TranscriptionModel = src.TranscriptionModel,
            ChatModel = src.ChatModel,
            PostProcessModel = src.PostProcessModel,
            SupportsRealtime = src.SupportsRealtime,
            SupportsTranscription = src.SupportsTranscription,
            TranscriptionTemperature = src.TranscriptionTemperature,
            Language = src.Language,
            ContextBiasMode = src.ContextBiasMode,
            ScribeKeytermsRaw = src.ScribeKeytermsRaw,
            TagAudioEvents = src.TagAudioEvents,
            NoVerbatim = src.NoVerbatim
        };
    }
}
