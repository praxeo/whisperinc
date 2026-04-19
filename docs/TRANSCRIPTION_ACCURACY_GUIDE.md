# WhisperInk Transcription Accuracy Guide

This guide explains how to maximize transcription accuracy across all supported providers in WhisperInk.

## Table of Contents
- [Overview](#overview)
- [Core Accuracy Parameters](#core-accuracy-parameters)
- [Provider-Specific Recommendations](#provider-specific-recommendations)
- [Context Biasing Strategies](#context-biasing-strategies)
- [Domain-Specific Configurations](#domain-specific-configurations)
- [Troubleshooting Accuracy Issues](#troubleshooting-accuracy-issues)

## Overview

WhisperInk supports multiple transcription providers, each with different parameters that affect accuracy. Understanding and configuring these parameters correctly is essential for getting the best results.

### Supported Providers
- **OpenAI Whisper** - General-purpose speech recognition with excellent multilingual support
- **Cohere Transcribe** - Optimized for medical and technical domains with context biasing
- **Mistral Voxtral** - Fast, accurate transcription with language detection
- **ElevenLabs Scribe** - High-quality transcription with speaker diarization support
- **Local ONNX** - Run Cohere models locally with CPU/GPU acceleration

## Core Accuracy Parameters

### Language

**What it does:** Specifies the language of the audio input. Explicit language setting improves accuracy by constraining the model's search space.

**How to configure:**
1. Right-click the WhisperInk tray icon
2. Select "⚙ Configure Providers..."
3. Select your provider from the dropdown
4. Choose the language from the "Language" dropdown

**Recommendations:**
- Always set the language explicitly for best accuracy
- Use ISO 639-1 codes: `en`, `es`, `fr`, `de`, `it`, `pt`, `nl`, `ja`, `ko`, `zh`, `ru`, `ar`, `hi`
- If you speak multiple languages, choose the primary language of your dictation

**Provider support:**
| Provider | Required | Auto-detect | Notes |
|----------|-----------|-------------|-------|
| OpenAI Whisper | No | Yes | Explicit setting improves accuracy |
| Cohere Transcribe | Yes | No | Must be set |
| Mistral Voxtral | No | Yes | Explicit setting improves accuracy |
| ElevenLabs Scribe | No | Yes | Auto-detects well |
| Local ONNX | Yes | No | Must be set |

### Temperature

**What it does:** Controls the randomness/creativity of the transcription. Lower values produce more deterministic, consistent results.

**How to configure:**
1. Right-click the WhisperInk tray icon
2. Select "⚙ Configure Providers..."
3. Select your provider from the dropdown
4. Enter a value (0.0 to 1.0) in "Transcription Temperature"
5. Leave blank to use the provider's default

**Recommendations:**
- **0.0** - Maximum determinism, best for technical/medical dictation
- **0.1** - Very low randomness, recommended for Cohere medical use
- **0.2-0.3** - Low randomness, good balance for most use cases
- **0.5+** - Higher randomness, not recommended for dictation

**Provider support:**
| Provider | Recommended Value | Notes |
|----------|-------------------|-------|
| OpenAI Whisper | 0.0 | Fully deterministic |
| Cohere Transcribe | 0.1 | Optimized for medical dictation |
| Mistral Voxtral | N/A | Not configurable |
| ElevenLabs Scribe | 0.0-0.3 | Optional parameter |
| Local ONNX | N/A | Not configurable |

### Context Bias Terms

**What it does:** Provides domain-specific vocabulary to guide the transcription model. This is the most powerful tool for improving accuracy in specialized domains.

**How to configure:**
1. Right-click the WhisperInk tray icon
2. Select "🎯 Context Bias Terms"
3. Enter one term or phrase per line
4. Click "Save"

**Best practices:**
- Use full words/phrases, not abbreviations
- Include both singular and plural forms
- Add common compound terms relevant to your domain
- Limit to 50-100 terms for best performance
- Update regularly based on transcription errors you observe

**Example format:**
```
myocardial infarction
electrocardiogram
tachycardia
bradycardia
hypertension
hypotension
```

**Provider support:**
| Provider | Mode | Limit | Notes |
|----------|------|-------|-------|
| OpenAI Whisper | `whisper_prompt` | ~224 tokens | Comma-delimited string |
| Cohere Transcribe | `cohere_terms` | 100 terms | JSON array |
| Mistral Voxtral | N/A | N/A | Not supported |
| ElevenLabs Scribe | N/A | N/A | Not supported |
| Local ONNX | N/A | N/A | Not supported |

### Context Bias Mode

**What it does:** Determines how context bias terms are sent to the API. Different providers use different field names and formats.

**How to configure:**
1. Right-click the WhisperInk tray icon
2. Select "⚙ Configure Providers..."
3. Select your provider from the dropdown
4. Choose the appropriate mode from "Context Bias Mode"

**Options:**
- **None** - Don't send bias terms (Mistral, ElevenLabs)
- **Whisper Prompt** - Send as comma-delimited string in `prompt` field (OpenAI, Groq, DeepInfra, local servers)
- **Cohere Terms** - Send as JSON array in `context_bias_terms` field (Cohere v2 cloud API only)

**Provider defaults:**
| Provider | Default Mode |
|----------|--------------|
| OpenAI Whisper | `whisper_prompt` |
| Cohere Transcribe API | `cohere_terms` |
| Mistral | `none` |
| ElevenLabs Scribe | `none` |
| Local Server | `whisper_prompt` |
| Cohere Local (ONNX) | `none` |

## Provider-Specific Recommendations

### OpenAI Whisper

**Strengths:**
- Excellent multilingual support
- Strong general-purpose accuracy
- Well-documented parameters

**Optimal Configuration:**
```
Model: whisper-1
Temperature: 0.0
Language: [your language code]
Context Bias Mode: whisper_prompt
Context Bias Terms: [domain-specific terms]
```

**Advanced Tips:**
- The `prompt` field (via Context Bias Terms) can also include introductory text to set context
- Whisper performs best with clear, well-recorded audio
- Consider using post-processing correction for medical/technical content

**Known Limitations:**
- May struggle with very specialized terminology without bias terms
- Can hallucinate text in silent regions
- No built-in speaker diarization

### Cohere Transcribe

**Strengths:**
- Excellent medical/technical domain accuracy
- Powerful context biasing with `context_bias_terms`
- Deterministic output with low temperature

**Optimal Configuration:**
```
Model: cohere-transcribe-03-2026
Temperature: 0.1
Language: [required, e.g., "en"]
Context Bias Mode: cohere_terms
Context Bias Terms: [up to 100 domain terms]
```

**Advanced Tips:**
- Use the full JSON array format for `context_bias_terms`
- Cohere's medical terminology recognition is excellent with proper biasing
- Temperature of 0.1 is specifically recommended for medical dictation
- Consider using the local ONNX version for privacy and cost savings

**Known Limitations:**
- Language parameter is required (no auto-detect)
- Context bias limited to 100 terms
- No built-in speaker diarization

### Mistral Voxtral

**Strengths:**
- Fast transcription with good accuracy
- Supports real-time streaming
- Good language detection

**Optimal Configuration:**
```
Model: voxtral-mini-latest
Temperature: [not configurable]
Language: [optional, but recommended]
Context Bias Mode: none
Context Bias Terms: [not supported]
```

**Advanced Tips:**
- Use Realtime mode for live typing with minimal latency
- Adjust Streaming Delay (240-2400ms) to balance speed vs accuracy
- 480ms is recommended for most use cases
- 2400ms provides highest accuracy but more latency

**Known Limitations:**
- No temperature control
- No context biasing support
- Fewer language options than Whisper

### ElevenLabs Scribe

**Strengths:**
- High-quality transcription
- Speaker diarization support
- Entity detection and redaction
- Multi-channel audio processing

**Optimal Configuration:**
```
Model: scribe_v2
Temperature: 0.0-0.3
Language: [optional, auto-detects well]
Context Bias Mode: none
Context Bias Terms: [not supported]
```

**Advanced Tips:**
- Enable diarization for meeting transcription
- Use entity redaction for privacy-sensitive content
- Multi-channel processing can separate speakers
- Language auto-detection is excellent

**Known Limitations:**
- No context biasing support
- Higher cost than some alternatives
- Requires ElevenLabs API key

### Local ONNX (Cohere)

**Strengths:**
- Privacy (no data leaves your machine)
- No API costs
- Works offline
- GPU acceleration available

**Optimal Configuration:**
```
Model: cohere-transcribe-03-2026 (local)
Temperature: [not configurable]
Language: [required, set in code]
Context Bias Mode: none
Context Bias Terms: [not supported]
```

**Advanced Tips:**
- Use INT8 models for better accuracy (slightly slower)
- Ensure CUDA/cuDNN is properly installed for GPU acceleration
- 30-second chunking with 5-second overlap handles long audio
- CPU-only mode works but is slower

**Known Limitations:**
- No context biasing in current implementation
- Language must be set in code (not configurable via UI yet)
- Requires ~2GB disk space for model files
- Initial model loading takes 5-10 seconds

## Context Biasing Strategies

### Medical Domain

**Common Medical Terms:**
```
myocardial infarction
electrocardiogram
tachycardia
bradycardia
hypertension
hypotension
pneumonia
bronchitis
asthma
diabetes mellitus
hyperglycemia
hypoglycemia
intravenous
subcutaneous
intramuscular
diagnosis
prognosis
symptoms
treatment
medication
prescription
dosage
contraindication
side effect
allergic reaction
anaphylaxis
cardiopulmonary resuscitation
defibrillator
ventilator
intubation
extubation
hospitalization
discharge
follow-up
referral
consultation
laboratory
radiology
pathology
surgery
operation
procedure
recovery
rehabilitation
```

**Configuration:**
- Provider: Cohere Transcribe or OpenAI Whisper
- Temperature: 0.1 (Cohere) or 0.0 (Whisper)
- Post-Processing: Enable "Med Correction" for best results

### Technical/Programming Domain

**Common Programming Terms:**
```
function
variable
parameter
argument
return value
class
object
instance
method
property
interface
inheritance
polymorphism
encapsulation
abstraction
algorithm
data structure
array
list
dictionary
hash map
set
queue
stack
tree
graph
node
edge
database
table
column
row
primary key
foreign key
index
query
transaction
API
endpoint
request
response
JSON
XML
HTML
CSS
JavaScript
Python
Java
C sharp
TypeScript
React
Angular
Vue
Node.js
Express
Django
Flask
Spring Boot
.NET
Git
repository
branch
merge
commit
push
pull
deployment
container
Docker
Kubernetes
cloud
AWS
Azure
Google Cloud
```

**Configuration:**
- Provider: OpenAI Whisper or Cohere Transcribe
- Temperature: 0.0
- Post-Processing: Optional, may help with technical corrections

### Legal Domain

**Common Legal Terms:**
```
plaintiff
defendant
litigation
complaint
motion
hearing
deposition
subpoena
affidavit
testimony
evidence
exhibit
verdict
judgment
settlement
contract
agreement
clause
provision
liability
damages
compensation
injunction
restraining order
appeal
appellate
supreme court
district court
jurisdiction
statute
regulation
ordinance
precedent
case law
common law
civil law
criminal law
tort
negligence
breach of contract
intellectual property
patent
trademark
copyright
trade secret
non-disclosure agreement
confidentiality
privilege
attorney-client privilege
attorney work product
discovery
interrogatory
request for production
admission
expert witness
lay witness
hearsay
objection
sustained
overruled
```

**Configuration:**
- Provider: Cohere Transcribe (recommended) or OpenAI Whisper
- Temperature: 0.1 (Cohere) or 0.0 (Whisper)
- Post-Processing: Optional, may help with legal terminology

### General Business Domain

**Common Business Terms:**
```
revenue
profit
margin
expense
budget
forecast
quarter
fiscal year
stakeholder
shareholder
board of directors
CEO
CFO
CTO
manager
supervisor
employee
contractor
consultant
vendor
supplier
customer
client
lead
opportunity
deal
pipeline
conversion
retention
churn
acquisition
marketing
sales
operations
human resources
finance
accounting
legal
compliance
audit
risk management
strategic planning
key performance indicator
KPI
ROI
return on investment
net present value
internal rate of return
break-even point
market share
competitive analysis
SWOT analysis
strengths weaknesses opportunities threats
mission statement
vision statement
values
culture
diversity inclusion
sustainability
corporate social responsibility
CSR
```

**Configuration:**
- Provider: Any provider works well
- Temperature: 0.0-0.2
- Post-Processing: Usually not needed

## Domain-Specific Configurations

### Emergency Department Clinical Documentation

**Recommended Setup:**
```
Provider: Cohere Transcribe API
Model: cohere-transcribe-03-2026
Temperature: 0.1
Language: en
Context Bias Mode: cohere_terms
Post-Processing: ON (Med Correction)
```

**Why this configuration:**
- Cohere's model is optimized for medical terminology
- Low temperature (0.1) ensures deterministic output
- Context bias terms improve recognition of medical vocabulary
- Post-processing corrects common speech recognition errors

**Example Context Bias Terms:**
```
myocardial infarction, pericardial effusion, tachycardia, bradycardia,
hypertension, hypotension, pneumonia, bronchitis, asthma, COPD,
diabetes mellitus, hyperglycemia, hypoglycemia, intravenous,
subcutaneous, intramuscular, diagnosis, prognosis, treatment,
medication, prescription, dosage, contraindication, side effect,
allergic reaction, anaphylaxis, CPR, defibrillator, ventilator,
intubation, extubation, hospitalization, discharge, follow-up,
referral, consultation, laboratory, radiology, pathology
```

### Software Development Documentation

**Recommended Setup:**
```
Provider: OpenAI Whisper
Model: whisper-1
Temperature: 0.0
Language: en
Context Bias Mode: whisper_prompt
Post-Processing: OFF
```

**Why this configuration:**
- Whisper handles programming terminology well with proper biasing
- Zero temperature ensures consistent technical terms
- Context bias terms guide recognition of programming keywords
- Post-processing usually not needed for technical content

**Example Context Bias Terms:**
```
function, variable, parameter, argument, return value, class, object,
instance, method, property, interface, inheritance, polymorphism,
encapsulation, abstraction, algorithm, data structure, array, list,
dictionary, hash map, set, queue, stack, tree, graph, node, edge,
database, table, column, row, primary key, foreign key, index,
query, transaction, API, endpoint, request, response, JSON, XML,
HTML, CSS, JavaScript, Python, Java, C sharp, TypeScript, React,
Angular, Vue, Node.js, Express, Django, Flask, Spring Boot, .NET,
Git, repository, branch, merge, commit, push, pull, deployment
```

### Meeting Transcription

**Recommended Setup:**
```
Provider: ElevenLabs Scribe
Model: scribe_v2
Temperature: 0.2
Language: [auto-detect or explicit]
Context Bias Mode: none
Post-Processing: OFF
```

**Why this configuration:**
- ElevenLabs offers speaker diarization to identify different speakers
- Moderate temperature balances accuracy with natural speech patterns
- Language auto-detection handles multilingual meetings
- Diarization helps attribute statements to correct speakers

**Additional Settings (if available):**
- Enable diarization: ON
- Number of speakers: [estimated count]
- Timestamp granularity: segment

### Multilingual Dictation

**Recommended Setup:**
```
Provider: OpenAI Whisper
Model: whisper-1
Temperature: 0.0
Language: [primary language]
Context Bias Mode: whisper_prompt
Post-Processing: OFF
```

**Why this configuration:**
- Whisper has the best multilingual support
- Explicit language setting for primary language improves accuracy
- Context bias terms can include common phrases in secondary languages
- Zero temperature ensures consistent output

**Tips for multilingual use:**
- Set language to your most commonly spoken language
- Include common phrases from other languages in context bias terms
- Consider creating separate provider configurations for each language
- Switch providers based on current language context

## Troubleshooting Accuracy Issues

### Problem: Consistent misspelling of specific words

**Solution:**
1. Add the correctly spelled words to Context Bias Terms
2. Include common variations and misspellings
3. For medical/technical terms, use the full correct spelling

### Problem: Model transcribes wrong language

**Solution:**
1. Explicitly set the Language parameter in provider settings
2. Ensure the language code is correct (ISO 639-1 format)
3. For mixed-language content, set the primary language

### Problem: Too many hallucinations in silent regions

**Solution:**
1. Reduce temperature (lower = less hallucination)
2. Use Cohere's hallucination filter (if available)
3. Improve audio quality - reduce background noise
4. Enable post-processing correction

### Problem: Technical/medical terms not recognized

**Solution:**
1. Add terms to Context Bias Terms
2. Use both singular and plural forms
3. Include common abbreviations and their full forms
4. Consider using a provider with better domain support (Cohere for medical)

### Problem: Inconsistent transcription of the same phrase

**Solution:**
1. Reduce temperature to 0.0 for maximum determinism
2. Ensure consistent audio quality and speaking pattern
3. Add the phrase to Context Bias Terms
4. Check if post-processing is interfering

### Problem: Poor accuracy with accented speech

**Solution:**
1. Ensure language is set correctly
2. Add common phonetic variations to Context Bias Terms
3. Try different providers (some handle accents better)
4. Improve audio quality - use a good microphone

### Problem: Slow transcription speed

**Solution:**
1. For Mistral Realtime: increase Streaming Delay
2. For Local ONNX: use INT4 models or enable GPU
3. For cloud providers: check network connectivity
4. Consider using a faster model (e.g., voxtral-mini instead of full)

## Best Practices Summary

1. **Always set the language explicitly** - This is the single biggest accuracy improvement
2. **Use low temperature** - 0.0-0.1 for dictation, higher only for creative content
3. **Leverage context biasing** - Add domain-specific terms to improve recognition
4. **Choose the right provider** - Match provider to your domain (Cohere for medical, etc.)
5. **Enable post-processing** - Use medical correction for clinical documentation
6. **Maintain good audio quality** - Clear audio with minimal background noise
7. **Test and iterate** - Review transcriptions and adjust settings based on errors
8. **Keep bias terms updated** - Add new terms as you encounter them in your work
9. **Use appropriate mode** - Realtime for live typing, Batch for highest accuracy
10. **Monitor performance** - Check debug logs for issues and provider response times

## Additional Resources

- [OpenAI Whisper Documentation](https://platform.openai.com/docs/guides/speech-to-text)
- [Cohere Transcribe Documentation](https://docs.cohere.com/docs/audio-transcription-quickstart)
- [Mistral Audio Transcription](https://docs.mistral.ai/capabilities/audio_transcription)
- [ElevenLabs Speech-to-Text](https://elevenlabs.io/docs/speech-to-text)
- [WhisperInk GitHub Repository](https://github.com/praxeo/whisperinc)
