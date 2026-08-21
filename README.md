# IVA-CUI — Unity XR client for a voice-agent QoE study

The Unity XR frontend used in the master's thesis *The Impact of Network Degradation on
Quality of Experience in Real-Time Conversation with LLM-powered Virtual Agents* (KTH).
Participants hold timed, free-form voice conversations with embodied agents in VR while a
network emulator degrades the voice path; this app is what the participant sees and hears.

Forked from the CUI '25 **IVA-CUI** project by Maslych et al.
([paper](https://doi.org/10.1145/3719160.3736636)) — please cite their paper if you use
the Unity environments. The original per-turn HTTP voice pipeline (ASR → LLM → TTS request
cycle) has been replaced with a realtime WebRTC voice connection to a
[Pipecat server](https://github.com/Lambozhuang/macos-local-voice-agents), and the walking-quest
scenes have been reshaped into standalone one-off conversations: the participant is
teleported in front of one agent per trial, talks, and rates the experience.

## Architecture

```
  Windows study PC                    QoE Lab machine                  Mac
  ┌───────────────────────┐         ┌────────────────────┐   ┌──────────────────────┐
  │ Unity client          │ WebRTC  │ Linux bridge +     │   │ Pipecat voice agent  │
  │  Quest 3 via Link     │◄───────►│ netem (voice path  │◄─►│  bot.py :7860        │
  │  avatars + lip-sync   │ Opus +  │ only) + operator   │   │  VAD · turn · STT ·  │
  │                       │ data ch │ console :8080      │   │  LLM · TTS           │
  └──────────┬────────────┘         └─────────▲──────────┘   └──────────────────────┘
             │        WebSocket + HTTP        │
             └─ study control / ratings / logs┘
```

Two separate connections leave the client:

- **Voice** (Unity ↔ Mac, WebRTC): Opus audio both ways plus an RTVI data channel
  (`"chat"`) carrying live transcripts, bot speaking on/off events and per-stage timing
  metrics. This is the only traffic that crosses the network emulator.
- **Study control** (Unity ↔ operator console, WebSocket + HTTP): the qoe-lab console
  starts each trial and collects questionnaire ratings and conversation logs. This
  traffic terminates at the QoE Lab machine and is never impaired.

The mic stays open for the whole conversation (full duplex); barge-in is decided
server-side by voice activity detection. The WebRTC connection is established during the
task briefing so model warm-up stays off the clock, and the agent stays silent until the
participant presses **Start**. The client records the raw RTVI event stream verbatim for
every run; turn and latency analysis happens offline.

## Key files

| Piece | File |
|---|---|
| WebRTC voice client (mic, audio, RTVI, lip-sync, reconnect) | `iva-cui-unity/Assets/Scripts/PipecatClient.cs` |
| Study controller (console WebSocket, task lifecycle, HUD) | `iva-cui-unity/Assets/Scripts/QoE/QoeDeviceClient.cs` |
| Questionnaire client | `iva-cui-unity/Assets/Scripts/QoE/QoeRatingClient.cs` |
| Telemetry collector (writes/uploads conversation logs) | `iva-cui-unity/Assets/Scripts/QoE/QoeTurnLog.cs` |

## Running

- Unity **2022.3.62f3**, `com.unity.webrtc` 3.0.0-pre.8. Scene: `Assets/Scenes/QoE_Shell.unity`
  (all four environments merged; task switching teleports the XR rig, no scene loading).
- Set `PipecatClient.offerUrl` to the server (`http://<mac-lan-ip>:7860/api/offer`) and
  the `QoeDeviceClient` host/port to the operator console (`:8080`).
- Headset: Meta Quest 3 tethered via Link, so no wireless hop is in the audio path.
- Agents `t0`…`t9` map to task indices (t0 training, t1–3 city, t4–6 hotel, t7–9 museum);
  personas and voices live on the server.

## Legacy

`iva-cui-backend/` and parts of the upstream Unity machinery (quest progression, filler
audio, the HTTP voice pipeline) predate the WebRTC rewrite and are not on the current
code path. Third-party asset licenses: `iva-cui-unity/ASSET_LICENSES.md`.
