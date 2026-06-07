using System;
using System.Collections;
using System.Text;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;

// Unity <-> Pipecat (macos-local-voice-agents) voice agent over WebRTC.
// Ported from the proven PoC. Full-duplex: streams the mic up continuously and
// plays the agent's voice through an avatar AudioSource that already has OVR Lip
// Sync wired (so the mouth moves). Demo-quality, single file.
//
// MILESTONE 2 (single agent): assign `targetAudioSource` to one agent's existing
// lip-sync AudioSource (e.g. "Agent 1 LipSync"). Multi-agent per-encounter
// reconnect comes in Milestone 4.
//
// SEAM (Pipecat): this is where the old HTTP voice pipeline used to live. The
// study harness (conversationGateOpen, QoeTurnLog.CurrentEpoch) is consulted but
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

    [Header("Avatar audio (assign one agent's lip-sync AudioSource)")]
    [Tooltip("The agent avatar's existing AudioSource that drives OVR Lip Sync. " +
             "The agent voice plays through this so the mouth moves. If null, a " +
             "fallback child AudioSource is created (audible, but no lip sync).")]
    public AudioSource targetAudioSource;

    [Header("Lip sync fallback (optional)")]
    [Tooltip("Leave EMPTY for the minimal path (the avatar's own OVR context reads " +
             "targetAudioSource). Only assign this if the mouth does NOT move: then " +
             "set that context's Skip Audio Source = true and we feed it PCM directly.")]
    public OVRLipSyncContext lipSyncContext;

    [Header("Connect on Start (else call Connect() manually)")]
    public bool connectOnStart = true;

    private RTCPeerConnection pc;
    private RTCDataChannel dc;
    private MediaStream sendStream;
    private AudioStreamTrack micTrack;
    private AudioStreamTrack remoteTrack;
    private AudioSource micSource;
    private AudioClip micClip;
    private string micDevice;
    private float keepAliveTimer;
    private bool tearingDown;

    [Serializable] private class OfferBody { public string sdp; public string type; public string pc_id; public bool restart_pc; public string voice; }
    [Serializable] private class AnswerBody { public string pc_id; public string sdp; public string type; }

    private void Start()
    {
        StartCoroutine(WebRTC.Update());

        // If no avatar AudioSource was assigned, make a throwaway one so audio is at
        // least audible (no lip sync in that case).
        if (targetAudioSource == null)
        {
            var go = new GameObject("PipecatRemoteAudio");
            go.transform.SetParent(transform);
            targetAudioSource = go.AddComponent<AudioSource>();
            Debug.LogWarning("[Pipecat] No targetAudioSource assigned — created a fallback (no lip sync).");
        }

        if (connectOnStart) Connect();
    }

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

    public void Connect()
    {
        StartCoroutine(ConnectRoutine());
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
                Debug.Log("[Pipecat] Remote audio track received -> playing on avatar AudioSource");
                remoteTrack = at;
                // Play the agent voice through the avatar's lip-sync AudioSource so
                // its existing OVR Lip Sync context drives the mouth (minimal path).
                targetAudioSource.SetTrack(at);
                targetAudioSource.loop = true;
                targetAudioSource.Play();

                // Fallback path: if a lipSyncContext was assigned (because the minimal
                // path didn't move the mouth), feed it PCM directly. Harmless if null.
                at.onReceived += OnRemoteAudio;
            }
        };

        if (micSource != null)
        {
            sendStream = new MediaStream();
            micTrack = new AudioStreamTrack(micSource);
            pc.AddTrack(micTrack, sendStream);
        }

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

    // Worker-thread callback: only used for the split-tap lip-sync fallback.
    private void OnRemoteAudio(float[] data, int channels, int sampleRate)
    {
        if (tearingDown || lipSyncContext == null) return;
        lipSyncContext.ProcessAudioSamplesRaw((float[])data.Clone(), channels);
    }

    private void OnDcOpen()
    {
        Debug.Log("[Pipecat] data channel open -> sending client-ready");
        string id = Guid.NewGuid().ToString().Substring(0, 8);
        string clientReady =
            "{\"id\":\"" + id + "\",\"label\":\"rtvi-ai\",\"type\":\"client-ready\",\"data\":{\"version\":\"" +
            rtviVersion + "\",\"about\":{\"library\":\"unity-webrtc\"}}}";
        dc.Send(clientReady);
    }

    private void OnDcMessage(byte[] bytes)
    {
        // SEAM (Pipecat): RTVI events (transcripts, bot-started/stopped-speaking,
        // metrics) arrive here. Logged for now; QoE re-sourcing is Milestone 6.
        Debug.Log("[Pipecat] DC <- " + Encoding.UTF8.GetString(bytes));
    }

    private void Update()
    {
        if (dc != null && dc.ReadyState == RTCDataChannelState.Open)
        {
            keepAliveTimer += Time.deltaTime;
            if (keepAliveTimer >= 1f)
            {
                keepAliveTimer = 0f;
                dc.Send("ping: " + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
        }
    }

    private void OnDestroy()
    {
        tearingDown = true;
        if (remoteTrack != null) remoteTrack.onReceived -= OnRemoteAudio;
        if (dc != null) dc.Close();
        if (micTrack != null) micTrack.Dispose();
        if (sendStream != null) sendStream.Dispose();
        if (pc != null) pc.Close();
        if (!string.IsNullOrEmpty(micDevice)) Microphone.End(micDevice);
    }
}
