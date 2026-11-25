"""Run CrispASR's convert-qwen3-asr-to-gguf.py (v0.8.38, unmodified) without torch or safetensors.

The converter only uses safetensors.safe_open(...).keys()/get_tensor() and torch.bfloat16 / .float().numpy().
This shim supplies both from a plain safetensors reader: BF16 -> float32 is exact (bf16 is the top half
of an f32), so the arrays the converter then casts to F16/F32 are the ones torch would have given it.

Needs only numpy and gguf (both installed for py 3.13 on the desktop). Used 2026-11-23 for
yuriyvnv/Qwen3-ASR-1.7B-EN-Medical; the result matched cstr's base Qwen3 Q4_K in every metadata key,
tensor name, type and shape after `crispasr-quantize <f16> <out> q4_k`.

usage:
  gh api "repos/CrispStrobe/CrispASR/contents/models/convert-qwen3-asr-to-gguf.py?ref=v0.8.38" --jq .content | base64 -d > conv.py
  py -I _qwen3_convert.py conv.py --input <hf model dir> --output <model-f16.gguf>
Keep the downloaded model in its own folder outside cohere-gguf\ (and outside the repo).
"""
import json
import runpy
import struct
import sys
import types

import numpy as np


class _BF16:
    def __repr__(self):
        return "bfloat16"


torch = types.ModuleType("torch")
torch.bfloat16 = _BF16()
sys.modules["torch"] = torch

_NP = {"F32": np.float32, "F16": np.float16, "F64": np.float64, "I64": np.int64, "I32": np.int32,
       "I16": np.int16, "I8": np.int8, "U8": np.uint8, "BOOL": np.bool_}


class _Tensor:
    def __init__(self, arr, bf16):
        self._arr = arr
        self.dtype = torch.bfloat16 if bf16 else arr.dtype

    def float(self):
        return self

    def numpy(self):
        return self._arr


class _SafeOpen:
    def __init__(self, path, framework="pt", device="cpu"):
        self._f = open(path, "rb")
        n = struct.unpack("<Q", self._f.read(8))[0]
        self._hdr = json.loads(self._f.read(n))
        self._hdr.pop("__metadata__", None)
        self._base = 8 + n
        self._mm = np.memmap(path, dtype=np.uint8, mode="r")

    def __enter__(self):
        return self

    def __exit__(self, *a):
        self._f.close()
        del self._mm

    def keys(self):
        return sorted(self._hdr)

    def get_tensor(self, name):
        h = self._hdr[name]
        a, b = h["data_offsets"]
        raw = self._mm[self._base + a:self._base + b]
        shape = tuple(h["shape"])
        if h["dtype"] == "BF16":
            u = np.frombuffer(raw.tobytes(), dtype=np.uint16).astype(np.uint32) << 16
            return _Tensor(u.view(np.float32).reshape(shape), True)
        return _Tensor(np.frombuffer(raw.tobytes(), dtype=_NP[h["dtype"]]).reshape(shape).copy(), False)


st = types.ModuleType("safetensors")
st.safe_open = _SafeOpen
sys.modules["safetensors"] = st

sys.stdout.reconfigure(encoding="utf-8")  # the converter prints arrows; the console is cp1252
sys.stderr.reconfigure(encoding="utf-8")
conv = sys.argv[1]
sys.argv = [conv] + sys.argv[2:]
runpy.run_path(conv, run_name="__main__")
