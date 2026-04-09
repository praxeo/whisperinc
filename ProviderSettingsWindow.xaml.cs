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

        public ProviderSettingsWindow(List<ApiProvider> providers, string activeId)
        {
            InitializeComponent();

            // Deep-copy so edits don't mutate the caller until Save
            _providers = providers.Select(CloneProvider).ToList();

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
            SupportsTranscription = src.SupportsTranscription
        };
    }
}
