using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.UI;

public class TrainingSceneController : MonoBehaviour
{
    [SerializeField] private AudioSource botAudioSource;

    [SerializeField] private Transform interactiveShirt;
    private static int trainingPhaseIdx = 0;
    private static int pathPhaseIdx = 0;

    [SerializeField] private Text transcriptionTextUI;
    [SerializeField] private GameObject endOfSceneObject;

    // Artificial response delay removed for the QoE thesis (only real network
    // delay is measured). timeStamp_UserFinishedInput is kept for logging.
    private float timeStamp_UserFinishedInput = 0.0f;

    private static List<string> taskStringsForDisplay = new List<string>();
    private static List<TMP_Text> taskTexts;
    private static List<RectTransform> micActiveObjects = new List<RectTransform>();

    public enum InventoryItem
    {
        ConfirmationCode, Shirt, HotelDirectory, HotelKey, MealPass, MuseumTicket, MuseumBrochure
    }

    private static Dictionary<InventoryItem, Sprite> inventorySprites;
    private static List<Image> inventorySlotsOnUI = new List<Image>();

    private static MicrophoneHandler microphoneHandler;

    private static readonly List<string> tasks = new List<string>
    {
        "(1) Move forward",
        "(2) Turn and move right",
        "(3) Complete the surveys",
        "(4) Pick up an object",
        "(5) Use Microphone"
    };

    [SerializeField] private InputActionReference controllerMicButton;

    public void OnTrainingSceneShirtPickup()
    {
        StartCoroutine(OnTrainingSceneShirtPickupIEnumerator());
    }

    private IEnumerator OnTrainingSceneShirtPickupIEnumerator()
    {
        yield return new WaitForSeconds(1.5f);
        Training_AddItemToInventory(InventoryItem.Shirt);
        interactiveShirt.gameObject.SetActive(false);
        MoveToNextPhase();
    }

    public static void MoveToNextPhase(bool advancePath = true)
    {
        // One-off mode has no tutorial walkthrough; nothing to advance.
        if (StudyControls.oneOffConversations)
        {
            return;
        }
        if (advancePath)
        {
            PathRenderer.EnablePathAt(pathPhaseIdx);
            pathPhaseIdx++;
        }
        AdvanceTaskOnUI(tasks[trainingPhaseIdx]);
        trainingPhaseIdx++;
        microphoneHandler.PlayNewTaskAvailableNotificationSound();
    }

    private void InitializeTaskUI()
    {
        taskStringsForDisplay = new List<string>
        {
            "<b>Tasks</b>",
        };

        SetUpdatedTaskText();
        MoveToNextPhase();
    }

    public static void AdvanceTaskOnUI(string task)
    {
        if (taskStringsForDisplay.Count > 1)
        {
            taskStringsForDisplay[taskStringsForDisplay.Count - 1] = $"<s>{taskStringsForDisplay[taskStringsForDisplay.Count - 1]}</s>";
        }

        taskStringsForDisplay.Add(task);
        SetUpdatedTaskText();
    }

    private static void SetUpdatedTaskText()
    {
        string text = "";
        foreach (string task in taskStringsForDisplay)
        {
            text += task + "\n";
        }
        foreach (TMP_Text taskText in taskTexts)
        {
            taskText.text = text;
        }
    }

    public static void SetMicActiveObjects(bool active)
    {
        foreach (Transform micActiveObject in micActiveObjects)
        {
            micActiveObject.gameObject.SetActive(active);
        }
    }

    private static TrainingSceneController instance;

    // Hard-stop the Training agent at end-of-run. Training uses its own audio
    // path (botAudioSource) instead of the zone pipeline, so AgentSelectionController
    // .StopAllAgents() doesn't reach it. Cut the clip and clear thinking state so a
    // late reply can't keep talking after the subject is teleported to neutral.
    public static void StopAudio()
    {
        if (instance == null) return;
        if (instance.botAudioSource != null) instance.botAudioSource.Stop();
        instance.ResetThinking();
    }

    private void Awake()
    {
        instance = this;
    }

    private void Start()
    {
        // NOTE: the backend conversation refresh is no longer done here. With
        // Training + Hotel merged into one scene, an unconditional /refresh/Training/
        // at startup raced the Hotel refresh and left the global handler on
        // Training — so Hotel agents answered with Alfred's prompt + voice.
        // Refresh now happens per-teleport in QoeDeviceClient.TeleportToTask,
        // which resets the correct scene right before the player talks.

        microphoneHandler = FindObjectOfType<MicrophoneHandler>();
        if (microphoneHandler == null)
        {
            Debug.LogError("MicrophoneHandler not found.");
        }

        // The mic pipeline below is the only part of this controller the QoE study
        // needs. The quest task-UI / inventory walkthrough is one-off-disabled, so
        // skip finding & seeding those scene objects — they may be deleted from
        // QoE_Shell entirely.
        if (StudyControls.oneOffConversations)
        {
            return;
        }

        taskTexts = FindObjectsOfType<TMP_Text>().Where(t => t.name == "task_ui_text").ToList();

        // all gameobjects name "mic_active_object" need to be found even if they are disabled
        micActiveObjects = FindObjectsOfType<RectTransform>().Where(t => t.name == "mic_active_object").ToList();
        SetMicActiveObjects(false);

        // Initialize the inventory
        inventorySprites = new Dictionary<InventoryItem, Sprite>
        {
            { InventoryItem.Shirt, Resources.Load<Sprite>("Inventory/Red_Shirt") },
        };

        // find the four objects "InventorySlot1", "InventorySlot2", "InventorySlot3", "InventorySlot4"
        // they have an Image component where sprites will be placed
        var inventorySlots = FindObjectsOfType<RectTransform>().Where(t => t.name.Contains("InventorySlot")).ToList();
        foreach (var slot in inventorySlots)
        {
            var img = slot.GetComponent<Image>();
            if (img != null)
            {
                inventorySlotsOnUI.Add(img);
            }
            slot.gameObject.SetActive(false);
        }

        Invoke(nameof(InitializeTaskUI), .1f);
    }

    [Header("Mic proximity gate")]
    [Tooltip("The Training agent's head. The mic only activates for this agent when the player's camera is within range. Assign in the Inspector.")]
    [SerializeField] private Transform trainingAgentHead;

    [Tooltip("Max distance (m) from the camera to the Training agent's head for the mic to activate here.")]
    [SerializeField] private float micActivationRange = 2.5f;

    // True while the player camera is within micActivationRange of the Training
    // agent. The zone-based StudyControls pipeline reads this so it stays silent
    // here instead of playing its "mic unavailable" sound — both pipelines share
    // one button, and the Training agent owns it at this spot.
    public static bool PlayerNearTrainingAgent { get; private set; }

    private bool warnedNoHead = false;

    private bool ComputePlayerNearTrainingAgent()
    {
        if (trainingAgentHead == null)
        {
            if (!warnedNoHead)
            {
                Debug.LogWarning("TrainingSceneController: 'trainingAgentHead' not assigned — Training mic is disabled. Assign the Training agent's head in the Inspector.");
                warnedNoHead = true;
            }
            return false;
        }

        Camera cam = Camera.main;
        if (cam == null) return false;
        return Vector3.Distance(cam.transform.position, trainingAgentHead.position) <= micActivationRange;
    }

    private void Update()
    {
        PlayerNearTrainingAgent = ComputePlayerNearTrainingAgent();

        bool micPressed = Input.GetKeyDown(KeyCode.M)
            || (controllerMicButton != null && controllerMicButton.action.WasPressedThisFrame());
        if (!micPressed) return;

        // Only this agent handles the mic when the player is near it; otherwise
        // let the zone-based StudyControls pipeline (Hotel/City/Museum) take it.
        if (!PlayerNearTrainingAgent) return;

        HandleMicButtonInput();
    }

    private bool isThinking = false;
    private Coroutine thinkingTimeoutCoroutine;
    private const float kThinkingTimeoutSeconds = 30f;

    private IEnumerator ThinkingTimeout()
    {
        yield return new WaitForSeconds(kThinkingTimeoutSeconds);
        if (isThinking)
        {
            Debug.LogWarning("TrainingSceneController: thinking timeout — resetting mic.");
            ResetThinking();
        }
    }

    private void ResetThinking()
    {
        isThinking = false;
        if (thinkingTimeoutCoroutine != null)
        {
            StopCoroutine(thinkingTimeoutCoroutine);
            thinkingTimeoutCoroutine = null;
        }
    }

    private void HandleMicButtonInput()
    {
        if (!microphoneHandler.IsRecording)
        {
            // Conversation gate (QoE): subject hasn't pressed "Start" yet, or the
            // run has ended. Stay silent — mirrors StudyControls' zone pipeline.
            if (!StudyControls.conversationGateOpen)
            {
                return;
            }

            if (isThinking || botAudioSource.isPlaying)
            {
                microphoneHandler.PlayMicUnavailableSound();
                return;
            }
            microphoneHandler.StartRecording();
            SetMicActiveObjects(true);
        }
        else
        {
            microphoneHandler.StopRecording();
            SetMicActiveObjects(false);
            var audioBytes = microphoneHandler.GetLatestMicAudioBytes();
            StartCoroutine(ServerInterface.instance.UploadAudioBytes(audioBytes, PrintTranscriptionAndSendResponseGenerationRequest));
            timeStamp_UserFinishedInput = Time.time;
            isThinking = true;
            thinkingTimeoutCoroutine = StartCoroutine(ThinkingTimeout());
        }
    }

    private static int micInputsDone = 0;

    public void PrintTranscriptionAndSendResponseGenerationRequest(string transcription)
    {
        var transcription_on_ui = $"You said: \"{transcription}\"";
        if (transcriptionTextUI != null) transcriptionTextUI.text = transcription_on_ui;
        micInputsDone++;

        if (micInputsDone == 3 && endOfSceneObject != null)
        {
            endOfSceneObject.SetActive(true);
        }

        StartCoroutine(GenerateResponseToTranscription(transcription));
    }

    private IEnumerator GenerateResponseToTranscription(string text)
    {
        string encodedText = UnityWebRequest.EscapeURL(text);
        // Use the Inspector-configured backend host (not hardcoded 127.0.0.1) so
        // Training works on the netem topology where Unity and the backend are on
        // separate machines.
        string baseUrl = ServerInterface.instance != null
            ? ServerInterface.instance.MiddlewareBaseUrl
            : "http://127.0.0.1:8000";
        string url = $"{baseUrl}/speak/agent1/?q={encodedText}";

        print($"Sending a request to middleware server");

        using (UnityWebRequest webRequest = UnityWebRequest.Get(url))
        {
            yield return webRequest.SendWebRequest();

            if (webRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"Error contacting speak endpoint: {webRequest.error}");
                ResetThinking();
            }
            else
            {
                Debug.Log($"Received: {webRequest.downloadHandler.text}");
                TrainingSpeechResponse speechResponse = ExtractInfoFromResponse(webRequest.downloadHandler.text);

                string audioFileUrl = $"{baseUrl}/{speechResponse.audio}";

                StartCoroutine(Training_DownloadAndPlayAudio(audioFileUrl, speechResponse));
            }
        }
    }

    public static float GetNormalRandom(float mean, float standardDeviation)
    {
        // Box-Muller transform
        float u1 = 1.0f - Random.value; // uniform(0,1) random number
        float u2 = 1.0f - Random.value;
        float randStdNormal = Mathf.Sqrt(-2.0f * Mathf.Log(u1)) * Mathf.Sin(2.0f * Mathf.PI * u2);
        return mean + standardDeviation * randStdNormal; // return the normally distributed value
    }

    private IEnumerator Training_DownloadAndPlayAudio(string audioUrl, TrainingSpeechResponse speechResponse)
    {
        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(audioUrl, AudioType.MPEG))
        {
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(www);

                // QoE thesis: no artificial response delay (see AgentSelectionController).
                // The Training agent plays as soon as its audio downloads; only real
                // network delay is present.
                botAudioSource.clip = clip;
                botAudioSource.Play();
                ResetThinking();
                timeStamp_UserFinishedInput = 0.0f;
            }
            else
            {
                Debug.LogError($"Failed to download audio clip: {www.error}");
                ResetThinking();
            }
        }
    }

    [System.Serializable]
    public class TrainingSpeechResponse
    {
        public string message;
        public string audio;
        public string transition;

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

    private TrainingSpeechResponse ExtractInfoFromResponse(string response)
    {
        return JsonUtility.FromJson<TrainingSpeechResponse>(response);
    }

    public static void Training_AddItemToInventory(InventoryItem item)
    {
        Sprite itemSprite = Training_GetSpriteForItem(item);

        foreach (var slot in inventorySlotsOnUI)
        {
            if (slot.gameObject.activeInHierarchy && slot.sprite != null)
            {
                if (slot.sprite.name == itemSprite.name)
                {
                    Debug.LogWarning($"{item} is already in inventory");
                    return;
                }
            }
        }

        foreach (var slot in inventorySlotsOnUI)
        {
            if (slot.sprite == null)
            {
                slot.sprite = itemSprite;
                slot.gameObject.SetActive(true);
                break;
            }
        }
    }

    public static void Training_RemoveItemFromInventory(InventoryItem item)
    {
        Sprite itemSprite = Training_GetSpriteForItem(item);

        foreach (var slot in inventorySlotsOnUI)
        {
            if (slot.sprite != null && slot.gameObject.activeInHierarchy)
            {
                if (slot.sprite.name == itemSprite.name)
                {
                    slot.sprite = null;
                    slot.gameObject.SetActive(false);
                    break;
                }
            }
        }
    }

    public static Sprite Training_GetSpriteForItem(InventoryItem item)
    {
        if (inventorySprites.TryGetValue(item, out Sprite sprite))
        {
            return sprite;
        }
        else
        {
            Debug.LogError($"Sprite for {item} not found!");
            return null;
        }
    }
}