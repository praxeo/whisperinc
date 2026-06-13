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
            ChkSupportsTranscription.IsChecked = _current.SupportsTranscription;
            TxtTranscriptionTemperature.Text = _current.TranscriptionTemperature?.ToString() ?? "";

            // Set language dropdown
            var langItem = CmbLanguage.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag?.ToString() == _current.Language);
            if (langItem != null)
                CmbLanguage.SelectedItem = langItem;
            else
                CmbLanguage.SelectedIndex = 0;

            // Biasing is routed automatically per provider from the single global
            // Context Bias list — show the mechanism read-only instead of letting
            // the user pick a (usually wrong) mode.
            BiasInfoLabel.Text = DescribeBias(_current);

            TxtScribeKeyterms.Text = _current.ScribeKeytermsRaw;
            ChkTagAudioEvents.IsChecked = _current.TagAudioEvents;
            ChkNoVerbatim.IsChecked     = _current.NoVerbatim;
            UpdateKeytermsCount();

            TxtTranscriberKind.Text = $"Type: {_current.TranscriberKind}";
            // ElevenLabs Scribe v2 fields only matter when the provider uses
            // the xi-api-key auth scheme. Hide the whole group for everyone
            // else so the dialog doesn't suggest editable knobs that won't
            // do anything.
            ScribeGroup.Visibility = _current.UsesCustomAuthHeader
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Beam size + hotword boost only apply to providers backed by a local
            // crispasr.exe server — hide the group everywhere else.
            TxtLocalBeamSize.Text = _current.LocalBeamSize?.ToString() ?? "";
            TxtHotwordsBoost.Text = _current.HotwordsBoost?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            LocalCrispGroup.Visibility = _current.TranscriberKind == TranscriberKind.LocalCrispAsrServer
                ? Visibility.Visible
                : Visibility.Collapsed;
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
            _current.SupportsTranscription = ChkSupportsTranscription.IsChecked == true;

            // Parse temperature
            if (double.TryParse(TxtTranscriptionTemperature.Text, out double temp))
                _current.TranscriptionTemperature = temp;
            else
                _current.TranscriptionTemperature = null;

            // Get language from dropdown
            _current.Language = (CmbLanguage.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "en";

            _current.ScribeKeytermsRaw = TxtScribeKeyterms.Text;
            _current.TagAudioEvents    = ChkTagAudioEvents.IsChecked == true;
            _current.NoVerbatim        = ChkNoVerbatim.IsChecked == true;

            // Beam size: blank or non-positive = null (server default / greedy).
            _current.LocalBeamSize = int.TryParse(TxtLocalBeamSize.Text, out int beam) && beam > 0
                ? beam
                : null;

            // Hotword boost: blank or non-positive = null (server default 2.0).
            _current.HotwordsBoost = double.TryParse(TxtHotwordsBoost.Text,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double boost) && boost > 0
                ? boost
                : null;
        }

        /// <summary>One-line, read-only description of how this provider routes the
        /// shared Context Bias list — shown in place of the old manual mode dropdown.</summary>
        private static string DescribeBias(ApiProvider p) => p.ResolvedBiasMechanism switch
        {
            "mistral_context_bias" => "Domain vocabulary → Mistral context_bias (batch; ≤100 terms).",
            "whisper_prompt"       => "Domain vocabulary → prompt glossary.",
            "elevenlabs_keyterms"  => "Domain vocabulary → ElevenLabs keyterms (from the global Context Bias list).",
            "hotwords"             => "Domain vocabulary → CrispASR hotwords (real on Parakeet/Voxtral; ignored by Cohere/Granite/Voxtral-4B).",
            "phrase_sets"          => "Domain vocabulary → Google phrase sets (native).",
            "context_terms"        => "Domain vocabulary → Soniox context terms (native).",
            _                      => "No native biasing — context-bias terms have no effect for this provider.",
        };

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
            SupportsTranscription = src.SupportsTranscription,
            TranscriptionTemperature = src.TranscriptionTemperature,
            Language = src.Language,
            ContextBiasMode = src.ContextBiasMode,
            ScribeKeytermsRaw = src.ScribeKeytermsRaw,
            TagAudioEvents = src.TagAudioEvents,
            NoVerbatim = src.NoVerbatim,
            // New schema fields (factory dispatch). Forgetting these here
            // makes the dialog silently reset them every time it's opened.
            TranscriberKind  = src.TranscriberKind,
            LocalServerPort  = src.LocalServerPort,
            LocalModelGlob   = src.LocalModelGlob,
            LocalBackendHint = src.LocalBackendHint,
            LocalGpuBackend  = src.LocalGpuBackend,
            LocalModelFolder = src.LocalModelFolder,
            LocalBeamSize    = src.LocalBeamSize,
            LocalPuncModel   = src.LocalPuncModel,
            LocalTruecaseModel = src.LocalTruecaseModel,
            LocalExtraParams = src.LocalExtraParams != null
                ? new Dictionary<string, string>(src.LocalExtraParams)
                : new Dictionary<string, string>(),
            BiasMechanism    = src.BiasMechanism,
            HotwordsBoost    = src.HotwordsBoost,
        };
    }
}
