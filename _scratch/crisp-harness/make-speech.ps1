# Generates speech.wav (16 kHz mono PCM16) for the harness with Windows TTS.
# Pure ASCII on purpose: PowerShell 5.1 misreads BOM-less UTF-8.
Add-Type -AssemblyName System.Speech
$out = Join-Path (Split-Path -Parent $PSCommandPath) "speech.wav"
$s = New-Object System.Speech.Synthesis.SpeechSynthesizer
$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$s.SetOutputToWaveFile($out, $fmt)
$s.Speak("The patient has chest pain and shortness of breath since this morning.")
$s.Dispose()
Write-Host "wrote $out"
