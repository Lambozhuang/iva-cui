using System;
using System.Collections;
using System.Text;
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

    public enum KokoroVoice
    {
        af_heart, af_bella, af_nicole, af_aoede, af_kore, af_sarah,
        am_michael, am_fenrir, am_puck, am_echo, am_eric, am_liam,
        bf_emma, bf_isabella, bf_alice, bf_lily, bm_george, bm_fable, bm_lewis, bm_daniel
    }

    [Header("Agent TTS voice (sent at connect time)")]
    public KokoroVoice voice = KokoroVoice.af_heart;

    [Header("Lip sync fallback (optional)")]
    [Tooltip("Leave EMPTY for the minimal path (the avatar's own OVR context reads " +
             "its AudioSource). Only assign if the mouth does NOT move: then set that " +
             "context's Skip Audio Source = true and we feed it PCM directly.")]
    public OVRLipSyncContext lipSyncContext;

    // True once the peer connection + data channel are up (greeting still held).
    // QoeDeviceClient gates the Start button on this.
    public bool IsConnected { get; private set; }

    private RTCPeerConnection pc;
    private RTCDataChannel dc;
    private MediaStream sendStream;
    private AudioStreamTrack micTrack;
    private AudioStreamTrack remoteTrack;
    private AudioSource micSource;
    private AudioSource agentSink;     // the current agent avatar's lip-sync AudioSource
    private AudioClip micClip;
    private string micDevice;
    private float keepAliveTimer;

    private bool dcReady;              // data channel open
    private bool greetReleased;        // OpenConversation() called -> ok to send client-ready
    private bool clientReadySent;
    private bool tearingDown;

    [Serializable] private class OfferBody { public string sdp; public string type; public string pc_id; public bool restart_pc; public string voice; }
    [Serializable] private class AnswerBody { public string pc_id; public string sdp; public string type; }

    private void Start()
    {
        StartCoroutine(WebRTC.Update());
    }

    // === Study lifecycle entry points (called by QoeDeviceClient) ===

    // Begin connecting to the agent. `sink` is the agent avatar's lip-sync
    // AudioSource the reply should play through (its OVR context drives the mouth).
    // The greeting is held until OpenConversation().
    public void Connect(AudioSource sink)
    {
        if (pc != null) { Debug.LogWarning("[Pipecat] Connect() called while already connected — ignoring."); return; }
        agentSink = sink;
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
        if (agentSink != null) { agentSink.Stop(); agentSink.SetTrack(null); }
        if (dc != null) { dc.Close(); dc = null; }
        if (micTrack != null) { micTrack.Dispose(); micTrack = null; }
        if (sendStream != null) { sendStream.Dispose(); sendStream = null; }
        if (pc != null) { pc.Close(); pc = null; }
        if (!string.IsNullOrEmpty(micDevice)) { Microphone.End(micDevice); micDevice = null; }
        if (micSource != null) { Destroy(micSource); micSource = null; }
        remoteTrack = null; agentSink = null;
        dcReady = greetReleased = clientReadySent = IsConnected = false;
        tearingDown = false;
        Debug.Log("[Pipecat] disconnected");
    }

    // === Connection ===

    private IEnumerator InitMic()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("[Pipecat] No microphone device found.");
            yield break;
        }
        micDevice = Microphone.devices[0];
        micSource = gameObject.AddComponent<AudioSource>();
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
        pc.OnConnectionStateChange = s => Debug.Log($"[Pipecat] Peer connection: {s}");
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

        var offerOp = pc.CreateOffer();
        yield return offerOp;
        if (offerOp.IsError) { Debug.LogError("[Pipecat] CreateOffer: " + offerOp.Error.message); yield break; }

        var desc = offerOp.Desc;
        var slOp = pc.SetLocalDescription(ref desc);
        yield return slOp;
        if (slOp.IsError) { Debug.LogError("[Pipecat] SetLocalDescription: " + slOp.Error.message); yield break; }

        // aiortc is non-trickle: gather all ICE candidates BEFORE posting the offer.
        float gt = 0f;
        while (pc.GatheringState != RTCIceGatheringState.Complete && gt < 5f) { gt += Time.deltaTime; yield return null; }
        Debug.Log($"[Pipecat] ICE gathering: {pc.GatheringState} ({gt:F1}s)");

        var body = new OfferBody { sdp = pc.LocalDescription.sdp, type = "offer", pc_id = "", restart_pc = false, voice = voice.ToString() };
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
                yield break;
            }

            var answer = JsonUtility.FromJson<AnswerBody>(req.downloadHandler.text);
            Debug.Log($"[Pipecat] Got answer (pc_id={answer.pc_id})");
            var answerDesc = new RTCSessionDescription { type = RTCSdpType.Answer, sdp = answer.sdp };
            var srOp = pc.SetRemoteDescription(ref answerDesc);
            yield return srOp;
            if (srOp.IsError) { Debug.LogError("[Pipecat] SetRemoteDescription: " + srOp.Error.message); yield break; }
            Debug.Log("[Pipecat] Remote description set — connecting media...");
        }
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
        if (tearingDown || lipSyncContext == null) return;
        lipSyncContext.ProcessAudioSamplesRaw((float[])data.Clone(), channels);
    }

    private void OnDcMessage(byte[] bytes)
    {
        // SEAM (Pipecat): RTVI events (transcripts, bot-started/stopped-speaking,
        // metrics) arrive here. Logged for now; QoE re-sourcing is Milestone 6.
        Debug.Log("[Pipecat] DC <- " + Encoding.UTF8.GetString(bytes));
    }

    private void Update()
    {
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
    }

    private void OnDestroy()
    {
        Disconnect();
    }
}
