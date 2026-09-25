#requires -Version 7
<#
  _tekken_ids.ps1 - checks bias terms against a Voxtral GGUF's own Tekken
  vocabulary, the way CrispASR's Voxtral 3B runtime tokenizes them
  (src/voxtral.cpp, v0.8.30 through main on 2026-09-24): every one of the
  150,000 serialized entries may be merged, but the model has embedding
  rows for ids below 131,072 only (1,000 specials + 130,072 entries). A term
  whose merges reach past that gets an id with no embedding row. On
  2026-09-24 "epigastric" (astric = 146,371) made Voxtral 3B answer every
  take with a page of <unk>. See CLAUDE.md 10.2.

  Each term is checked as it appears in the prompt the voxtral backend
  builds ("The following words may appear: a,b,c."): split with Tekken's
  pre-tokenizer pattern, then the same lowest-rank-first BPE merge.

  USAGE    pwsh .\_tekken_ids.ps1                          # the shared list, the 3B GGUF
           pwsh .\_tekken_ids.ps1 -Words epigastric,melena
           pwsh .\_tekken_ids.ps1 -Gguf <a Voxtral .gguf>
#>
param([string[]]$Words, [string]$Gguf)
$ErrorActionPreference = 'Stop'
if (-not $Gguf) {
  $Gguf = (Get-ChildItem (Join-Path $env:APPDATA '.WhisperInk\cohere-gguf\voxtral-mini-3b*.gguf') | Select-Object -First 1).FullName
  if (-not $Gguf) { throw 'no voxtral-mini-3b*.gguf in the model folder; pass -Gguf' }
}
$Words = @($Words | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if (-not $Words) {
  $cfg = Get-Content (Join-Path $env:APPDATA '.WhisperInk\config.json') -Raw | ConvertFrom-Json
  $Words = @($cfg.ContextBiasTerms | Where-Object { $_ })
}

Add-Type -TypeDefinition @'
using System; using System.IO; using System.Text; using System.Collections.Generic; using System.Text.RegularExpressions;
public static class TekkenCheck {
    static string Str(BinaryReader br) { ulong n = br.ReadUInt64(); return Encoding.UTF8.GetString(br.ReadBytes((int)n)); }
    static object Val(BinaryReader br, uint t) {
        switch (t) {
            case 0: return br.ReadByte(); case 1: return br.ReadSByte(); case 2: return br.ReadUInt16(); case 3: return br.ReadInt16();
            case 4: return br.ReadUInt32(); case 5: return br.ReadInt32(); case 6: return br.ReadSingle(); case 7: return br.ReadByte();
            case 8: return Str(br);
            case 9: { uint et = br.ReadUInt32(); ulong n = br.ReadUInt64(); for (ulong i = 0; i < n; i++) Val(br, et); return null; }
            case 10: return br.ReadUInt64(); case 11: return br.ReadInt64(); case 12: return br.ReadDouble();
        }
        throw new Exception("unknown GGUF value type " + t);
    }
    public static int NSpecials, NEntries, LlmVocab;
    static readonly Dictionary<string, int> Rank = new Dictionary<string, int>();
    static readonly Encoding L1 = Encoding.Latin1;
    // Tekken's pre-tokenizer pattern (the o200k-style split mistral-common uses).
    static readonly Regex Pre = new Regex(
        @"[^\r\n\p{L}\p{N}]?[\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{M}]*[\p{Ll}\p{Lm}\p{Lo}\p{M}]+|[^\r\n\p{L}\p{N}]?[\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{M}]+[\p{Ll}\p{Lm}\p{Lo}\p{M}]*|\p{N}| ?[^\s\p{L}\p{N}]+[\r\n/]*|\s*[\r\n]+|\s+(?!\S)|\s+");

    public static void Load(string path) {
        using (var fs = File.OpenRead(path)) using (var br = new BinaryReader(fs)) {
            if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "GGUF") throw new Exception(path + " is not a GGUF");
            br.ReadUInt32(); ulong nTensors = br.ReadUInt64(); ulong nKv = br.ReadUInt64();
            long align = 32; int nVocab = 150000; NSpecials = 1000; LlmVocab = 131072;
            for (ulong i = 0; i < nKv; i++) {
                string key = Str(br); object v = Val(br, br.ReadUInt32());
                if (key == "general.alignment") align = Convert.ToInt64(v);
                if (key == "tokenizer.tekken.n_specials") NSpecials = Convert.ToInt32(v);
                if (key == "tokenizer.tekken.n_vocab") nVocab = Convert.ToInt32(v);
                if (key == "voxtral.llm.vocab_size") LlmVocab = Convert.ToInt32(v);
            }
            long off = -1, n = 0; uint type = 0;
            for (ulong i = 0; i < nTensors; i++) {
                string name = Str(br); uint nd = br.ReadUInt32(); long ne0 = 1;
                for (uint d = 0; d < nd; d++) { long ne = (long)br.ReadUInt64(); if (d == 0) ne0 = ne; }
                uint t = br.ReadUInt32(); long o = (long)br.ReadUInt64();
                if (name == "tokenizer.tekken.vocab_tensor") { off = o; n = ne0; type = t; }
            }
            if (off < 0) throw new Exception("no tokenizer.tekken.vocab_tensor: not a Voxtral GGUF?");
            if (type != 0) throw new Exception("tokenizer.tekken.vocab_tensor is ggml type " + type + ", expected F32");
            fs.Position = (fs.Position + align - 1) / align * align + off;
            var blob = new byte[n];
            for (long i = 0; i < n; i++) blob[i] = (byte)(int)br.ReadSingle();
            // As tekken_build_reverse: every serialized entry becomes mergeable.
            long pos = 0; int r = 0;
            for (; r < nVocab && pos + 2 <= n; r++) {
                int len = blob[pos] | (blob[pos + 1] << 8); pos += 2;
                if (pos + len > n) break;
                Rank[L1.GetString(blob, (int)pos, len)] = r;
                pos += len;
            }
            NEntries = r;
        }
    }

    // As tekken_bpe_encode: merge the adjacent pair with the lowest rank until none is left.
    static List<KeyValuePair<string, int>> Bpe(byte[] data) {
        var pieces = new List<int[]>();
        for (int i = 0; i < data.Length; i++) pieces.Add(new[] { i, 1 });
        while (pieces.Count > 1) {
            int best = int.MaxValue, at = -1;
            for (int i = 0; i + 1 < pieces.Count; i++) {
                int rr;
                if (Rank.TryGetValue(L1.GetString(data, pieces[i][0], pieces[i][1] + pieces[i + 1][1]), out rr) && rr < best) { best = rr; at = i; }
            }
            if (at < 0) break;
            pieces[at][1] += pieces[at + 1][1]; pieces.RemoveAt(at + 1);
        }
        var ids = new List<KeyValuePair<string, int>>();
        foreach (var p in pieces) {
            int rr; int id = Rank.TryGetValue(L1.GetString(data, p[0], p[1]), out rr) ? rr + NSpecials : 0;
            ids.Add(new KeyValuePair<string, int>(Encoding.UTF8.GetString(data, p[0], p[1]), id));
        }
        return ids;
    }

    // One term as it sits in the joined list: a leading space for the first, a comma for the rest.
    public static string Check(string term, bool first, out bool bad) {
        bad = false; var sb = new StringBuilder();
        foreach (Match m in Pre.Matches((first ? " " : ",") + term)) {
            foreach (var kv in Bpe(Encoding.UTF8.GetBytes(m.Value))) {
                bool past = kv.Value >= LlmVocab; bad |= past;
                sb.Append("[" + kv.Key + "]=" + kv.Value + (past ? "!" : "") + " ");
            }
        }
        return sb.ToString().TrimEnd();
    }
}
'@

[TekkenCheck]::Load($Gguf)
"$(Split-Path $Gguf -Leaf): $([TekkenCheck]::NSpecials) specials + $([TekkenCheck]::NEntries) entries, but the model has $([TekkenCheck]::LlmVocab) ids; ! marks an id past them"
$bad = @()
for ($i = 0; $i -lt $Words.Count; $i++) {
  $isBad = $false
  $ids = [TekkenCheck]::Check($Words[$i], $i -eq 0, [ref]$isBad)
  '{0,-3} {1,-20} {2}' -f $(if ($isBad) { '!!' } else { '' }), $Words[$i], $ids
  if ($isBad) { $bad += $Words[$i] }
}
if ($bad) { "`n$($bad.Count) of $($Words.Count) terms get ids past the vocabulary: $($bad -join ', ')" }
else { "`nNone of the $($Words.Count) terms gets an id past the vocabulary." }
