# Conversation Telemetry — IVA-CUI (QoE thesis)

Per-run capture of every user↔agent conversational turn — key events with
timestamps, plus the transcript — so that **after** the study you can compute the
**actual time-to-response for each agent reply** and separate the network (netem)
component from the server-side generation time.

This is the companion to [NETEM_TRAFFIC.md](NETEM_TRAFFIC.md), which describes the
per-utterance wire cycle. This doc describes what the Unity client *records* about
that cycle and how it ships the record to the operator console (`qoe-lab`).

> Status: implemented on the Unity (device) side. The `qoe-lab` server already
> implements the receiving end (`POST /telemetry` + storage); the operator console
> currently keeps the telemetry *request* path dormant (see "Transport" below).

---

## What is captured

Every turn is one user utterance → one agent reply. For each turn the device
records the same latency stages already tracked in `SceneProfiling`, plus the
server-reported timings and the transcript:

| Event | Field | Source |
|---|---|---|
| Mic first pressed | `t_speak_start` | `SceneProfiling.speakStart` |
| Mic released (user done speaking) | `t_speak_end` | `SceneProfiling.speakEnd` — **analysis t0** |
| ASR upload POST sent (`:8083`) | `t_asr_start` | `SceneProfiling.asrStart` |
| ASR response received | `t_asr_end` | `SceneProfiling.asrEnd` |
| `/speak` GET sent (`:8000`) | `t_tts_req_start` | `SceneProfiling.ttsReqStart` |
| `/speak` response received | `t_tts_req_end` | `SceneProfiling.ttsReqEnd` |
| `/static` mp3 download begins | `t_tts_download_start` | `SceneProfiling.ttsVoiceDownloadStart` |
| Audio playback begins (**reply heard**) | `t_tts_play_start` | `SceneProfiling.ttsVoicePlayStart` |
| Audio playback ends | `t_tts_play_end` | **estimate**: `play_start + clip.length` |

Plus, threaded from the `/speak` JSON response (server-measured, **not** device
clock): `llm_generation_time_ms`, `speech_generation_time_ms`, `llm_client_name`,
`user_input_word_count`, `response_word_count`, `transition_length`,
`conversation_over`. And the content: `user_transcript` (the ASR result) and
`agent_reply` (the spoken text).

**Units.** All `t_*` and `d_*` are **seconds** (Unity `Time.time`, i.e. seconds
since app start — directly comparable to the existing `latency_log.csv`). The
server-reported `*_ms` fields are **milliseconds**. Word counts are integers.

**Clock base.** `Time.time` is relative to app start, so each run records a
`device_clock` base (`unix_ms_at_run_start` + `time_dot_time_at_run_start`,
captured back-to-back at run start). Convert any device timestamp to wall clock:

```
wall_ms(t) = unix_ms_at_run_start + (t - time_dot_time_at_run_start) * 1000
```

There is no device↔server clock-sync endpoint, so absolute alignment to the
operator/netem clock is best-effort. The sound cross-device measurement is the
*subtraction* method below, which cancels the clock offset.

---

## The core analysis: time-to-response

Device-observed time-to-response for a turn (from "user stops talking" to "reply
starts playing"):

```
d_total_response = t_tts_play_start - t_speak_end          (provided precomputed)
```

This spans the **three sequential netem round trips** (ASR upload → `/speak` →
`/static` fetch) *and* the server-side LLM + TTS generation. To isolate the
**network** portion, subtract the server-measured generation times:

```
network_component ≈ d_total_response
                    - (llm_generation_time_ms + speech_generation_time_ms) / 1000
```

Per-leg device-clock decomposition (all provided precomputed as `d_*`):

| Duration | Meaning |
|---|---|
| `d_user_speak` | how long the user spoke (`speakEnd - speakStart`) |
| `d_asr` | ASR round trip (`asrEnd - asrStart`) |
| `d_tts_req` | `/speak` round trip incl. server LLM+TTS (`ttsReqEnd - ttsReqStart`) |
| `d_tts_download` | mp3 fetch + decode to play start (`ttsVoicePlayStart - ttsVoiceDownloadStart`) |
| `d_total_response` | **the headline number** (`ttsVoicePlayStart - speakEnd`) |
| `d_audio_play` | reply playback length (estimate; see caveat) |

> **Caveat — `d_audio_play` / `t_tts_play_end`.** Playback end is estimated as
> `play_start + clip.length`, **not** a hardware "audio finished" callback. It can
> be wrong if a clip is cut short under impairment. Don't derive network timing
> from it.

---

## Both pipelines feed one collector

The project has two audio pipelines. Both now push turn-records into the same
collector (`QoeTurnLog`):

- **Zone pipeline** (City/Hotel/Museum agents): `StudyControls` → `ServerInterface`
  → `AgentSelectionController` → `StudyTasks`. Already recorded latency to
  `latency_log.csv`; the turn is captured at playback start in
  `AgentSelectionController.PlayAgentResponseAfterDelay`.
- **Training pipeline** (`TrainingSceneController`, its own `/speak/agent1` path):
  **previously recorded nothing**. It now sets the same `SceneProfiling` stages and
  records its turn at playback start, so a training turn produces an identical
  record (`pipeline: "training"`).

Recording at **playback start** (not `clip.length` later) is deliberate: every
time-to-response timestamp is already known there, and a run that ends mid-clip
(timer / Done / operator) can't drop that last — often most impaired — turn.

`AgentType` (Agent1/2/3) repeats across the three merged scenes, so the true agent
identity is `(backend_scene, task_index, agent_type)` — all three are in the
envelope.

---

## Transport

Mirrors the `qoe-lab` reference XR simulator (`XRDeviceSimulator.tsx → doUpload`):
the device uploads **only when the operator console asks**.

```
operator → device   WS /device   { "type": "request_telemetry",
                                    "data": { "condition_run_id", "session_id", "label", "experiment_id" } }
device   → server   HTTP          POST /telemetry   { "sid", "condition_run_id", ...envelope }
```

- The device answers a `request_telemetry` frame by POSTing the just-finalized
  run's envelope to `/telemetry`. The server requires `sid` + `condition_run_id`,
  strips them, and stores the rest verbatim as an opaque JSON blob.
- `409` ⇒ already stored (treated as success, no retry). An `{ignored:true}` ack
  ⇒ server dropped it (e.g. a training run) — also success. Otherwise a couple of
  retries, then give up — telemetry is best-effort and **never blocks the session**.
- **Debug Task buttons** run with no operator and no `sid`, so they never POST.
  Their envelope is still written to disk (below). The debug path is otherwise
  identical to a real run (`debug == real`).

> **Operator-console note (dormant request path).** `qoe-lab` implements the
> `request_telemetry` WS signal and the `POST /telemetry` endpoint, but its
> operator console hides the Designer "telemetry" data-collection toggle, so
> `telemetry` never enters a run's runtime `expectedDataSources` and the console
> never *emits* `request_telemetry` in a normal session. Until the lab un-hides
> that toggle, rely on the on-device JSON files below; the device is already wired
> to answer the request the moment the lab enables it. The console's CSV exporter
> also currently skips telemetry, so even POSTed rows won't appear in the exported
> bundle without a small `qoe-lab` change. No `qoe-lab` change was made for this
> feature.

---

## On-device files (always written)

Independent of the network POST, every finalized run is written as pretty-printed
JSON on the headset:

```
<Application.persistentDataPath>/Telemetry/telemetry_{sid|debug}_{conditionRunId}_{unixSeconds}.json
```

(mirroring `ConversationLogger`'s `Conversations/` convention). This is the
reliable retrieval path: pull the `Telemetry/` folder off the Quest after the
study. `ConversationLogger`'s separate transcript JSON is left untouched; the
transcript is duplicated into the telemetry turns so each file is self-contained.

---

## JSON schema

Regular JSON. The `samples` array (named so the server's `/telemetry` log line
reports the turn count, matching the simulator) holds one object per turn.

```jsonc
{
  // ---- per-run ENVELOPE ----
  "sid": "5f3c…uuid",          // string; from start_task. null for debug (never POSTed).
  "condition_run_id": 3,        // int; from start_task (== run.index).
  "schema_version": 1,
  "task_number": 3,             // int|null; null for training.
  "task_index": 3,              // int; 0=Training, 1..9 zone agents.
  "label": "Task 3",
  "backend_scene": "Shirts",    // Training | Shirts | Hotel | Museum.
  "pipeline": "zone",           // "zone" | "training".
  "is_debug_run": false,
  "device_kind": "quest3-unity",
  "device_clock": {
    "unix_ms_at_run_start": 1733400000000,   // long; wall clock at run start.
    "time_dot_time_at_run_start": 512.34      // float; Time.time at the same instant.
  },
  "run_start_time": 512.34,     // float s (Time.time) at Start press.
  "run_end_time":   678.90,     // float s (Time.time) at run end.
  "end_reason": "subject pressed Done", // "timer expired" | "operator ended early" | "subject pressed Done".
  "max_condition_duration_s": 120,

  // ---- per-turn records ----
  "samples": [
    {
      "turn_index": 0,
      "request_id": 481923,     // SceneProfiling.randomRequestId — joins to latency_log.csv.
      "agent_type": "Agent1",
      "pipeline": "zone",
      "complete": true,

      "t_speak_start": 515.10,
      "t_speak_end": 517.42,
      "t_asr_start": 517.45,
      "t_asr_end": 518.01,
      "t_tts_req_start": 518.03,
      "t_tts_req_end": 519.20,
      "t_tts_download_start": 519.21,
      "t_tts_play_start": 519.95,
      "t_tts_play_end": 523.10,            // estimate (play_start + clip.length).

      "d_user_speak": 2.32,
      "d_asr": 0.56,
      "d_tts_req": 1.17,
      "d_tts_download": 0.74,
      "d_total_response": 2.53,            // headline: time-to-response.
      "d_audio_play": 3.15,                // estimate.

      "llm_generation_time_ms": 842.117,
      "speech_generation_time_ms": 311.004,
      "llm_client_name": "ollama-llama3",
      "user_input_word_count": 11,
      "response_word_count": 27,
      "transition_length": 4,
      "conversation_over": false,

      "user_transcript": "hey how's it going today",
      "agent_reply": "Doing great, thanks for asking!"
    }
    // … one object per turn, in order …
  ]
}
```

---

## Where the code lives

| File | Role |
|---|---|
| `Assets/Scripts/QoE/QoeTurnLog.cs` | The collector. `BeginRun` / `RecordTurn` / `FinishRun`; serialization + disk write; holds the last run for an inbound `request_telemetry`. |
| `Assets/Scripts/QoE/QoeDeviceClient.cs` | `BeginRun` at `OnStartPressed`; `FinishRun` in `FinishRun`; `OnRequestTelemetry` + `PostTelemetry` answer the WS request. |
| `Assets/Scripts/AgentSelectionController.cs` | Zone pipeline: `RecordTurn` at playback start. |
| `Assets/Scripts/StudyControls.cs` | Zone pipeline: stashes the user transcript on ASR finish. |
| `Assets/Scripts/SceneSpecific/TrainingSceneController.cs` | Training pipeline: adds the `SceneProfiling` stages it was missing + `RecordTurn`. |

`SceneProfiling.cs` (per-turn `latency_log.csv`) and `ConversationLogger.cs`
(transcript JSON) are unchanged and continue to work alongside this.

---

## Limitations

- **No clock sync.** Device timestamps are uncorrected `Time.time`; cross-device
  alignment relies on the per-run clock base (best-effort) or the subtraction
  method. Fine for per-turn latency; not for absolute device↔operator alignment.
- **`t_tts_play_end` is an estimate** (clip length), not a real playback-finished
  callback.
- **Successful turns only.** A turn is recorded when its reply plays; turns that
  fail/time out under impairment leave no per-turn record (the run-level timer and
  `end_reason` still bound the run). Matches the `qoe-lab` simulator's behavior.
- **One row per run** server-side (`UNIQUE` on `condition_run_id`); a re-POST is a
  `409` no-op.
