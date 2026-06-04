using LLMAgents;
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public class ServerInterface : MonoBehaviour
{
    [Header("Server connection")]
    [Tooltip("Backend host IP or hostname (no port). The middleware and the ASR/Whisper server are assumed to be on this same host.")]
    public string serverHost = "127.0.0.1";

    [Tooltip("Port of the Python middleware (FastAPI): /speak, /refresh, /check_transition, /static.")]
    public int serverPort = 8000;

    [Tooltip("Port of the ASR / Whisper server. Uses the same host as above.")]
    public int whisperPort = 8083;

    // Combined forms used when building request URLs. Whisper shares serverHost,
    // so you only enter the IP once.
    private string HostIpPort => $"{serverHost}:{serverPort}";
    private string WhisperUrl => $"http://{serverHost}:{whisperPort}/transcribe_audio/";

    // Public middleware base URL (e.g. "http://192.168.1.50:8000") so other
    // controllers reuse the Inspector-configured host instead of hardcoding
    // 127.0.0.1 — required for the netem topology where Unity and the backend
    // run on different machines.
    public string MiddlewareBaseUrl => $"http://{HostIpPort}";

    public static ServerInterface instance;

    private void Awake()
    {
        instance = this;
    }

    private void Start()
    {
        if (refreshAtStart)
        {
            StartCoroutine(SendRefreshRequest(StudyControls.GetUserStudySceneName()));
        }
    }

    [SerializeField] private bool refreshAtStart = true;

    public static void RefreshConversation()
    {
        instance.StartCoroutine(instance.SendRefreshRequest(StudyControls.GetUserStudySceneName()));
    }

    public static void RefreshTrainingConversation()
    {
        instance.StartCoroutine(instance.SendRefreshRequest("Training"));
    }

    // Refresh the backend conversation for an explicit scene name (e.g. "Hotel",
    // "Training", "Museum", "Shirts"). The backend keeps a single global handler
    // built from transition_prompts_<scene>.py, so this selects which scene's
    // per-agent prompts + voices `agent1/2/3` resolve to. Call this when the
    // player teleports to an agent so the right scene is active server-side.
    public static void RefreshScene(string sceneName)
    {
        if (instance == null)
        {
            Debug.LogError("ServerInterface.RefreshScene: no instance.");
            return;
        }
        instance.StartCoroutine(instance.SendRefreshRequest(sceneName));
    }

    private IEnumerator SendRefreshRequest(string sceneToRefresh)
    {
        Debug.Log("Sending refresh request for " + sceneToRefresh);
        string url = $"http://{HostIpPort}/refresh/{sceneToRefresh}/";

        using (UnityWebRequest webRequest = UnityWebRequest.Get(url))
        {
            yield return webRequest.SendWebRequest();

            if (webRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"Error refreshing message history: {webRequest.error}");

                if (webRequest.error.ToLower().Contains("cannot connect"))
                {
                    Debug.LogError($"Make sure that Python backend is running on {HostIpPort}");
                }
            }
            else
            {
                Debug.Log($"Server response: {webRequest.downloadHandler.text}");
            }
        }
    }

    public IEnumerator UploadAudioBytes(byte[] audioBytes, Action<string> callback)
    {
        SceneProfiling.asrStart = Time.time;

        WWWForm form = new WWWForm();
        form.AddBinaryData("file", audioBytes, "temp.wav", "audio/wav");

        UnityWebRequest www = UnityWebRequest.Post(WhisperUrl, form);
        www.downloadHandler = new DownloadHandlerBuffer();

        yield return www.SendWebRequest();

        if (www.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("Error: " + www.error);
        }
        else
        {
            Debug.Log("Success: " + www.downloadHandler.text);
            string text = ExtractTranscriptiontFromResponse(www.downloadHandler.text);
            SceneProfiling.asrEnd = Time.time;
            callback?.Invoke(text);
        }
    }

    [System.Serializable]
    public class WhisperResponse
    {
        public string transcription;
    }

    private string ExtractTranscriptiontFromResponse(string response)
    {
        WhisperResponse whisperResponse = JsonUtility.FromJson<WhisperResponse>(response);
        return whisperResponse.transcription;
    }

    public IEnumerator SendTextToSpeechRequest(AgentType agentType, string text)
    {
        SceneProfiling.ttsReqStart = Time.time;
        string agentTypeString = agentType.ToString().ToLower();

        string encodedText = UnityWebRequest.EscapeURL(text);
        string url = $"http://{HostIpPort}/speak/{agentTypeString}/?q={encodedText}";

        print($"Sending a request to middleware server");

        using (UnityWebRequest webRequest = UnityWebRequest.Get(url))
        {
            yield return webRequest.SendWebRequest();

            if (webRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.Log($"Error: {webRequest.error}");
            }
            else
            {
                SceneProfiling.ttsReqEnd = Time.time;
                Debug.Log($"Received: {webRequest.downloadHandler.text}");
                SpeechResponse speechResponse = ExtractInfoFromResponse(webRequest.downloadHandler.text);

                ConversationLogger.LogAgentMessage(agentType, speechResponse.message);

                string audioFileUrl = $"http://{HostIpPort}/{speechResponse.audio}";

                StartCoroutine(DownloadAndPlayAudio(agentType, audioFileUrl, speechResponse));
            }
        }
    }

    private IEnumerator DownloadAndPlayAudio(AgentType agent, string audioUrl, SpeechResponse speechResponse)
    {
        SceneProfiling.ttsVoiceDownloadStart = Time.time;
        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(audioUrl, AudioType.MPEG))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                AgentSelectionController.PlayAudioForAgent(agent, clip, speechResponse);

                // QoE thesis: skip the per-turn transition check in one-off mode.
                // /check_transition fires a second LLM call every turn, but its
                // result is fed to HandleLLMDeterminedTask which early-outs under
                // oneOffConversations — so the call is pure wasted backend work
                // that competes with the next turn's /speak on the single LLM
                // server and adds uncontrolled latency to the QoE measurement.
                if (!StudyControls.oneOffConversations)
                {
                    StartCoroutine(SendTransitionCheckRequest(agent));
                }

                // Agent wrapped up (user said goodbye). Let the goodbye clip play,
                // then end the round so the subject goes straight to rating rather
                // than waiting out the timer. Guarded inside NotifyConversationOver
                // so it no-ops when there's no active QoE run (e.g. plain testing).
                if (speechResponse != null && speechResponse.conversation_over)
                {
                    StartCoroutine(EndRunAfterClip(clip != null ? clip.length : 0f));
                }

                SceneProfiling.ComputeTimes(speechResponse);
            }
            else
            {
                Debug.LogError($"Failed to download audio clip: {www.error}");
            }
        }
    }

    // Wait out the goodbye clip (plus a short beat so it isn't cut off), then ask
    // the QoE device client to end the round. A small fixed pad covers the gap
    // between download and the clip actually starting on the agent's AudioSource.
    private IEnumerator EndRunAfterClip(float clipLength)
    {
        yield return new WaitForSeconds(clipLength + 0.5f);
        QoeDevice.QoeDeviceClient.NotifyConversationOver();
    }

    [System.Serializable]
    public class SpeechResponse
    {
        public string message;
        public string audio;
        public string transition;

        // True when the agent wrapped up because the user signalled they were
        // done (backend stripped the <END> marker). The device ends the round
        // once this reply's audio finishes playing. JsonUtility leaves it false
        // when the field is absent, so older backends are handled gracefully.
        public bool conversation_over;

        // Fields arriving in JSON response
        public string llm_client_name;

        public int user_input_word_count;
        public int response_word_count;
        public int transition_length;
        public float llm_generation_time;
        public float speech_generation_time;

        public override string ToString()
        {
            return $"Message: {message}\n" +
                   $"Audio: {audio}\n" +
                   $"Transition: {transition}\n" +
                   $"LLM Client Name: {llm_client_name}\n" +
                   $"User Input Word Count: {user_input_word_count}\n" +
                   $"Response Word Count: {response_word_count}\n" +
                   $"Transition Length: {transition_length}\n" +
                   $"LLM Generation Time: {llm_generation_time}\n" +
                   $"Speech Generation Time: {speech_generation_time}";
        }
    }

    private SpeechResponse ExtractInfoFromResponse(string response)
    {
        return JsonUtility.FromJson<SpeechResponse>(response);
    }

    public IEnumerator SendTransitionCheckRequest(AgentType agentType)
    {
        string agentTypeString = agentType.ToString().ToLower();

        string url = $"http://{HostIpPort}/check_transition/{agentTypeString}/";

        Debug.Log($"Sending transition check for {agentTypeString} via {url}");

        using (UnityWebRequest webRequest = UnityWebRequest.Get(url))
        {
            yield return webRequest.SendWebRequest();

            if (webRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.Log($"Error: {webRequest.error}");
            }
            else
            {
                TransitionCheckResponse transitionResponse = ExtractTransitionCheckResponse(webRequest.downloadHandler.text);
                var transition = transitionResponse.transition;
                Debug.Log($">> Transition response: {transition}");

                StudyTasks.agentFinishedTalking = false;
                StartCoroutine(StudyTasks.HandleLLMDeterminedTask(transition));
            }
        }
    }

    [System.Serializable]
    public class TransitionCheckResponse
    {
        public string role;
        public string transition;
    }

    private TransitionCheckResponse ExtractTransitionCheckResponse(string response)
    {
        return JsonUtility.FromJson<TransitionCheckResponse>(response);
    }
}