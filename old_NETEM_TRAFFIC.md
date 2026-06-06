# Netem Traffic Analysis — IVA-CUI

What crosses the netem (network-shaping) machine when Unity is separated from the
two Python services on the same LAN.

## Topology

```
┌─────────────┐         netem          ┌────────────────────────────────────┐
│   Unity     │   (you control this    │   "Backend" host (Windows + WSL2)    │
│  (client)   │◄═══ link's delay, ════►│  • Middleware  FastAPI  :8000        │
│             │      loss, rate)        │  • ASR (Whisper) FastAPI :8083 (WSL) │
└─────────────┘                         │  • LLM server            :8082       │
                                        │  • edge-tts → Microsoft cloud        │
                                        └────────────────────────────────────┘
```

The `setup_port_proxy.ps1` script forwards exactly **two** ports to the LAN:
`8000` (middleware) and `8083` (ASR). Those are the only services Unity reaches
directly, and therefore the only traffic that crosses netem.

## What traverses the netem link

All of it is **HTTP/TCP**, Unity ⇄ backend host, in this per-utterance cycle:

| # | Direction | Endpoint / Port | Payload | Size profile |
|---|-----------|-----------------|---------|--------------|
| 1 | Unity → ASR | `POST :8083/transcribe_audio/` | recorded mic audio (multipart file upload, WAV/PCM) | **large uplink** — scales with utterance length |
| 2 | ASR → Unity | response to (1) | `{"transcription": "..."}` JSON | tiny |
| 3 | Unity → Middleware | `GET :8000/speak/{role}/?q=<text>` | transcribed text in query string | small |
| 4 | Middleware → Unity | response to (3) | JSON: `message`, `audio` (filename), `transition`, timing fields | small–medium |
| 5 | Unity → Middleware | `GET :8000/static/<timestamp>.mp3` | — | request tiny |
| 6 | Middleware → Unity | the TTS mp3 file | edge-tts generated audio | **large downlink** — scales with response length |

Plus occasional control calls, also over `:8000`:

- `GET /refresh/{scene_name}/` — resets conversation at scene start (tiny)
- `GET /check_transition/{role}/` — state check (tiny request, tiny JSON reply;
  if Unity polls this on a timer, it's steady low-rate chatter)

## What does NOT cross netem

These all happen on the backend side, behind the netem boundary, so shaping
won't touch them:

- **Middleware ⇄ LLM** (`192.168.50.147:8082/v1` or Ollama `:11434`) — LLM
  inference traffic. Internal to the backend host.
- **Middleware → edge-tts** — `TTS.py` uses Microsoft's `edge_tts`, which calls
  out to Microsoft's cloud over the internet, not the LAN. The mp3 is generated
  server-side, then only the finished file (step 6) crosses netem.
- Note: the middleware does **not** call the ASR server — Unity hits ASR
  directly (which is why both 8000 and 8083 are port-forwarded).

## Implications for the QoE experiment

- The two payloads that dominate bytes and are most delay/loss-sensitive are the
  **audio upload (step 1)** and the **TTS mp3 download (step 6)**. These are what
  netem loss/throttling will hit hardest.
- Steps 3–5 are latency-sensitive but byte-light, so **added delay** (not
  bandwidth) is what perturbs them — each round trip adds directly to the agent's
  response latency, which is precisely the "response delay" the paper studies.
- Because TTS audio is fetched as a separate `GET /static/...` *after* the
  `/speak` reply, a single user turn incurs **at least 3 sequential round trips
  across netem** (ASR upload → speak → static fetch). Per-direction delay
  therefore multiplies across all three legs.

## Open items to confirm (Unity C# not in working tree)

- Whether `check_transition` is polled, and at what interval.
- Exact audio recording format/size for the upload in step 1.

> The Unity C# scripts were not present in the working tree at analysis time;
> the wire protocol above is derived from the backend code
> (`app.py`, `whisper_server.py`, `TTS.py`, `llm_backends.py`) and
> `setup_port_proxy.ps1`. Restore/point to the Unity scripts to verify the two
> open items.
