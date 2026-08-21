# Opus Pipeline Asymmetry (uplink vs downlink)

Read-only analysis of the WebRTC voice path between the Unity client and the
Pipecat server (Mac): do both directions use the same Opus encoding, and how does
each handle packet loss? No code was changed.

**Verdict.** Same codec both ways (Opus, 48 kHz, 20 ms, voip), but different
implementations at different fixed bitrates — and, critically, **asymmetric loss
handling on the decode side**. For a study whose netem shaper sits on this hop,
uplink and downlink are *not* equivalent stimuli.

**The conclusion that matters:** under identical shaping, downlink loss is
concealed (the participant hears a smoothed gap) while uplink loss is a hard
discontinuity into VAD/Whisper. The *recognition* path degrades faster than the
*listening* path.

## Versions

| End | Component | Version |
|---|---|---|
| Client — uplink encoder / downlink decoder | `com.unity.webrtc` (wraps libwebrtc) | `3.0.0-pre.8`, **libwebrtc M116** = `branch-heads/5845` |
| Server — downlink encoder / uplink decoder | `aiortc` (libopus via PyAV) | `1.13.0` (`server/uv.lock:101-102`) |

Both sides run codec **defaults**. Nothing in this repo overrides them — no
`SetParameters`, no `maxaveragebitrate`. On the client the defaults are compiled
into the native binary, not set in C#.

## Comparison

| | **Uplink** (Unity mic → server) | **Downlink** (server TTS → Unity) |
|---|---|---|
| Implementation | libwebrtc M116 | aiortc 1.13.0 |
| Codec / clock rate | Opus @ 48 kHz | Opus @ 48 kHz |
| Bitrate | **32 kbps** | **96 kbps** |
| Channels | **mono** | **stereo** |
| Frame size | 20 ms | 20 ms |
| Application mode | voip | voip |
| Adapts under load | **No** | **No** |
| In-band FEC on wire | **No** | **No** |
| DTX | No | No |
| **Loss handling (decoder)** | **Hard gap** — aiortc has no PLC | **Concealed** — NetEq Expand/Merge |
| Source feeding codec | mic native 48 kHz | Kokoro 24 kHz mono, upsampled + stereo-ized |

Uplink defaults come from `AudioEncoderOpusConfig`'s constructor: `bitrate_bps`
32000, `num_channels` 1, `application` kVoip, `fec_enabled` false, `dtx_enabled`
false, `frame_size_ms` 20. The 32 kbps is `kOpusBitrateFbBps` (fullband) × 1
channel; it is clamped to `kMinBitrateBps` 6000 – `kMaxBitrateBps` 510000.

## Packet loss

### Neither direction adapts its bitrate

- **Downlink:** `bit_rate = 96000` set once. aiortc's sender reacts to RTCP REMB
  *only if the encoder exposes `target_bitrate`* — its Opus encoder has none
  (only VP8 does). No TWCC. ⇒ fixed 96 kbps.
- **Uplink:** libwebrtc's audio encoder *can* adapt via
  `OnReceivedUplinkBandwidth`, but that needs REMB or transport-cc from the
  remote. aiortc sends neither for audio (both generators are video-only; it
  emits plain RTCP receiver reports). ⇒ effectively fixed 32 kbps.
  - The server's RTCP RR does feed `OnReceivedUplinkPacketLossFraction →
    SetProjectedPacketLossRate`, which tunes Opus's internal loss robustness —
    **not** the bitrate.

### No FEC in either direction

`useinbandfec=1` is (RFC 7587) a **receiver capability** — "my decoder can use
FEC." Unity putting it in its offer therefore asks the **server** for FEC on the
**downlink**; it does not make Unity's encoder emit FEC. aiortc ignores it: its
encoder sets no FEC option, and it registers Opus with no fmtp at all, so its
answer can't request uplink FEC either. Net: **no FEC on the wire either way**,
and `MungeOpusFec` is redundant with what libwebrtc already advertises by
default.

Consequence: NetEq's Opus-FEC recovery path has nothing to consume, so downlink
concealment is **Expand/Merge (PLC) only**.

### Loss resilience is asymmetric — the real difference

- **Downlink → concealed.** libwebrtc conceals missing audio via NetEq. Verified
  in M116 source: `decision_logic.cc` `NoPacket()` returns
  `NetEq::Operation::kExpand`, and `IsExpand` covers `kExpand` / `kCodecPlc`.
  Unconditional — no enable flag, never silence-for-loss.
- **Uplink → hard gap.** aiortc's decoder is five lines with no missing-frame
  path:

  ```python
  # aiortc 1.13.0 — src/aiortc/codecs/opus.py:24-28
  def decode(self, encoded_frame: JitterFrame) -> list[Frame]:
      packet = Packet(encoded_frame.data)
      packet.pts = encoded_frame.timestamp
      packet.time_base = TIME_BASE
      return cast(list[Frame], self.codec.decode(packet))
  ```

  No PLC, no `decode_fec`, no gap detection anywhere in the receive chain — a
  lost packet simply never becomes a `decode()` call. (aiortc does have NACK/RTX
  and loss stats, but recovery ≠ concealment.)

## Why it matters for the study

1. **Loss hurts the uplink more.** Under the same netem loss the downlink is
   smoothed by NetEq while the uplink reaches the STT as raw discontinuities.
   Don't attribute rated quality to a direction without accounting for this —
   it's the confound, not the bitrate.
2. **Bitrate asymmetry is mostly cosmetic.** 3× and mono-vs-stereo, but constant
   across conditions, and the downlink's extra bits largely duplicate mono
   24 kHz TTS into stereo.
3. **No adaptation either way.** Shaping reaches the codec as loss/latency, not
   as bitrate starvation — arguably what you want, but state it, don't assume it.

## Source pointers

**Client — `iva-cui-unity/Assets/Scripts/PipecatClient.cs`**
- `:239` — `Microphone.Start(micDevice, true, 1, 48000)` → mic mono @ 48 kHz.
- `:58`, `:307`, `:353-370` — `requestOpusFec` and `MungeOpusFec()` appending
  `useinbandfec=1` to the opus fmtp line.
- No `GetParameters`/`SetParameters` (0 matches) → compiled defaults run.

**Client engine — `com.unity.webrtc@3.0.0-pre.8`**
- `Runtime/Plugins/x86_64/webrtc.dll` — the binary loaded on the Windows study
  PC. Uplink defaults and NetEq PLC are compiled into this, not into any C#.
- `Runtime/Scripts/RTCStats.cs:993,1003` — `concealedSamples` /
  `concealmentEvents` on inbound RTP stats. Populated only from NetEq — both
  corroboration that concealment is in the path and the hook to confirm it live.

**libwebrtc M116** — via
`https://chromium.googlesource.com/external/webrtc/+/branch-heads/5845/<path>`
- `api/audio_codecs/opus/audio_encoder_opus_config.cc` — the encoder defaults
  listed above.
- `api/audio_codecs/opus/audio_encoder_opus_config.h` — `kMinBitrateBps` 6000,
  `kMaxBitrateBps` 510000, `kDefaultFrameSizeMs` 20.
- `modules/audio_coding/codecs/opus/audio_encoder_opus.cc` —
  `kOpusBitrateFbBps = 32000`; `SdpToConfig` maps
  `useinbandfec`/`stereo`/`usedtx`/`maxaveragebitrate`; mono ⇒ kVoip.
  `AppendSupportedEncoders` already advertises `useinbandfec=1` + stereo.
- `modules/audio_coding/neteq/decision_logic.cc` — `kExpand` on missing packet.

**Server — `macos-local-voice-agents/server/`**
- `bot.py:79-82` — `TransportParams(audio_in_enabled=True, audio_out_enabled=True)`
  and nothing else → aiortc Opus defaults apply.
- `bot.py:87` / `tts_mlx_isolated.py:388` — TTS at 24 kHz, mono, before aiortc
  upsamples and stereo-izes.

**aiortc 1.13.0** — `https://github.com/aiortc/aiortc/blob/1.13.0/src/aiortc/<path>`
- `codecs/opus.py` — decoder `:17-28` (no PLC); encoder `bit_rate = 96000` `:34`,
  `layout = "stereo"` `:36`, `application: voip` `:37`. No `target_bitrate`.
- `codecs/__init__.py` — Opus registered clockRate 48000, channels 2, PT 96,
  **no default fmtp**.
- `rtcrtpsender.py` / `rtcrtpreceiver.py` — REMB only helps encoders with
  `target_bitrate`; REMB + NACK generators are video-only; audio sends plain RR.

## Confidence and remaining unknowns

Verified against source, with provenance confirmed: `3.0.0-pre.8` ↔ M116 ↔
`branch-heads/5845`; M116 conceals unconditionally; Unity's receive path
genuinely runs NetEq (not bypassed); aiortc 1.13.0 has zero receive-side PLC.

Still unconfirmed without a live session:

- **Actual on-wire uplink bitrate.** 32 kbps is the compiled target, not an
  observed measurement. Confirm via `RTCRtpSender.GetStats()` →
  `RTCOutboundRTPStreamStats.bytesSent` delta (pattern in the package's
  `BandwidthSample.cs`).
- **Negotiated fmtp on both `m=audio` lines.** Capture SDP from a real
  `POST /api/offer` to confirm ptime and that `stereo` / `maxaveragebitrate` /
  `useinbandfec` do not appear in the answer.
- **PLC actually firing.** Watch `concealedSamples` / `concealmentEvents` climb
  under netem loss.

## If you ever change these settings

Not currently changed — recorded for reference.

- **Uplink bitrate** is settable without a native rebuild: `SetParameters`
  (`encodings[0].maxBitrate`, as the package's `BandwidthSample.cs` does) or an
  SDP `maxaveragebitrate` munge reusing the existing `MungeOpusFec` pattern —
  libwebrtc reads it, clamped 6000–510000.
- **Downlink bitrate** has no API or negotiation path — `bit_rate = 96000` is
  hard-coded, so it needs a monkey-patch/subclass of aiortc's `OpusEncoder`.
- **PLC is not a toggle either way.** Disabling it in libwebrtc means patching
  and rebuilding the native lib; adding it to aiortc means writing gap detection
  and concealment yourself in Python — and a hand-rolled version will not match
  NetEq, a long-tuned adaptive jitter-buffer + concealment + time-stretch system.
- **The main risk is methodological.** Codec settings are the fixed backdrop of
  the stimulus; changing them shifts the participants' quality anchor and breaks
  comparability with data already collected. Decide once, lock it, document it —
  and don't bundle FEC/DTX/channel changes into the same move.
