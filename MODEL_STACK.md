# Model Stack (agent backend)

Every model in the voice pipeline, as actually configured. Read-only audit — no
code was changed.

The server is a **sibling repo**, not part of `iva-cui`:
`C:/Users/ejnhizu/sources/macos-local-voice-agents` (runs on the Mac at
`/Users/lambo/Developer/macos-local-voice-agents`). All `bot.py` line numbers
below are in `server/`.

## The five stages

| Stage | Model | Version / precision | Pinned at |
|---|---|---|---|
| VAD | Silero VAD (ONNX, bundled in pipecat) | via `onnxruntime` 1.24.4 | `bot.py:130` — `VADParams(stop_secs=0.2)` |
| Turn-taking | smart-turn — loads `smart-turn-v3.2-cpu.onnx` | **v3.2**, CPU ONNX | `bot.py:123` — `LocalSmartTurnAnalyzerV3(SmartTurnParams(stop_secs=1.0))` |
| STT | `mlx-community/whisper-large-v3-turbo-q4` | large-v3-turbo, **q4** | `bot.py:85` via `MLXModel.LARGE_V3_TURBO_Q4` |
| LLM | `lmstudio-community/Meta-Llama-3.1-8B-Instruct-GGUF` | 8B instruct, **Q5_K_M** | served by LM Studio; `bot.py:90-97` |
| TTS | `mlx-community/Kokoro-82M-bf16` | 82M, **bf16**, 24 kHz mono | `bot.py:87`; `voices.py:6` |

Two of these are hidden behind enums in the source and were resolved from the
runtime log, not from the constant name:

- `MLXModel.LARGE_V3_TURBO_Q4` → `mlx-community/whisper-large-v3-turbo-q4`.
- `LocalSmartTurnAnalyzerV3` → `.../smart_turn/data/smart-turn-v3.2-cpu.onnx`.
  **The class says V3 but the weights are v3.2** — cite the file, not the class.

## LLM

`lmstudio-community/Meta-Llama-3.1-8B-Instruct-GGUF`, quant **Q5_K_M**
(`Meta-Llama-3.1-8B-Instruct-Q5_K_M.gguf`), served locally by LM Studio on
`127.0.0.1:1234` over the OpenAI-compatible API, `max_tokens=4096`
(`bot.py:90-97`). The `model="local-model"` string in the code is a placeholder
LM Studio ignores — it serves whatever is loaded in the GUI — so code and logs
both refer to this stage only as `local-model`.

Sampling settings (temperature, top-p, context length) are set in LM Studio
rather than in this repo.

## Not used

- `Marvis-AI/marvis-tts-250m-v0.1` — commented out at `bot.py:88`;
  `marvis_worker.py` is dead code.
- Kokoro is a **single** model. The ~20 "voices" are small per-voice embeddings,
  one assigned per agent in `agents_config.py` (`voices.py` is the single source
  of truth, shared with `prewarm.py`).

## Framework versions

Pipecat **1.3.0** on **Python 3.12.12** (Clang 17) — from the log banner. From
`server/uv.lock`: `mlx-whisper` 0.4.2, `mlx-audio` 0.2.4, `mlx-lm` 0.26.3,
`aiortc` 1.13.0, `av` 14.4.0, `numpy` 2.2.6, `onnxruntime` 1.24.4,
`pipecat-ai-small-webrtc-prebuilt` 1.0.0.

Requirement is `pipecat-ai[openai,deepgram,rime,silero,mlx-whisper]>=1.3.0` —
floating, so the lockfile is what fixes 1.3.0. The `deepgram` and `rime` extras
are installed but unused (no cloud STT/TTS in this pipeline).

## Provenance

Sources for the table: `server/bot.py`, `server/voices.py`,
`server/pyproject.toml`, `server/uv.lock`, and `log.txt` (a real session,
2026-06-17) for the two enum values and the version banner.

Caveat for future audits: `server/.venv/` in the Windows checkout is an **empty
stub** (2 files). Package internals cannot be resolved from this tree — read them
on the Mac, or from the runtime log as done here.
