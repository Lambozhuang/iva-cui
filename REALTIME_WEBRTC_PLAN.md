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

> **STATUS (updated):**
> - **PoC project** (`~/sources/Realtime Voice Agent Test`, separate repo) — **M0–M3 + voice DONE & proven on the study PC:** Unity↔Pipecat WebRTC, agent voice in, full-duplex interruption, OVR Lip Sync driven by the agent audio (visible avatar mouth), per-connection voice dropdown + `bot.py` voice param + `prewarm.py` (all 20 voices cached, offline-ready). Every technical unknown resolved.
> - **iva-cui teardown — DONE** (commits `377b3ec`→`7e64238` on `qoe/realtime-webrtc`): old HTTP voice pipeline removed (ServerInterface, MicrophoneHandler, AudioMemoryStreamHandler, mic editor); QoE harness decoupled via `TurnData` DTO; survivors marked with `// SEAM (Pipecat):`; QoE_Shell orphaned components removed (0 missing scripts). Compiles green.
> - **NOW:** Milestone 2 — port the proven PoC client into iva-cui.

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

### Milestone 2 — ◀ NOW — Bring it into iva-cui + avatar audio
Port the proven PoC client (`PipecatPoC.cs`) into the iva-cui project as the `IAgentTransport`-shaped
seam. Plug into `AgentSelectionController.PlayAudioForAgent(... TurnData ...)` / route inbound audio to
the active `ActivationZone.audioSource` (spatialized), and respect `conversationGateOpen` +
`QoeTurnLog.CurrentEpoch`. Lip sync (the OVR split-tap, proven in the PoC) attaches at the same
AudioSource. **Single-agent first** (one bot/port), prove a talking lip-synced avatar inside iva-cui,
then Milestone 4 generalizes to the roster.

### Milestone 3 — ✅ DONE (in PoC) — OVR Lip Sync
Split-tap: `AudioStreamTrack.onReceived += (pcm,ch,sr) => ctx.ProcessAudioSamplesRaw(pcm.Clone(),ch)`
into `OVRLipSyncContext(skipAudioSource=true)`. Proven in the PoC (visible mouth tracks the agent
voice). In iva-cui it attaches to the existing per-zone avatar OVR context; folded into Milestone 2.

### Milestone 4 — Multi-agent roster
Per-agent voice + persona via per-encounter reconnect. Simplest: one bot process per agent on its own
port (7860/7861/7862), each with its own system prompt + voice; Unity points each zone at its port.
Fresh connect (`pc_id:null`) per agent. No bot.py change required.

### Milestone 5 — qoe-lab control states + study harness re-integration
Wire the conversation into the existing study flow (briefing/Start gate, run states, Done, etc.).

### Milestone 6 — QoE logging (DECIDE LATER)
Decide how to log QoE for the streaming/full-duplex model. Not designed yet. Old harness untouched
until then.

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
