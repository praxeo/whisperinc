using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace WhisperInk
{
    public partial class ProviderSettingsWindow : Window
    {
        private List<ApiProvider> _providers;
        private ApiProvider? _current;
        private TranscriptionModelProfile? _currentProfile;
        private bool _isSwitchingProfile;

        /// <summary>On save, returns the edited provider list.</summary>
        public List<ApiProvider> ResultProviders { get; private set; } = new();

        public ProviderSettingsWindow(List<ApiProvider> providers, string activeId)
        {
            InitializeComponent();

            // Deep-copy so edits don't mutate the caller until Save
            _providers = providers.Select(CloneProvider).ToList();
            ApiProvider.NormalizeDefaults(_providers);

            CmbProviders.ItemsSource = _providers;

            var active = _providers.FirstOrDefault(p => p.Id == activeId) ?? _providers.FirstOrDefault();
            if (active != null)
                CmbProviders.SelectedItem = active;
        }

        private void CmbProviders_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Save current fields before switching
            CommitCurrentFields();

            _current = CmbProviders.SelectedItem as ApiProvider;
            if (_current == null) return;

            _current.EnsureTranscriptionProfiles();

            TxtName.Text = _current.Name;
            TxtBaseUrl.Text = _current.BaseUrl;
            TxtApiKey.Text = _current.ApiKey;
            TxtTranscriptionEndpoint.Text = _current.TranscriptionEndpoint;
            TxtAuthHeaderName.Text = _current.AuthHeaderName;
            TxtModelFieldName.Text = _current.ModelFieldName;
            TxtTranscriptionModel.Text = _current.TranscriptionModel;
            TxtLegacyTemperature.Text = _current.TranscriptionTemperature?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
            SetComboValue(CmbLegacyContextBiasMode, string.IsNullOrWhiteSpace(_current.ContextBiasMode) ? "none" : _current.ContextBiasMode);
            TxtChatModel.Text = _current.ChatModel;
            TxtPostProcessModel.Text = _current.PostProcessModel;
            ChkSupportsTranscription.IsChecked = _current.SupportsTranscription;
            ChkSupportsRealtime.IsChecked = _current.SupportsRealtime;

            RefreshProfilesCombo();
        }

        private void CommitCurrentFields()
        {
            if (_current == null) return;

            CommitCurrentProfileFields();

            _current.Name = TxtName.Text.Trim();
            _current.BaseUrl = TxtBaseUrl.Text.Trim().TrimEnd('/');
            _current.ApiKey = TxtApiKey.Text.Trim();
            _current.TranscriptionEndpoint = TxtTranscriptionEndpoint.Text.Trim().TrimEnd('/');
            _current.AuthHeaderName = TxtAuthHeaderName.Text.Trim();
            _current.ModelFieldName = TxtModelFieldName.Text.Trim();
            _current.TranscriptionModel = TxtTranscriptionModel.Text.Trim();

            if (double.TryParse(TxtLegacyTemperature.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var temp))
                _current.TranscriptionTemperature = temp;
            else
                _current.TranscriptionTemperature = null;

            _current.ContextBiasMode = GetComboValue(CmbLegacyContextBiasMode, "none");
            _current.ChatModel = TxtChatModel.Text.Trim();
            _current.PostProcessModel = TxtPostProcessModel.Text.Trim();
            _current.SupportsTranscription = ChkSupportsTranscription.IsChecked == true;
            _current.SupportsRealtime = ChkSupportsRealtime.IsChecked == true;

            // Keep legacy model synchronized with active profile model when present.
            var activeProfile = _current.GetActiveTranscriptionProfile();
            if (!string.IsNullOrWhiteSpace(activeProfile?.ModelId))
                _current.TranscriptionModel = activeProfile.ModelId.Trim();
        }

        private void RefreshProfilesCombo()
        {
            if (_current == null) return;

            _isSwitchingProfile = true;
            try
            {
                _current.EnsureTranscriptionProfiles();
                CmbProfiles.ItemsSource = null;
                CmbProfiles.ItemsSource = _current.TranscriptionProfiles;

                var active = _current.GetActiveTranscriptionProfile() ?? _current.TranscriptionProfiles.FirstOrDefault();
                CmbProfiles.SelectedItem = active;
            }
            finally
            {
                _isSwitchingProfile = false;
            }

            LoadProfileFields(_current.GetActiveTranscriptionProfile());
        }

        private void CmbProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSwitchingProfile) return;

            CommitCurrentProfileFields();
            _currentProfile = CmbProfiles.SelectedItem as TranscriptionModelProfile;

            if (_current != null && _currentProfile != null)
                _current.ActiveTranscriptionProfileId = _currentProfile.Id;

            LoadProfileFields(_currentProfile);
        }

        private void LoadProfileFields(TranscriptionModelProfile? profile)
        {
            _currentProfile = profile;

            if (profile == null)
            {
                TxtProfileDisplayName.Text = "";
                TxtProfileModelId.Text = "";
                ChkProfileEnabled.IsChecked = true;
                ChkProfileSendLanguage.IsChecked = true;
                TxtProfileLanguage.Text = "en";
                TxtProfileTemperature.Text = "";
                SetComboValue(CmbProfileContextBiasMode, "inherit");
                TxtProfilePrompt.Text = "";
                TxtProfileContextBiasTerms.Text = "";
                TxtProfileHints.Text = "";
                DgRawOverrides.ItemsSource = null;
                return;
            }

            TxtProfileDisplayName.Text = profile.DisplayName;
            TxtProfileModelId.Text = profile.ModelId;
            ChkProfileEnabled.IsChecked = profile.Enabled;
            ChkProfileSendLanguage.IsChecked = profile.SendLanguage;
            TxtProfileLanguage.Text = profile.Language;
            TxtProfileTemperature.Text = profile.Temperature?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
            SetComboValue(CmbProfileContextBiasMode, string.IsNullOrWhiteSpace(profile.ContextBiasMode) ? "inherit" : profile.ContextBiasMode);
            TxtProfilePrompt.Text = profile.Prompt;
            TxtProfileContextBiasTerms.Text = profile.ContextBiasTerms;
            TxtProfileHints.Text = profile.Hints;

            profile.RawOverrides ??= new List<RawParameterOverride>();
            DgRawOverrides.ItemsSource = profile.RawOverrides;
        }

        private void CommitCurrentProfileFields()
        {
            if (_currentProfile == null) return;

            _currentProfile.DisplayName = TxtProfileDisplayName.Text.Trim();
            _currentProfile.ModelId = TxtProfileModelId.Text.Trim();
            _currentProfile.Enabled = ChkProfileEnabled.IsChecked == true;
            _currentProfile.SendLanguage = ChkProfileSendLanguage.IsChecked == true;
            _currentProfile.Language = TxtProfileLanguage.Text.Trim();

            if (double.TryParse(TxtProfileTemperature.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var temp))
                _currentProfile.Temperature = temp;
            else
                _currentProfile.Temperature = null;

            _currentProfile.ContextBiasMode = GetComboValue(CmbProfileContextBiasMode, "inherit");
            _currentProfile.Prompt = TxtProfilePrompt.Text.Trim();
            _currentProfile.ContextBiasTerms = TxtProfileContextBiasTerms.Text.Trim();
            _currentProfile.Hints = TxtProfileHints.Text.Trim();

            if (_currentProfile.RawOverrides == null)
                _currentProfile.RawOverrides = new List<RawParameterOverride>();
        }

        private static string GetComboValue(ComboBox combo, string fallback)
        {
            if (combo.SelectedItem is ComboBoxItem cbi && cbi.Content is string cs && !string.IsNullOrWhiteSpace(cs))
                return cs.Trim();

            if (combo.SelectedValue is string sv && !string.IsNullOrWhiteSpace(sv))
                return sv.Trim();

            if (combo.Text is string txt && !string.IsNullOrWhiteSpace(txt))
                return txt.Trim();

            return fallback;
        }

        private static void SetComboValue(ComboBox combo, string value)
        {
            string wanted = (value ?? "").Trim();
            foreach (var item in combo.Items)
            {
                if (item is ComboBoxItem cbi && string.Equals(cbi.Content as string, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = cbi;
                    return;
                }
            }

            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
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
            newProvider.TranscriptionProfiles = TranscriptionParameterCatalog.CreateDefaultProfiles(newProvider.Id, "");
            if (newProvider.TranscriptionProfiles.Count > 0)
                newProvider.ActiveTranscriptionProfileId = newProvider.TranscriptionProfiles[0].Id;

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
            CommitCurrentProfileFields();

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

                p.EnsureTranscriptionProfiles();
                if (p.TranscriptionProfiles.Count == 0)
                {
                    MessageBox.Show($"Provider \"{p.Name}\" needs at least one transcription profile.", "Validation",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                foreach (var prof in p.TranscriptionProfiles)
                {
                    if (string.IsNullOrWhiteSpace(prof.DisplayName))
                    {
                        MessageBox.Show($"Provider \"{p.Name}\" has a profile with an empty display name.", "Validation",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var dupKeys = prof.RawOverrides
                        .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Key))
                        .GroupBy(r => r.Key.Trim(), StringComparer.OrdinalIgnoreCase)
                        .Where(g => g.Count() > 1)
                        .Select(g => g.Key)
                        .ToList();

                    if (dupKeys.Count > 0)
                    {
                        MessageBox.Show($"Provider \"{p.Name}\", profile \"{prof.DisplayName}\" has duplicate raw override keys: {string.Join(", ", dupKeys)}", "Validation",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
            }

            ApiProvider.NormalizeDefaults(_providers);
            ResultProviders = _providers;
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
            ContextBiasMode = src.ContextBiasMode,
            ActiveTranscriptionProfileId = src.ActiveTranscriptionProfileId,
            TranscriptionProfiles = src.TranscriptionProfiles?.Select(CloneProfile).ToList() ?? new List<TranscriptionModelProfile>()
        };

        private static TranscriptionModelProfile CloneProfile(TranscriptionModelProfile src) => new()
        {
            Id = src.Id,
            DisplayName = src.DisplayName,
            ModelId = src.ModelId,
            SendLanguage = src.SendLanguage,
            Language = src.Language,
            Temperature = src.Temperature,
            ContextBiasMode = src.ContextBiasMode,
            Prompt = src.Prompt,
            ContextBiasTerms = src.ContextBiasTerms,
            Hints = src.Hints,
            Enabled = src.Enabled,
            RawOverrides = src.RawOverrides?.Select(r => new RawParameterOverride
            {
                Key = r.Key,
                Value = r.Value,
                ValueTypeHint = r.ValueTypeHint,
                Enabled = r.Enabled
            }).ToList() ?? new List<RawParameterOverride>()
        };

        private void BtnProfileAdd_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;

            CommitCurrentProfileFields();
            _current.EnsureTranscriptionProfiles();

            var profile = new TranscriptionModelProfile
            {
                DisplayName = "New Profile",
                ModelId = _current.TranscriptionModel,
                SendLanguage = string.IsNullOrWhiteSpace(_current.AuthHeaderName),
                Language = string.IsNullOrWhiteSpace(_current.AuthHeaderName) ? "en" : "",
                Temperature = _current.TranscriptionTemperature,
                ContextBiasMode = "inherit",
                Hints = TranscriptionParameterCatalog.BuildHints(_current.Id, _current.TranscriptionModel),
                Enabled = true
            };

            _current.TranscriptionProfiles.Add(profile);
            _current.ActiveTranscriptionProfileId = profile.Id;

            RefreshProfilesCombo();
            CmbProfiles.SelectedItem = profile;
        }

        private void BtnProfileDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null || _currentProfile == null) return;
            if (_current.TranscriptionProfiles.Count <= 1)
            {
                MessageBox.Show("You must keep at least one profile.", "Cannot Delete",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"Delete profile \"{_currentProfile.DisplayName}\"?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            _current.TranscriptionProfiles.Remove(_currentProfile);
            _currentProfile = null;

            _current.EnsureTranscriptionProfiles();
            if (_current.TranscriptionProfiles.Count > 0)
                _current.ActiveTranscriptionProfileId = _current.TranscriptionProfiles[0].Id;

            RefreshProfilesCombo();
        }
    }
}
