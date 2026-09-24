# WhisperInk Transcription Accuracy Guide

This guide explains how to maximize transcription accuracy across all supported providers in WhisperInk.

> **Status (2026-09):** parts of this guide predate the 2026-06 refactor that made context-bias routing automatic (one shared list, sent to each provider's native field — there is no per-provider "Context Bias Mode" to pick any more) and removed the realtime mode, AI edit and med-correction. The biasing tables, the Cohere / Mistral / ElevenLabs sections and the medical / ED recommendations were corrected in 2026-09. Where anything here disagrees with `CLAUDE.md` → *Provider system*, CLAUDE.md is authoritative — it tracks the code.

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
- **Cohere Transcribe** - Strong English accuracy; **no vocabulary biasing** (the v2 API has no biasing field)
- **Mistral Voxtral** - Fast, accurate transcription with language detection
- **ElevenLabs Scribe** - High-accuracy transcription with large keyterm lists (up to 1000 terms)
- Also: Deepgram Nova-3 (and Nova-3 Medical), Google Chirp 3, Soniox, and local models via CrispASR (Parakeet, Cohere, Voxtral, Granite, Qwen3-ASR) — see `CLAUDE.md` for the full list

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

**Provider support** — the one shared list is routed automatically to each provider's native field (`ApiProvider.BiasMechanism`, baked per provider):
| Provider | Mechanism | Limit | Notes |
|----------|-----------|-------|-------|
| OpenAI Whisper | `whisper_prompt` | ~224 tokens | Labeled glossary in `prompt` |
| Mistral Voxtral (cloud) | `mistral_context_bias` | 100 terms | Comma string in `context_bias` |
| ElevenLabs Scribe | `elevenlabs_keyterms` | 1000 terms, each < 50 chars and ≤ 5 words | Repeated `keyterms` fields; ~20% cost surcharge when used |
| Deepgram Nova-3 / Medical | `deepgram_keyterm` | 100 terms | `keyterm` query params |
| Soniox | `context_terms` | 100 terms | `context.terms` |
| Google Chirp 3 | `phrase_sets` | — | `adaptation.phraseSets` |
| Local CrispASR | `hotwords` | — | Parakeet: phrase-boost trie (boost is opt-in — it can garble neighboring words); Voxtral 3B / Qwen3: injected into the model's prompt, so keep the list short; Cohere / Granite / Voxtral 4B: no effect |
| Cohere Transcribe (cloud) | none | — | The v2 API has no biasing field (the old `cohere_terms` was silently ignored and has been removed) |

A large list only helps providers that take one: ElevenLabs accepts 1000 terms, most others stop at 100, and a long list injected into an LLM decoder's prompt (Voxtral 3B, Qwen3) can hurt more than it helps.

### Context Bias Mode

There is nothing to choose any more: each built-in provider's mechanism is fixed (table above) and shown read-only in *Providers…*. The old per-provider "Context Bias Mode" setting survives only as the fallback for providers you add yourself.

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
- Strong English accuracy
- Deterministic output with low temperature
- Also runs locally (CrispASR GGUF presets) with no per-minute cost

**Optimal Configuration:**
```
Model: cohere-transcribe-03-2026
Temperature: 0.1
Language: [required, e.g., "en"]
Context Bias: none — the API has no biasing field
```

**Advanced Tips:**
- Temperature 0.1 is the preset default

**Known Limitations:**
- **No vocabulary biasing.** The v2 API has no biasing field: the `cohere_terms` → `context_bias_terms` field this guide used to recommend was silently dropped by the server and has been removed. Rare terms can't be steered.
- Language parameter is required (no auto-detect)
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
Context Bias: automatic — sent as `context_bias` (up to 100 terms)
```

**Known Limitations:**
- No temperature control
- Context bias capped at 100 terms
- Fewer language options than Whisper
- (The realtime/streaming mode described in older versions of this guide was removed in 2026-06.)

### ElevenLabs Scribe

**Strengths:**
- High accuracy on clinical dictation — it is the engine behind the production ED dictation web app (`praxeo/elevenlabs-web`)
- **Keyterms:** up to 1000 terms (each < 50 chars, ≤ 5 words) — by far the largest biasing list of any provider here
- Word-level timestamps and speaker diarization available

**Optimal Configuration (dictation):**
```
Model: scribe_v2
Temperature: 0             (sent automatically unless you set another in Providers…)
Language: en               (sent as language_code; "auto" lets Scribe detect it)
Context Bias: the shared list, plus the "ElevenLabs-only keyterms" box in Providers… —
              put long specialty lists (drug names, dressings) in that box
no_verbatim: on            (strips um/uh — preset default)
tag_audio_events: off      (no "(laughter)"/"(cough)" tags in the text — preset default)
```

**Advanced Tips:**
- WhisperInk sends the request shape elevenlabs-web runs in production: `language_code`, `temperature=0`, single speaker (`diarize=false`, `num_speakers=1`) and word timestamps, which feed the incomplete-transcript check (a result that stops well short of the speech warns instead of passing as complete). Pinning the language matters on short clips, where auto-detection can drift into another language.
- The transcript gets elevenlabs-web's cleanup before pasting: Scribe's pause ellipses (`…` / `...`) and line breaks become spaces.
- Keyterms add roughly 20% to the per-minute cost; spend them on words the model actually gets wrong (drug names, eponyms, abbreviations). Long lists belong in the ElevenLabs-only box: the global Context Bias list reaches every provider, most of which cap it at 100 terms, and the local speech-LLMs (Qwen3-ASR, Voxtral) write it into their prompt, where a long list dilutes.

**Known Limitations:**
- Higher cost than some alternatives (more with keyterms)
- Requires an ElevenLabs API key

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
- Provider: ElevenLabs Scribe (keyterms take up to 1000 terms — the only provider here where a long medical list is fully used); Deepgram Nova-3 Medical is the alternative with a medical model (keyterms capped at 100)
- Temperature: 0 (set it in Providers… for ElevenLabs)
- Cohere Transcribe has no biasing field, so this list does nothing there; "Med Correction" post-processing was removed in 2026-06

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
Provider: ElevenLabs Scribe
Model: scribe_v2
Temperature: 0
ElevenLabs-only keyterms: your ED vocabulary (drug names, eponyms, abbreviations)
no_verbatim: on
tag_audio_events: off
```

**Why this configuration:**
- It is the request elevenlabs-web runs in production for ED dictation — English pinned, single speaker, temperature 0, word timestamps — and WhisperInk now sends the same (see the ElevenLabs section)
- ElevenLabs is the only provider here that accepts a full medical keyterm list (1000 terms); most others cap at 100
- Temperature 0 keeps output deterministic
- The previous recommendation here (Cohere Transcribe with `cohere_terms` and "Med Correction") no longer works: Cohere's API has no biasing field — the terms were silently ignored — and med-correction was removed from the app

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
1. For cloud providers: check network connectivity
2. Consider using a faster model (e.g., voxtral-mini instead of full)
3. For local CrispASR models: the first dictation after switching pays the server start (roughly 1.5–3 s on CUDA); later ones are warm. Slow warm times on a laptop usually mean the Balanced power plan (see CLAUDE.md gotchas)

## Best Practices Summary

1. **Always set the language explicitly** - This is the single biggest accuracy improvement
2. **Use low temperature** - 0.0-0.1 for dictation, higher only for creative content
3. **Leverage context biasing** - Add domain-specific terms to improve recognition
4. **Choose the right provider** - Match provider to your domain (ElevenLabs Scribe with keyterms for clinical dictation, etc.)
5. **Maintain good audio quality** - Clear audio with minimal background noise
6. **Test and iterate** - Review transcriptions and adjust settings based on errors
7. **Keep bias terms updated** - Add new terms as you encounter them in your work
8. **Monitor performance** - Check debug logs for issues and provider response times

## Additional Resources

- [OpenAI Whisper Documentation](https://platform.openai.com/docs/guides/speech-to-text)
- [Cohere Transcribe Documentation](https://docs.cohere.com/docs/audio-transcription-quickstart)
- [Mistral Audio Transcription](https://docs.mistral.ai/capabilities/audio_transcription)
- [ElevenLabs Speech-to-Text](https://elevenlabs.io/docs/speech-to-text)
- [WhisperInk GitHub Repository](https://github.com/praxeo/whisperinc)
