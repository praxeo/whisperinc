# Transcription Accuracy Optimization Plan

## Overview
This plan outlines the UI additions needed to make all transcription parameters configurable in WhisperInk, enabling users to maximize accuracy across all supported providers (OpenAI Whisper, Cohere, Mistral, ElevenLabs).

## Current State Analysis

### Already Configurable in UI
| Parameter | Location | Notes |
|-----------|----------|-------|
| Provider selection | Context menu | Dropdown with provider list |
| Base URL | ProviderSettingsWindow | Text input |
| API Key | ProviderSettingsWindow | Text input |
| Transcription Endpoint | ProviderSettingsWindow | Text input |
| Auth Header Name | ProviderSettingsWindow | Text input |
| Model Field Name | ProviderSettingsWindow | Text input |
| Transcription Model | ProviderSettingsWindow | Text input |
| Chat Model | ProviderSettingsWindow | Text input |
| Post-Process Model | ProviderSettingsWindow | Text input |
| SupportsTranscription | ProviderSettingsWindow | Checkbox |
| SupportsRealtime | ProviderSettingsWindow | Checkbox |
| System Prompt | PromptWindow | Text area for AI mode |
| Post-Process Toggle | Context menu | Checkbox for medical correction |
| Streaming Delay | Context menu | Preset values (240-2400ms) |
| Microphone | Context menu | Dropdown of devices |
| Sound Toggle | Context menu | On/Off |

### Missing UI Configuration Options
| Parameter | Current Status | Provider Support |
|-----------|----------------|------------------|
| **Language** | Hardcoded to "en" | All providers |
| **TranscriptionTemperature** | In ApiProvider class, no UI | OpenAI, Cohere, ElevenLabs |
| **ContextBiasTerms** | In AppConfig, no UI | OpenAI, Cohere |
| **ContextBiasMode** | In ApiProvider, no UI | OpenAI, Cohere |

## Proposed UI Additions

### 1. ProviderSettingsWindow Enhancements

Add the following fields to [`ProviderSettingsWindow.xaml`](ProviderSettingsWindow.xaml):

#### 1.1 Transcription Temperature
```xml
<Label Content="Transcription Temperature (0.0-1.0, blank = provider default)"/>
<TextBox x:Name="TxtTranscriptionTemperature" Margin="0,0,0,4"/>
<TextBlock Foreground="#666" FontSize="10" Margin="0,0,0,8"
           Text="Lower = more deterministic. Cohere: 0.1, OpenAI: 0.0"/>
```

#### 1.2 Language Selection
```xml
<Label Content="Language (ISO 639-1 code, e.g. en, es, fr)"/>
<ComboBox x:Name="CmbLanguage" Margin="0,0,0,4">
    <ComboBoxItem Content="en (English)" Tag="en"/>
    <ComboBoxItem Content="es (Spanish)" Tag="es"/>
    <ComboBoxItem Content="fr (French)" Tag="fr"/>
    <ComboBoxItem Content="de (German)" Tag="de"/>
    <ComboBoxItem Content="it (Italian)" Tag="it"/>
    <ComboBoxItem Content="pt (Portuguese)" Tag="pt"/>
    <ComboBoxItem Content="nl (Dutch)" Tag="nl"/>
    <ComboBoxItem Content="ja (Japanese)" Tag="ja"/>
    <ComboBoxItem Content="ko (Korean)" Tag="ko"/>
    <ComboBoxItem Content="zh (Chinese)" Tag="zh"/>
    <ComboBoxItem Content="ru (Russian)" Tag="ru"/>
    <ComboBoxItem Content="ar (Arabic)" Tag="ar"/>
    <ComboBoxItem Content="hi (Hindi)" Tag="hi"/>
</ComboBox>
<TextBlock Foreground="#666" FontSize="10" Margin="0,0,0,8"
           Text="Leave blank for auto-detection (where supported)"/>
```

#### 1.3 Context Bias Mode
```xml
<Label Content="Context Bias Mode (how to send bias terms)"/>
<ComboBox x:Name="CmbContextBiasMode" Margin="0,0,0,4">
    <ComboBoxItem Content="None (don't send bias)" Tag="none"/>
    <ComboBoxItem Content="Whisper Prompt (OpenAI-compatible)" Tag="whisper_prompt"/>
    <ComboBoxItem Content="Cohere Terms (Cohere v2 API)" Tag="cohere_terms"/>
</ComboBox>
<TextBlock Foreground="#666" FontSize="10" Margin="0,0,0,8"
           Text="Whisper Prompt: OpenAI, Groq, DeepInfra, local servers"/>
<TextBlock Foreground="#666" FontSize="10" Margin="0,0,0,8"
           Text="Cohere Terms: Cohere v2 cloud API only"/>
```

### 2. Context Bias Terms Editor Window

Create a new window `ContextBiasWindow.xaml` for editing context bias terms:

```xml
<Window x:Class="WhisperInk.ContextBiasWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Context Bias Terms" Height="400" Width="500"
        WindowStartupLocation="CenterScreen"
        Background="#1E1E1E" ResizeMode="CanResizeWithGrip">
    
    <Grid Margin="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        
        <TextBlock Grid.Row="0" Text="Context Bias Terms (one per line):" 
                   FontWeight="Bold" FontSize="12" Foreground="#AAAAAA" Margin="0,0,0,10"/>
        
        <TextBox Grid.Row="1" x:Name="TxtBiasTerms"
                 AcceptsReturn="True" TextWrapping="Wrap" 
                 VerticalScrollBarVisibility="Auto"
                 Background="#2D2D2D" Foreground="#CCCCCC" 
                 BorderBrush="#555" Padding="8"/>
        
        <StackPanel Grid.Row="2" Orientation="Horizontal" 
                    HorizontalAlignment="Right" Margin="0,8,0,0">
            <Button Content="Cancel" Click="BtnCancel_Click" Margin="4,0"/>
            <Button Content="Save" Click="BtnSave_Click" 
                    Background="#2A4A2A" FontWeight="SemiBold" Margin="4,0"/>
        </StackPanel>
    </Grid>
</Window>
```

### 3. Context Menu Additions

Add a new menu item to [`MainWindow.xaml.cs`](MainWindow.xaml.cs:1355) context menu:

```csharp
// ── Context Bias Terms ──
var biasItem = new MenuItem { Header = "🎯 Context Bias Terms" };
biasItem.Click += (_, _) =>
{
    var biasWindow = new ContextBiasWindow(_contextBiasTerms);
    if (biasWindow.ShowDialog() == true)
    {
        _contextBiasTerms = biasWindow.BiasTerms;
        SaveConfig();
    }
};
menu.Items.Add(biasItem);
```

## Code Changes Required

### 1. ApiProvider.cs Updates

Add new properties to [`ApiProvider`](AppConfig.cs:6) class:

```csharp
// Language code for transcription (e.g., "en", "es")
public string Language { get; set; } = "en";
```

Update [`CreateDefaults()`](AppConfig.cs:83) to set language for each provider:

```csharp
new ApiProvider
{
    Id = "openai",
    Name = "OpenAI",
    BaseUrl = "https://api.openai.com",
    TranscriptionModel = "whisper-1",
    ChatModel = "gpt-4o-mini",
    PostProcessModel = "gpt-4o-mini",
    SupportsRealtime = false,
    SupportsTranscription = true,
    TranscriptionTemperature = 0.0,
    ContextBiasMode = "whisper_prompt",
    Language = "en"  // Add this
},
```

### 2. ProviderSettingsWindow.xaml.cs Updates

Add field bindings for new controls:

```csharp
// Load
TxtTranscriptionTemperature.Text = _current.TranscriptionTemperature?.ToString() ?? "";
CmbLanguage.SelectedValue = _current.Language ?? "en";
CmbContextBiasMode.SelectedValue = _current.ContextBiasMode ?? "none";

// Save
if (double.TryParse(TxtTranscriptionTemperature.Text, out double temp))
    _current.TranscriptionTemperature = temp;
else
    _current.TranscriptionTemperature = null;
_current.Language = CmbLanguage.SelectedValue?.ToString() ?? "en";
_current.ContextBiasMode = CmbContextBiasMode.SelectedValue?.ToString() ?? "none";
```

### 3. MainWindow.xaml.cs Updates

Update [`TranscribeBatchAsync()`](MainWindow.xaml.cs:1040) to use provider's language:

```csharp
// ── 2. Language ──
// Use provider's language setting, default to "en" if not set
string language = activeProvider?.Language ?? "en";
if (string.IsNullOrWhiteSpace(_activeAuthHeaderName))
    content.Add(new StringContent(language), "language");
```

Update [`CohereOnnxTranscriber`](CohereOnnxTranscriber.cs:163) call to use provider's language:

```csharp
string language = activeProvider?.Language ?? "en";
var result = await _cohereOnnx.TranscribeAsync(filePath, language);
```

### 4. ContextBiasWindow.xaml.cs

Create new window class:

```csharp
public partial class ContextBiasWindow : Window
{
    public List<string> BiasTerms { get; private set; } = new();
    
    public ContextBiasWindow(List<string> initialTerms)
    {
        InitializeComponent();
        TxtBiasTerms.Text = string.Join("\n", initialTerms);
    }
    
    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        BiasTerms = TxtBiasTerms.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
        DialogResult = true;
        Close();
    }
    
    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
```

## Provider-Specific Parameter Recommendations

### OpenAI Whisper
| Parameter | Recommended Value | Notes |
|-----------|-------------------|-------|
| `temperature` | 0.0 | Fully deterministic |
| `language` | "en" or specific code | Improves accuracy |
| `prompt` | Domain-specific terms | Via ContextBiasTerms |

### Cohere Transcribe
| Parameter | Recommended Value | Notes |
|-----------|-------------------|-------|
| `temperature` | 0.1 | Focused, deterministic |
| `language` | "en" or specific code | Required parameter |
| `context_bias_terms` | JSON array of terms | Up to 100 strings |

### Mistral Voxtral
| Parameter | Recommended Value | Notes |
|-----------|-------------------|-------|
| `language` | "en" or specific code | Optional, improves accuracy |
| `temperature` | Not supported | Uses default |

### ElevenLabs Scribe
| Parameter | Recommended Value | Notes |
|-----------|-------------------|-------|
| `language_code` | "en" or specific code | Optional, auto-detects |
| `temperature` | 0.0-1.0 | Optional |
| `model_id` | "scribe_v2" | Latest model |

## Future Enhancements (Optional)

### Advanced Parameters
These parameters are available in APIs but not currently implemented:

1. **Timestamp Granularities** (Mistral, ElevenLabs)
   - Returns segment-level timing information
   - Useful for subtitle generation

2. **Diarization** (ElevenLabs)
   - Speaker identification and separation
   - Useful for meeting transcription

3. **Entity Detection/Redaction** (ElevenLabs)
   - Detect and optionally redact PII
   - Useful for privacy-sensitive applications

4. **Response Format** (OpenAI)
   - JSON vs plain text output
   - Useful for programmatic processing

5. **Hallucination Filtering** (Cohere)
   - Filter out false positives in silent regions
   - Already implemented in local ONNX server

## Implementation Priority

### Phase 1: Core Accuracy Parameters (High Priority)
1. Add Language selection to ProviderSettingsWindow
2. Add TranscriptionTemperature input to ProviderSettingsWindow
3. Add ContextBiasMode dropdown to ProviderSettingsWindow
4. Create ContextBiasWindow for editing bias terms
5. Add Context Bias Terms menu item to context menu
6. Update MainWindow.xaml.cs to use provider's language setting

### Phase 2: Documentation & Examples (Medium Priority)
1. Create accuracy optimization guide
2. Document provider-specific recommendations
3. Create example bias term sets for common domains
4. Add tooltips and help text to UI

### Phase 3: Advanced Features (Low Priority)
1. Add timestamp granularity options
2. Add diarization support for ElevenLabs
3. Add entity detection/redaction options
4. Add response format selection for OpenAI

## Testing Checklist

- [ ] Test language selection with each provider
- [ ] Test temperature changes affect output
- [ ] Test context bias terms improve domain-specific accuracy
- [ ] Test context bias mode switching between providers
- [ ] Test saving and loading configuration
- [ ] Test with multiple languages
- [ ] Test with domain-specific bias terms (medical, technical, etc.)
