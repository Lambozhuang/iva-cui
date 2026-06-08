# Unity ↔ Pipecat WebRTC Frontend — Plan

Replace the old ASR→LLM→TTS HTTP voice pipeline with a **WebRTC connection from Unity to a
Pipecat voice-agent backend running on a Mac**. The Mac runs the
[`macos-local-voice-agents`](https://github.com/kwindla/macos-local-voice-agents) demo **as-is**
(SmallWebRTCTransport + RTVI; Silero VAD, smart-turn, Whisper, local LLM, Kokoro TTS). Unity is
**only the frontend** — it does what the demo's thin React web client does, reimplemented in C#.

**Primary goal: Unity behaves like the macOS Pipecat web client.** Full-duplex, open-mic,
interruption/barge-in working — same backend, same protocol, same conversational behavior.

**netem** sits on the Unity(Windows) ↔ Mac LAN hop → controlled local baseline + real full-duplex
WebRTC streaming.

Branch: `qoe/realtime-webrtc` (forked from `main`).

> **QoE logging is deferred — we decide how to log later.** Do NOT design the build around the old
> QoE harness. First prove the audio pipeline, then layer features on. The old `SceneProfiling` /
> `QoeTurnLog` machinery stays untouched for now; we revisit instrumentation once the pipeline works.

---

## 1. Architecture

```
  Windows study PC                      netem                  Mac (backend, AS-IS)
  ┌────────────────────┐         ┌──────────────────┐   ┌───────────────────────────┐
  │ Unity frontend      │         │ LAN hop          │   │ bot.py (Pipecat):         │
  │  com.unity.webrtc   │◄───────►│  delay/jitter/   │◄─►│  SmallWebRTCTransport      │
  │  PipecatClient.cs   │ WebRTC: │  loss/bandwidth  │   │  Silero VAD + smart-turn   │
  │  + avatar / lipsync │ Opus    │                  │   │  Whisper STT               │
  │  + (QoE later)      │ audio + │                  │   │  local LLM (LM Studio)     │
  │                     │ "chat"  │                  │   │  Kokoro TTS                │
  └────────────────────┘ data ch └──────────────────┘   │  FastAPI POST /api/offer   │
                                                          └───────────────────────────┘
```

- **Transport:** WebRTC (Opus audio both ways + an SCTP data channel named `"chat"`).
- **Signaling:** single HTTP `POST /api/offer` to the Mac (no auth, LAN). aiortc is **non-trickle**.
- **Protocol on the data channel:** RTVI (envelope `{id, label:"rtvi-ai", type, data}`).
- **Interruption/barge-in:** built into the backend (VAD-driven). Works **iff the mic stays open
  during bot speech** (full-duplex). The only client requirement is: never gate the mic.
- **No cloud, no API key, no token server.** Everything local.

---

## 2. Confirmed facts (verified this session)

- **`com.unity.webrtc` `3.0.0-pre.8`** works on the project's Unity (user-confirmed). It has
  `AudioStreamTrack.onReceived` (added ≥ `pre.6`), needed for the lip-sync tap later.
- Backend `/api/offer` body: `{ "sdp", "type":"offer", "pc_id", "restart_pc" }`; answer:
  `{ "sdp", "type":"answer", "pc_id" }`. (`bot.py`)
- Backend defaults to `--host localhost` → **must be started with `--host 0.0.0.0`** to accept LAN
  connections. One CLI flag, no code change.
- Backend greets only after the client sends RTVI **`client-ready`** (`on_client_ready` →
  `set_bot_ready()` + queues the opening line). No `client-ready` ⇒ agent never speaks.
- Keepalive: client must send a `"ping"` string ~1 Hz (server `is_connected()` has a 3 s window).
- `pipecat-ai==0.0.81` is pinned (`uv.lock`) → RTVI **protocol version `"1.0.0"`** (send that).
- Newtonsoft JSON (`com.unity.nuget.newtonsoft-json`) is already in the Unity manifest.

**Still empirical (settle by running):**
- Full audio round-trip Unity↔aiortc on this exact stack (signaling/ICE/DTLS interop is
  high-confidence; media flow is the thing to prove). → **that's what the PoC is for.**
- Exact RTVI v0.0.81 field names (message *shapes* read from a newer branch) → log inbound messages
  on first live turn.

---

## 3. Prior art — reference only, do NOT copy

- **`macos-local-voice-agents`** (the Mac backend) — run as-is. The React client
  (`<ConsoleTemplate transportType="smallwebrtc" .../>`) is the behavior target; all its logic is
  inside `@pipecat-ai/small-webrtc-transport` + `@pipecat-ai/client-js`.
- **`stefanwebb/unity-voice-agents`** — the only Unity↔Pipecat example. **CC-BY-SA-4.0 (viral) → do
  not paste any of it.** Useful only as a read-only sketch of the offer/answer dance. It is
  **text-only** (TTS disabled, inbound audio discarded), POSTs before ICE completes (breaks off
  loopback), uses blocking HTTP, never sends `client-ready`, no keepalive, no reconnect, string-slice
  JSON. It proves Unity-WebRTC↔aiortc *connects*, nothing more. Take WebRTC sample plumbing from
  upstream `com.unity.webrtc`, not from this repo.
- **Porting references (better-licensed):** `pipecat-client-android` (BSD-2; handshake + `client-ready`
  ordering), `pipecat-client-cxx/rtvi_messages.h` (message catalog), `gtk2k/Unity_aiortc_sample` (MIT;
  the ICE-gathering-wait pattern).

---

## 4. What we build (Unity C#) — and why it's ours

No C# Pipecat SDK exists, so the frontend is hand-written:

1. **SmallWebRTC signaling** (`PipecatClient.cs`): `RTCPeerConnection(iceServers:[])` →
   `CreateDataChannel("chat", ordered)` → add `sendrecv` audio transceiver (mic track) →
   `CreateOffer`/`SetLocalDescription` → **wait for `IceGatheringState==Complete`** (poll or
   candidate-null marker, ~1–2 s timeout) → read `pc.LocalDescription.sdp` → **async** POST
   `/api/offer` (`UnityWebRequest`/`HttpClient`, NOT blocking) → `SetRemoteDescription(answer)`.
   *The ICE-complete wait is the #1 hazard; getting it wrong = silent no-media on a real LAN.*
2. **Outbound mic track:** `Microphone.Start` → `AudioStreamTrack` fed via `SetData` in
   `OnAudioFilterRead`, 48 kHz Opus aligned, `AddTrack`. **Always open** (never gate `track.Enabled`
   during bot speech — that would kill barge-in).
3. **Inbound audio:** `pc.OnTrack` → `AudioSource.SetTrack(track)` for playback (main-thread
   marshalled). *(Spatial routing to the avatar + OVR Lip Sync split-tap come later.)*
4. **RTVI layer over `"chat"`:** `OnMessage` (`byte[]`→UTF-8→Newtonsoft) on envelope
   `{id,label:"rtvi-ai",type,data}`. Send **`client-ready`** (`data:{version:"1.0.0",
   about:{library:"unity-webrtc"}}`) on channel-open; wait `bot-ready`; send 1 Hz `"ping"`.
   Dispatch inbound types (`user-transcription`, `bot-output`/`bot-tts-text`,
   `user/bot-started/stopped-speaking`, `metrics`, `signalling`:`peerLeft`/`renegotiate`).
5. **Session lifecycle:** teardown (`RemoveTrack`/`Dispose`/`Close`) and reconnect via `pc_id` —
   needed later for per-agent switching.

**Free (not ours):** the entire AI pipeline (VAD, STT, LLM, TTS, **interruption**) runs unmodified on
the Mac. Avatars/scenes stay as-is.

---

## 4b. Transport abstraction — Pipecat now, OpenAI-Realtime-compatible later

Both Pipecat and OpenAI Realtime are **WebRTC**, so the client is ~70–80% the same regardless of
backend. Structure the PoC behind **one interface from the start** so a second backend is "write
another adapter," not "rewrite the client." Designing this in now costs ~nothing; building both now
does not — so **build Pipecat first**, OpenAI later.

**Layered reuse:**
| Layer | Pipecat | OpenAI Realtime | Shared? |
|---|---|---|---|
| WebRTC peer + media (mic out, audio in, `onReceived` lip-sync tap, Opus both ways, full-duplex) | same | same | **100% — the hard part is shared** |
| Signaling | `POST /api/offer` (no auth), JSON `{sdp,type,pc_id,restart_pc}` | `POST /v1/realtime/calls`, `Bearer <ephemeral>` (mint via `/v1/realtime/client_secrets` first), `Content-Type: application/sdp` | small per-adapter (~30–50 LOC) |
| Data-channel control protocol | RTVI `{id,label:"rtvi-ai",type,data}`, `client-ready`/`bot-ready`, 1 Hz ping | `oai-events`: `session.update`, `response.*`, `input_audio_buffer.*`, `conversation.item.input_audio_transcription.*` | **genuinely different** — one parser each (~150–300 LOC), both map to the SAME internal events |
| Session config (persona/voice) | lives on the **server** (bot.py / per-port) — client is dumb | client **owns** it — send `session.update` after connect | `ConfigureSession()` = no-op for Pipecat, sends `session.update` for OpenAI |

**Design:**
```
  Avatar audio · OVR Lip Sync · scene · (QoE later)   ← backend-agnostic
                      │  common C# events:
                      │  OnConnected / OnBotStartedSpeaking / OnBotStoppedSpeaking /
                      │  OnUserTranscript / OnBotTranscript
        ┌─────────────┴──────────────────────────────┐
        │ IAgentTransport + shared WebRTC/media core  │  ← identical, the make-or-break part
        └──────────┬────────────────────────┬─────────┘
        PipecatTransport            OpenAIRealtimeTransport
        (signaling+RTVI+config)      (signaling+oai-events+session.update)
```
The shared base owns `RTCPeerConnection` + the in/out audio tracks. Each adapter differs only in
signaling (URL/auth/body), data-channel parse/emit, and session config. Everything above
`IAgentTransport` — avatar, lip sync, later the logging — is written once.

**Caveats:** it's "swappable backends," not a one-flag switch — each adapter is a few hundred real
lines, but isolated. Proving the Pipecat PoC (Unity↔aiortc media flow) largely de-risks OpenAI too,
since OpenAI's WebRTC is more battle-tested than aiortc interop (the harder direction is the one we
prove first). So: PoC defines `IAgentTransport` + `PipecatTransport`; OpenAI becomes a later adapter.

---

## 5. Work order

> **STATUS (updated — M0–M5 all DONE & verified on the study PC):**
> - **PoC** (separate repo): proved Unity↔Pipecat WebRTC, agent voice, full-duplex interruption, OVR lip sync, voice param.
> - **iva-cui teardown** (`377b3ec`→`7e64238`): old HTTP pipeline removed, QoE harness decoupled via `TurnData`.
> - **M2 — DONE** (`606e4f2`,`7244703`,`d04211f` + fixes): `PipecatClient` ported in; task-driven lifecycle (connect-on-briefing, greeting held until Start, mic gated to `conversationGateOpen`, disconnect on run-end); Start gated until connected; lip sync via auto split-tap.
> - **M4 — DONE** (`03f4fc2`,`7085854`,`f56f52b` + macos `0f92c96`): all 10 agents (t0–t9) with own persona + Kokoro voice via `agent_id` in the offer; backend `agents_config.py` ports the legacy prompts; 10 avatar audio sources wired. **Verified: each agent its own voice + lip sync.**
> - **M5 — DONE** (folded into M2/M3 hooks): conversation wired into briefing/Start/run-end/timer flow.
> - **NOW:** Milestone 6 — QoE telemetry re-sourcing from RTVI events (+ Done-button auto-reveal + avatar animation states, which ride the same events).

### Milestone 0 — ✅ DONE — Backend reachable (zero code, do first)
Start the Mac bot `uv run bot.py --host 0.0.0.0`. From the **Windows study PC**, open the demo's
stock React client in a browser pointed at `http://<mac-ip>:7860`.
- **GO:** agent greets ("Hello, I'm Pipecat!"), full-duplex voice works, **barge-in works**, netem on
  the hop visibly degrades it.
- **NO-GO:** firewall / `--host` / subnet issue — fix before any Unity work.
This proves backend + LAN + netem + interruption with nothing written.

### Milestone 1 — ✅ DONE — Minimal Unity PoC (a new EMPTY project, NOT iva-cui)
Goal: **prove the audio pipeline end-to-end in isolation.** A throwaway empty Unity project with
`com.unity.webrtc 3.0.0-pre.8`, just enough to:
- connect to the Mac bot (signaling + ICE-complete wait + `/api/offer`),
- send mic audio,
- send `client-ready` + `"ping"`, receive `bot-ready`,
- **hear the agent's voice** on a plain `AudioSource`,
- **barge-in works** (talk over the agent → it stops),
- log inbound RTVI message `type`s to confirm v0.0.81 field names.

**Structure it behind `IAgentTransport` from the start** (see §4b): a shared WebRTC/media core +
`PipecatTransport`. Don't inline Pipecat specifics into the peer/audio code — keep signaling, RTVI
parsing, and session-config inside the adapter, so a future `OpenAIRealtimeTransport` slots in
without touching the media core. (Don't build the OpenAI adapter now — just don't preclude it.)

**Success = a working voice conversation with interruption, in a bare Unity project.** This is the
single observable that confirms ICE + DTLS + Opus both-ways + data channel + RTVI handshake all work
together against the real backend. Empty project keeps it fast and removes iva-cui as a variable.

### Milestone 2 — ✅ DONE — In iva-cui + avatar audio + study lifecycle
`PipecatClient` connects per encounter at briefing (audio routed to that task's avatar AudioSource),
greeting held until Start, mic gated to `conversationGateOpen`, disconnect on run-end. Lip sync via
auto split-tap (finds the agent's `OVRLipSyncContext`, `skipAudioSource=true`, fed from `onReceived`).

### Milestone 3 — ✅ DONE (in PoC) — OVR Lip Sync
Split-tap: `AudioStreamTrack.onReceived += (pcm,ch,sr) => ctx.ProcessAudioSamplesRaw(pcm.Clone(),ch)`
into `OVRLipSyncContext(skipAudioSource=true)`. Proven in the PoC (visible mouth tracks the agent
voice). In iva-cui it attaches to the existing per-zone avatar OVR context; folded into Milestone 2.

### Milestone 4 — ✅ DONE — Multi-agent roster
Per-encounter connect; each agent (t0–t9) sends `agent_id` in the offer → bot serves that persona +
default Kokoro voice (`agents_config.py` ports the legacy `transition_prompts_*` prompts; one bot
process, single backend edit). `kTaskAgentIds` maps task index→agent_id; 10 avatar audio sources wired.

### Milestone 5 — ✅ DONE — Study-flow integration
Folded into M2's hooks: briefing→connect, Start→greeting+gate-open, timer/Done/operator→disconnect.

### Milestone 6 — ✅ DONE (telemetry + UX) — RTVI events
Implemented deliberately SIMPLE (per directive: qoe-lab stores the telemetry envelope whole; defining
a "turn" client-side under open-mic/interruption is fragile → don't):
- **M6a** (`4ef8d78`) — **greet during briefing**: `OpenConversation()` moved from Start to
  `EnterBriefing`, so the agent greets + the cold-start (cold TTS ~5s) happen during the brief read,
  off the timed clock. Start just opens the gate+timer → fast first real reply. Greeting is preamble,
  not a measured turn (mic muted in briefing).
- **M6b** (`63743b3`) — **raw RTVI event capture**: `PipecatClient.OnDcMessage` appends every inbound
  data-channel message verbatim (`{t, msg}`) to `Envelope.rtvi_events` (new `RawEvent`; `schema_version`
  →2). The complete server-authoritative record (speaking events, transcripts, per-stage `metrics`).
  **No client-side parsing** — turn definition + latency are offline analysis from this stream.
- **M6c** (`a490b78`) — **Done-button auto-reveal**: `<END>` substring in inbound bot text →
  `NotifyConversationOver()`. The typed per-turn `RecordTurn`/`samples[]` rebuild was deliberately NOT
  done — the raw stream supersedes it; `samples[]` is left empty.
- **M6d** (`ba62abd`) — removed the vestigial always-idle mic indicator from the HUD.

**Backend caveat (noted, not done):** the bot does not strip `<END>` before TTS, so Kokoro may *speak*
it. If audible in testing, add a one-line strip in `bot.py` before the TTS branch.

### Milestone 7 — remaining polish (optional / as-needed)
- **Avatar animation states** (listening/thinking/talking) off the RTVI speaking events — cosmetic;
  not yet wired (the old mic pipeline drove these).
- **netem verification** — confirm impairment on the Unity↔Mac hop shows up in the captured timings.
- **Voice polish** — audition the two UK stand-in voices (Museum t8/t9), swap in `agents_config.py`.
- **OpenAI Realtime adapter** — optional second backend; now "write one adapter", not core.

### Who does what
- **Code (`.cs`/backend `.py` config)** — assistant.
- **Editor / Play-test (study PC only — no Play mode on dev machine)** — user: resolve the package,
  scene/AudioSource/OVR wiring, run every Play-test and the PoC.

---

## 6. Open risks (settle by running, cheapest first)
1. **Backend reachable over LAN** (Milestone 0) — zero code; foundation of everything.
2. **Unity↔aiortc media round-trip** (Milestone 1 PoC) — the real make-or-break; ICE-complete wait is
   the likely failure point (apply poll/timeout).
3. **RTVI v0.0.81 field names** — log inbound types on first turn before relying on them.
4. **`onReceived` → OVR visemes in Editor Play mode on Windows** (Milestone 3) — API-verified;
   runtime firing is the residual unknown.

---

*Reference doc. Architecture decided: Pipecat-local on a Mac, Unity custom frontend, full-duplex with
interruption, matching the macOS demo's behavior. QoE logging deferred.*
