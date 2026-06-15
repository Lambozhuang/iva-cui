using System;
using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;

// Unity <-> Pipecat (macos-local-voice-agents) voice agent over WebRTC, wired
// into the QoE study's task lifecycle. Demo-quality, single file.
//
// LIFECYCLE (driven by QoeDeviceClient):
//   teleport-to-task / briefing  -> Connect(agentSink)   peer+DC+ICE up, mic muted,
//                                                          greeting HELD back
//   Start pressed (gate opens)    -> OpenConversation()   release greeting, mic hot
//   Done / timer / teleport-away  -> Disconnect()         tear down, fresh next time
//
// Per-encounter connect = each agent gets its own bot session/voice (Milestone 4
// just makes offerUrl/voice per-agent). The agent voice plays through the agent
// avatar's existing lip-sync AudioSource (passed to Connect), so the mouth moves.
// No proximity / ActivationZone dependency — the active agent is chosen by task.
//
// QoE re-sourcing from RTVI events is deferred (Milestone 6).
public class PipecatClient : MonoBehaviour
{
    [Header("Mac backend (http://<mac-lan-ip>:7860/api/offer)")]
    public string offerUrl = "http://127.0.0.1:7860/api/offer";

    [Tooltip("RTVI protocol version sent in client-ready. 1.0.0 matches the pinned server.")]
    public string rtviVersion = "1.0.0";

    [Header("Microphone")]
    [Tooltip("Capture device. Don't pick a virtual/loopback device — it echoes.")]
    public string micDeviceName = "";

    [Range(0f, 8f)]
    [Tooltip("Linear gain applied to captured mic samples before they're sent to the agent. " +
             "1 = unchanged. Raise if the agent isn't picking up a soft-spoken participant; " +
             "keep it modest (samples are clamped to avoid hard clipping). " +
             "Adjustable live in the inspector while the scene is running.")]
    public float micGain = 1f;

    [Header("Harsh-network robustness")]
    [Tooltip("Auto re-establish the media path (ICE restart) if the peer connection drops " +
             "to Disconnected/Failed under high loss/RTT, instead of leaving the session dead. " +
             "Reuses the existing bot session + conversation context via restart_pc.")]
    public bool autoReconnect = true;
    [Tooltip("Seconds the connection must stay Disconnected before we trigger an ICE restart. " +
             "Brief blips often self-heal; this avoids thrashing on transient drops.")]
    public float reconnectAfterSeconds = 2.0f;
    [Tooltip("Minimum seconds between ICE-restart attempts, so a persistently bad path " +
             "doesn't get hammered with renegotiations.")]
    public float reconnectCooldownSeconds = 5.0f;
    [Tooltip("Munge the Opus fmtp line in the SDP offer to request in-band FEC (useinbandfec=1). " +
             "Lets the encoder add redundant frame copies so the server can recover lost mic " +
             "audio before it reaches the VAD. EXPERIMENTAL: only takes effect if the libwebrtc " +
             "build honors it; verify on the Mac that VAD start-of-speech improves under loss.")]
    public bool requestOpusFec = true;

    // Live mic input level (0..1), smoothed, sampled from the capture clip each
    // frame while the mic track is hot. Exposed for the HUD level meter so the
    // participant can see the Unity frontend is hearing them. 0 when muted.
    public float MicLevel { get; private set; }

    public enum KokoroVoice
    {
        af_heart, af_bella, af_nicole, af_aoede, af_kore, af_sarah,
        am_michael, am_fenrir, am_puck, am_echo, am_eric, am_liam,
        bf_emma, bf_isabella, bf_alice, bf_lily, bm_george, bm_fable, bm_lewis, bm_daniel
    }

    [Header("Agent TTS voice (testing override)")]
    [Tooltip("Per-agent voice normally comes from the bot (selected by agent_id). " +
             "Tick to force the voice below instead — for testing only.")]
    public bool overrideVoice = false;
    public KokoroVoice voice = KokoroVoice.af_heart;

    private string agentId = "";  // t0..t9, set per encounter by QoeDeviceClient

    // The agent avatar's OVR Lip Sync context, auto-found on the sink's GameObject
    // at Connect. We drive it via the split-tap (skipAudioSource=true + fed PCM from
    // onReceived) because letting OVR read a SetTrack'd AudioSource directly produces
    // a constantly-flapping mouth (WebRTC's receiver filter ordering). Proven in PoC.
    private OVRLipSyncContext agentLipSync;
    private bool agentLipSyncOriginalSkip;

    // True once the peer connection + data channel are up (greeting still held).
    public bool IsConnected { get; private set; }

    // True once the agent has audibly begun speaking — driven by the RTVI
    // 'bot-started-speaking' event, NOT raw onReceived PCM (which flows during
    // connection/buffering before any audible speech). Since the greeting plays
    // during briefing, this means "the agent has started its greeting" —
    // QoeDeviceClient gates the Start button on this so Start lights up only once
    // the agent is genuinely talking.
    public bool HasAgentSpoken { get; private set; }

    private RTCPeerConnection pc;
    private RTCDataChannel dc;
    private MediaStream sendStream;
    private AudioStreamTrack micTrack;
    private AudioStreamTrack remoteTrack;
    private AudioSource micSource;
    private MicGainFilter micGainFilter; // scales captured samples (micGain) ahead of WebRTC's filter
    private AudioSource agentSink;     // the current agent avatar's lip-sync AudioSource
    private ActivationZone agentZone;  // its ActivationZone, for talk/listen animation
    private AudioClip micClip;
    private string micDevice;
    private float keepAliveTimer;

    private bool dcReady;              // data channel open
    private bool greetReleased;        // OpenConversation() called -> ok to send client-ready
    private bool clientReadySent;
    private bool tearingDown;

    private string pcId = "";          // server-assigned id; reused to renegotiate (ICE restart)
    private RTCPeerConnectionState connState = RTCPeerConnectionState.New;
    private float disconnectedFor;     // seconds the connection has been Disconnected/Failed
    private float reconnectCooldown;   // seconds remaining before another ICE restart is allowed
    private bool reconnecting;         // an ICE-restart renegotiation is in flight

    [Serializable] private class OfferBody { public string sdp; public string type; public string pc_id; public bool restart_pc; public string voice; public string agent_id; }
    [Serializable] private class AnswerBody { public string pc_id; public string sdp; public string type; }

    private void Start()
    {
        StartCoroutine(WebRTC.Update());
    }

    // === Study lifecycle entry points (called by QoeDeviceClient) ===

    // Begin connecting to the agent. `sink` is the agent avatar's lip-sync
    // AudioSource the reply should play through (its OVR context drives the mouth).
    // `agentId` (t0..t9) tells the bot which persona + default voice to use.
    // The greeting is held until OpenConversation().
    public void Connect(AudioSource sink, string agentId, ActivationZone zone)
    {
        this.agentId = agentId;
        if (pc != null) { Debug.LogWarning("[Pipecat] Connect() called while already connected — ignoring."); return; }
        agentSink = sink;

        // The agent's ActivationZone (drives the look-at-player / attentive pose while
        // talking). Passed in explicitly by QoeDeviceClient — the zone is a SIBLING of
        // the lip-sync AudioSource, not an ancestor, so it can't be found by walking up.
        agentZone = zone;
        if (agentZone == null && sink != null)
            Debug.LogWarning($"[Pipecat] No ActivationZone wired for {sink.gameObject.name} — agent won't animate while talking.");

        // Find the agent's OVR Lip Sync context (on the sink GameObject) and switch
        // it to the split-tap: it stops reading its own AudioSource (which produces a
        // flapping mouth with a SetTrack'd source) and instead consumes the PCM we
        // feed from onReceived. Original flag restored on Disconnect.
        agentLipSync = sink != null ? sink.GetComponent<OVRLipSyncContext>() : null;
        if (agentLipSync != null)
        {
            agentLipSyncOriginalSkip = agentLipSync.skipAudioSource;
            agentLipSync.skipAudioSource = true;
        }
        else if (sink != null)
        {
            Debug.LogWarning($"[Pipecat] No OVRLipSyncContext on {sink.gameObject.name} — mouth won't move.");
        }

        StartCoroutine(ConnectRoutine());
    }

    // Release the agent's greeting and open the mic. Called when the subject
    // presses Start (gate opens). Safe to call before or after the DC opens.
    public void OpenConversation()
    {
        greetReleased = true;
        TrySendClientReady();
    }

    // Tear everything down so a fresh Connect() starts a clean session/context.
    public void Disconnect()
    {
        tearingDown = true;
        if (remoteTrack != null) { remoteTrack.onReceived -= OnRemoteAudio; }
        // Stop playback. (Don't SetTrack(null) — com.unity.webrtc dereferences it.)
        if (agentSink != null) agentSink.Stop();
        // Reset the agent's OVR context to silence so the mouth stops moving.
        if (agentLipSync != null) agentLipSync.skipAudioSource = agentLipSyncOriginalSkip;
        // Relax the talking pose.
        if (agentZone != null) { agentZone.SetBotSpeaking(false); agentZone = null; }
        diagFaceMesh = null; // diagnostic: re-resolve per agent
        if (dc != null) { dc.Close(); dc = null; }
        if (micTrack != null) { micTrack.Dispose(); micTrack = null; }
        if (sendStream != null) { sendStream.Dispose(); sendStream = null; }
        if (pc != null) { pc.Close(); pc = null; }
        if (!string.IsNullOrEmpty(micDevice)) { Microphone.End(micDevice); micDevice = null; }
        if (micGainFilter != null) { Destroy(micGainFilter); micGainFilter = null; }
        if (micSource != null) { Destroy(micSource); micSource = null; }
        remoteTrack = null; agentSink = null; agentLipSync = null;
        dcReady = greetReleased = clientReadySent = IsConnected = HasAgentSpoken = false;
        tearingDown = false;
        pcId = ""; connState = RTCPeerConnectionState.New;
        disconnectedFor = reconnectCooldown = 0f; reconnecting = false;
        MicLevel = 0f;
        Debug.Log("[Pipecat] disconnected");
    }

    // === Connection ===

    // Pick the device whose name contains micDeviceName (case-insensitive);
    // empty -> device[0]. Avoids blindly grabbing a virtual loopback device.
    private string ResolveMicDevice()
    {
        var devices = Microphone.devices;
        if (devices.Length == 0) return null;
        if (!string.IsNullOrEmpty(micDeviceName))
        {
            foreach (var d in devices)
                if (d.IndexOf(micDeviceName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return d;
            Debug.LogWarning($"[Pipecat] No mic matching '{micDeviceName}' — falling back to '{devices[0]}'. " +
                             "Right-click -> List Microphone Devices to see exact names.");
        }
        return devices[0];
    }

    private IEnumerator InitMic()
    {
        micDevice = ResolveMicDevice();
        if (string.IsNullOrEmpty(micDevice))
        {
            Debug.LogError("[Pipecat] No microphone device found.");
            yield break;
        }
        Debug.Log($"[Pipecat] Using mic: '{micDevice}'");
        micSource = gameObject.AddComponent<AudioSource>();
        // Gain filter, added BEFORE the AudioStreamTrack ctor (which appends
        // WebRTC's own capture filter) so it runs first in the DSP chain and
        // scales the samples WebRTC then grabs + transmits. com.unity.webrtc reads
        // raw filter-chain buffers, so AudioSource.volume wouldn't affect the sent
        // audio — this filter is the place to apply mic gain.
        micGainFilter = gameObject.AddComponent<MicGainFilter>();
        micGainFilter.owner = this;
        micClip = Microphone.Start(micDevice, true, 1, 48000);

        float t = 0f;
        while (Microphone.GetPosition(micDevice) <= 0 && t < 3f) { t += Time.deltaTime; yield return null; }

        micSource.clip = micClip;
        micSource.loop = true;
        micSource.Play();
        Debug.Log($"[Pipecat] Mic started: {micDevice}");
    }

    private IEnumerator ConnectRoutine()
    {
        yield return StartCoroutine(InitMic());

        var config = default(RTCConfiguration);
        config.iceServers = new RTCIceServer[] { }; // LAN: host candidates only, no STUN
        pc = new RTCPeerConnection(ref config);

        pc.OnIceConnectionChange = s => Debug.Log($"[Pipecat] ICE connection: {s}");
        pc.OnConnectionStateChange = s =>
        {
            Debug.Log($"[Pipecat] Peer connection: {s}");
            connState = s;
            // A fresh connect/restart that reaches Connected clears the recovery timers.
            if (s == RTCPeerConnectionState.Connected)
            {
                disconnectedFor = 0f;
                reconnecting = false;
            }
        };
        pc.OnTrack = e =>
        {
            if (e.Track is AudioStreamTrack at)
            {
                Debug.Log("[Pipecat] Remote audio track received -> routing to agent avatar");
                remoteTrack = at;
                RouteToAgentSink();
                at.onReceived += OnRemoteAudio; // only used if lipSyncContext fallback is set
            }
        };

        sendStream = new MediaStream();
        micTrack = new AudioStreamTrack(micSource);
        micTrack.Enabled = false; // muted until the gate opens (Start)
        pc.AddTrack(micTrack, sendStream);

        dc = pc.CreateDataChannel("chat", new RTCDataChannelInit { ordered = true });
        dc.OnOpen = OnDcOpen;
        dc.OnMessage = OnDcMessage;
        dc.OnClose = () => Debug.Log("[Pipecat] data channel closed");

        yield return StartCoroutine(NegotiateRoutine(iceRestart: false));
    }

    // Create an offer, (optionally) munge it for Opus FEC, post it to /api/offer,
    // and apply the answer. Shared by the initial connect and by ICE-restart
    // recovery. When iceRestart is true we keep the same RTCPeerConnection and ask
    // the server to renegotiate the existing session (restart_pc + the stored
    // pc_id), so the bot + conversation context survive a network drop.
    private IEnumerator NegotiateRoutine(bool iceRestart)
    {
        var offerOptions = new RTCOfferAnswerOptions { iceRestart = iceRestart };
        var offerOp = pc.CreateOffer(ref offerOptions);
        yield return offerOp;
        if (offerOp.IsError) { Debug.LogError("[Pipecat] CreateOffer: " + offerOp.Error.message); reconnecting = false; yield break; }

        var desc = offerOp.Desc;
        if (requestOpusFec) desc.sdp = MungeOpusFec(desc.sdp);
        var slOp = pc.SetLocalDescription(ref desc);
        yield return slOp;
        if (slOp.IsError) { Debug.LogError("[Pipecat] SetLocalDescription: " + slOp.Error.message); reconnecting = false; yield break; }

        // aiortc is non-trickle: gather all ICE candidates BEFORE posting the offer.
        float gt = 0f;
        while (pc.GatheringState != RTCIceGatheringState.Complete && gt < 5f) { gt += Time.deltaTime; yield return null; }
        Debug.Log($"[Pipecat] ICE gathering: {pc.GatheringState} ({gt:F1}s){(iceRestart ? " [ICE restart]" : "")}");

        // voice only when overriding for tests; empty = use the agent's default voice.
        // On reconnect, reuse pc_id + restart_pc so the server renegotiates in place.
        var body = new OfferBody { sdp = pc.LocalDescription.sdp, type = "offer", pc_id = iceRestart ? pcId : "",
                                   restart_pc = iceRestart, voice = overrideVoice ? voice.ToString() : "", agent_id = agentId };
        string json = JsonUtility.ToJson(body);

        using (var req = new UnityWebRequest(offerUrl, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[Pipecat] /api/offer failed: {req.error}\n{req.downloadHandler.text}");
                reconnecting = false;
                yield break;
            }

            var answer = JsonUtility.FromJson<AnswerBody>(req.downloadHandler.text);
            if (!string.IsNullOrEmpty(answer.pc_id)) pcId = answer.pc_id;
            Debug.Log($"[Pipecat] Got answer (pc_id={answer.pc_id})");
            var answerDesc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = answer.sdp };
            var srOp = pc.SetRemoteDescription(ref answerDesc);
            yield return srOp;
            if (srOp.IsError) { Debug.LogError("[Pipecat] SetRemoteDescription: " + srOp.Error.message); reconnecting = false; yield break; }
            Debug.Log($"[Pipecat] Remote description set — {(iceRestart ? "media path restarting..." : "connecting media...")}");
        }
    }

    // Add useinbandfec=1 (and a packet-loss hint) to the Opus fmtp line so the
    // encoder emits FEC-protected frames the server can use to reconstruct lost
    // mic audio before it reaches the VAD. We find Opus's payload type from its
    // rtpmap, then patch (or append) its fmtp line. No-op if no Opus line is found.
    // EXPERIMENTAL — only effective if the underlying libwebrtc honors the SDP.
    private static string MungeOpusFec(string sdp)
    {
        var rtpmap = Regex.Match(sdp, @"a=rtpmap:(\d+)\s+opus/48000", RegexOptions.IgnoreCase);
        if (!rtpmap.Success) return sdp;
        string pt = rtpmap.Groups[1].Value;

        var fmtp = Regex.Match(sdp, @"a=fmtp:" + pt + @"\s+([^\r\n]*)");
        if (fmtp.Success)
        {
            string parms = fmtp.Groups[1].Value;
            if (parms.Contains("useinbandfec")) return sdp; // already present
            string patched = "a=fmtp:" + pt + " " + parms + ";useinbandfec=1";
            return sdp.Substring(0, fmtp.Index) + patched + sdp.Substring(fmtp.Index + fmtp.Length);
        }
        // No fmtp line for Opus — insert one right after its rtpmap line.
        int eol = sdp.IndexOf('\n', rtpmap.Index);
        if (eol < 0) return sdp;
        return sdp.Substring(0, eol + 1) + "a=fmtp:" + pt + " useinbandfec=1\r\n" + sdp.Substring(eol + 1);
    }

    // Play the agent voice through the agent avatar's AudioSource so its existing
    // OVR Lip Sync moves the mouth (minimal path). Falls back to nothing if no sink.
    private void RouteToAgentSink()
    {
        if (remoteTrack == null || agentSink == null) return;
        agentSink.SetTrack(remoteTrack);
        agentSink.loop = true;
        agentSink.Play();
    }

    private void TrySendClientReady()
    {
        if (clientReadySent || !dcReady || !greetReleased || dc == null) return;
        if (dc.ReadyState != RTCDataChannelState.Open) return;
        string id = Guid.NewGuid().ToString().Substring(0, 8);
        string clientReady =
            "{\"id\":\"" + id + "\",\"label\":\"rtvi-ai\",\"type\":\"client-ready\",\"data\":{\"version\":\"" +
            rtviVersion + "\",\"about\":{\"library\":\"unity-webrtc\"}}}";
        dc.Send(clientReady);
        clientReadySent = true;
        Debug.Log("[Pipecat] client-ready sent -> agent will greet now");
    }

    private void OnDcOpen()
    {
        Debug.Log("[Pipecat] data channel open (connection ready; greeting held until Start)");
        dcReady = true;
        IsConnected = true;
        TrySendClientReady(); // sends only if Start was already pressed (slow-ICE case)
    }

    private void OnRemoteAudio(float[] data, int channels, int sampleRate)
    {
        if (tearingDown || agentLipSync == null) return;
        agentLipSync.ProcessAudioSamplesRaw((float[])data.Clone(), channels);
    }

    // DIAGNOSTIC (removable): once/sec logs (a) the agent's viseme energy and (b) the
    // MAX live blendshape weight on the agent's face mesh. This splits the t2/t3
    // dead-mouth cause:
    //   viseme>0 AND maxBlend>0  -> morph target IS writing weights; mesh/rig doesn't
    //                               visibly deform -> avatar asset issue.
    //   viseme>0 AND maxBlend==0 -> morph target's apply isn't running for this agent.
    //   viseme==0                -> OVR context not being fed.
    private float visemeLogTimer;
    private SkinnedMeshRenderer diagFaceMesh; // resolved once from the agent sink's avatar
    private void LogVisemeDiag()
    {
        if (agentLipSync == null) return;
        visemeLogTimer += Time.deltaTime;
        if (visemeLogTimer < 1f) return;
        visemeLogTimer = 0f;

        float speech = 0f;
        var frame = agentLipSync.GetCurrentPhonemeFrame();
        if (frame != null && frame.Visemes != null && frame.Visemes.Length > 0)
            speech = 1f - frame.Visemes[0];

        // Find the face mesh (H_DDS_HighRes) under the agent's avatar root, once.
        if (diagFaceMesh == null && agentSink != null)
        {
            var root = agentZone != null ? agentZone.transform.root : agentSink.transform.root;
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr.name == "H_DDS_HighRes") { diagFaceMesh = smr; break; }
        }
        float maxBlend = 0f;
        if (diagFaceMesh != null && diagFaceMesh.sharedMesh != null)
        {
            int n = diagFaceMesh.sharedMesh.blendShapeCount;
            for (int i = 0; i < n; i++)
            {
                float w = diagFaceMesh.GetBlendShapeWeight(i);
                if (w > maxBlend) maxBlend = w;
            }
        }
        Debug.Log($"[Pipecat] agent={agentId} speechActivity={speech:F3} maxBlendWeight={maxBlend:F1} faceMesh={(diagFaceMesh != null ? "ok" : "MISSING")}");
    }

    private void OnDcMessage(byte[] bytes)
    {
        string json = Encoding.UTF8.GetString(bytes);
        Debug.Log("[Pipecat] DC <- " + json);

        // Capture the raw RTVI event stream verbatim into the run telemetry (no
        // client-side parsing — qoe-lab stores the envelope whole; turn definition +
        // latency analysis happen offline from this server-authoritative record).
        // OnMessage fires on the Unity main thread, so this is safe. No-op between
        // runs (the briefing greeting is before BeginRun, correctly not captured).
        QoeDevice.QoeTurnLog.RecordRawEvent(json);

        // Drive the active agent's attentive pose from the bot's speaking state.
        // No "talking" animation exists — we hold the listening pose + face the
        // player while the bot speaks (as the old pipeline did), release on stop.
        // Field-agnostic substring match on the RTVI envelope.
        if (json.Contains("bot-started-speaking"))
        {
            HasAgentSpoken = true; // gates the Start button (agent is now audibly talking)
            if (agentZone != null) agentZone.SetBotSpeaking(true);
        }
        else if (json.Contains("bot-stopped-speaking"))
        {
            if (agentZone != null) agentZone.SetBotSpeaking(false);
        }

        // Done-button auto-reveal: the agent appends "<END>" to its farewell when the
        // user signals they're done (agents_config.py SHARED_STYLE). It is not stripped
        // server-side, so it arrives here in the bot's text (bot-llm-text/bot-tts-text).
        // A substring match on the raw message is robust to which field carries it.
        // Reveals the Done button; NotifyConversationOver is guarded (RunningTask only,
        // idempotent) so a match in the briefing greeting or a repeat is harmless.
        if (json.Contains("<END>"))
        {
            QoeDevice.QoeDeviceClient.NotifyConversationOver();
        }
    }

    private void Update()
    {
        LogVisemeDiag(); // DIAGNOSTIC (removable)
        if (dc == null) return;

        if (dc.ReadyState == RTCDataChannelState.Open)
        {
            keepAliveTimer += Time.deltaTime;
            if (keepAliveTimer >= 1f)
            {
                keepAliveTimer = 0f;
                dc.Send("ping: " + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
        }

        // Mic is hot only while the study's conversation gate is open. (The gate is
        // closed at neutral spawn / during briefing / after run-end.) Muting the
        // track transmits silence without renegotiating — the session stays up.
        bool micShouldBeHot = StudyControls.conversationGateOpen;
        if (micTrack != null && micTrack.Enabled != micShouldBeHot)
            micTrack.Enabled = micShouldBeHot;

        SampleMicLevel(micShouldBeHot);
        ServiceReconnect();
    }

    // Sample the live capture level so the HUD can show the participant their voice
    // is being picked up. Reads a window of the looping mic clip at the current
    // record head and tracks peak amplitude, with asymmetric smoothing (fast attack,
    // slow release) so the meter is responsive but not jittery. Zero when the mic is
    // muted (gate closed) so the meter reads dead, matching what's transmitted.
    private float[] _micSampleBuf = new float[256];
    private void SampleMicLevel(bool hot)
    {
        if (!hot || micClip == null || string.IsNullOrEmpty(micDevice))
        {
            MicLevel = Mathf.MoveTowards(MicLevel, 0f, Time.deltaTime * 4f);
            return;
        }
        int pos = Microphone.GetPosition(micDevice) - _micSampleBuf.Length;
        if (pos < 0) { return; }
        micClip.GetData(_micSampleBuf, pos);
        float g = micGain < 0f ? 0f : micGain;  // match the gain applied to the transmitted signal
        float peak = 0f;
        for (int i = 0; i < _micSampleBuf.Length; i++)
        {
            float a = _micSampleBuf[i] * g; if (a < 0f) a = -a;
            if (a > 1f) a = 1f;  // same soft clamp as MicGainFilter
            if (a > peak) peak = a;
        }
        // Attack quickly toward a rising level, release slowly so it doesn't flicker.
        MicLevel = peak > MicLevel
            ? Mathf.Lerp(MicLevel, peak, 0.6f)
            : Mathf.MoveTowards(MicLevel, peak, Time.deltaTime * 1.5f);
    }

    // Watchdog: if the peer connection sits in Disconnected/Failed past the grace
    // window, trigger an in-place ICE restart (renegotiate the existing session)
    // rather than leaving the conversation dead. Rate-limited by a cooldown so a
    // persistently bad path isn't hammered. The bot session + context are preserved.
    private void ServiceReconnect()
    {
        if (reconnectCooldown > 0f) reconnectCooldown -= Time.deltaTime;
        if (!autoReconnect || tearingDown || pc == null || reconnecting) return;

        bool down = connState == RTCPeerConnectionState.Disconnected
                 || connState == RTCPeerConnectionState.Failed;
        if (!down) { disconnectedFor = 0f; return; }

        disconnectedFor += Time.deltaTime;
        if (disconnectedFor < reconnectAfterSeconds || reconnectCooldown > 0f) return;

        Debug.LogWarning($"[Pipecat] connection {connState} for {disconnectedFor:F1}s — attempting ICE restart");
        reconnecting = true;
        disconnectedFor = 0f;
        reconnectCooldown = reconnectCooldownSeconds;
        StartCoroutine(NegotiateRoutine(iceRestart: true));
    }

    private void OnDestroy()
    {
        Disconnect();
    }
}

// Multiplies the captured mic buffer in-place by PipecatClient.micGain. Added to
// the mic AudioSource's GameObject BEFORE com.unity.webrtc's own capture filter,
// so it runs first in the DSP chain and scales the samples WebRTC then grabs and
// transmits (WebRTC reads raw filter-chain buffers, bypassing AudioSource.volume).
// Samples are soft-limited to avoid hard digital clipping when gain is high.
public class MicGainFilter : MonoBehaviour
{
    [NonSerialized] public PipecatClient owner;

    private void OnAudioFilterRead(float[] data, int channels)
    {
        // Read once per buffer; on the audio thread, so no Unity API calls here.
        float g = owner != null ? owner.micGain : 1f;
        if (g == 1f) return;
        if (g < 0f) g = 0f;
        for (int i = 0; i < data.Length; i++)
        {
            float s = data[i] * g;
            // Soft clamp into [-1, 1] so a high gain saturates rather than wraps.
            if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
            data[i] = s;
        }
    }
}
