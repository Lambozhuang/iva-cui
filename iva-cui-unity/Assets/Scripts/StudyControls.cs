using LLMAgents;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class StudyControls : MonoBehaviour
{
    public static readonly bool USE_NEW_LOOKAWAY = false;

    private static readonly Dictionary<string, string> unitySceneNameMappping = new Dictionary<string, string>
    {
        { "City_Scene", "Shirts" },
        { "Hotel_Scene", "Hotel" },
        { "Museum_Scene", "Museum" }
    };

    public enum UserStudyScene
    { Shirts = 0, Hotel = 1, Museum = 2 }

    [Tooltip("Will be overwritten when in an actual study scene")]
    [SerializeField] private UserStudyScene userStudyScene = UserStudyScene.Shirts;

    private static string userStudySceneName;

    // SEAM (Pipecat): the old push-to-talk mic + HTTP backend were removed here.
    // The WebRTC/Pipecat client will own continuous mic capture; this class keeps
    // the conversation gate + thinking-state plumbing the study still needs.

    public delegate void OnASRResponseReceived(string text);

    public static bool someoneIsThinking = false;

    // QoE thesis: the conversation gate. The subject is teleported in front of an
    // agent and must read a short on-HUD context briefing, then press "Start"
    // before they can talk — and the timed measurement window begins at that same
    // press. While the gate is closed the mic is blocked in BOTH pipelines (this
    // zone pipeline and TrainingSceneController's). QoeDeviceClient owns this:
    // it closes the gate on teleport (briefing shown) and opens it on Start, then
    // closes it again at end-of-run. Defaults open so a scene opened without the
    // device client still behaves normally.
    public static bool conversationGateOpen = true;

    // QoE thesis: conversations are standalone, timed one-offs (no linear quest).
    // When true, the LLM "transition" signal is ignored — no path arrows, no
    // inventory changes, no survey pop-ups, no scene-progression. This is what
    // lets City/Museum agents run in the merged QoE_Shell scene: the old
    // per-scene HandleLLMDeterminedTask paths assume GetUserStudyScene() resolves
    // to the visited scene, but in one merged scene it can't, so they'd throw
    // NotImplementedException on every turn. Leave true for the study.
    public static bool oneOffConversations = true;

    private LLMAgents.AgentType speakingToThisAgent = LLMAgents.AgentType.Agent1;

    public static StudyControls instance;

    public bool ignoreUserStudyConditions = false;

    ///////////////////////////////////////////////////////////////////////////////////////
    /* STUDY CONDITION RELATED */
    ///////////////////////////////////////////////////////////////////////////////////////

    public enum WaitIndicatorType
    { None, Natural, Artificial }

    public enum DelayDuration
    { None, One, Two, Three }

    public static Dictionary<DelayDuration, float> delayDurations = new Dictionary<DelayDuration, float>
    {
        { DelayDuration.None, 0f },
        { DelayDuration.One, 1.75f },
        { DelayDuration.Two, 4.25f },
        { DelayDuration.Three, 6.75f }
    };

    public WaitIndicatorType waitIndicatorType = WaitIndicatorType.None;

    public DelayDuration delayDuration = DelayDuration.One;

    [Header("User study conditions")]
    [SerializeField] private bool doCounterbalancing = false;

    [SerializeField] private int counterbalanceOrder = 0;

    [Header("Input Related")]
    [SerializeField] private InputActionReference controllerMicButton;

    public int GetCounterBalanceOrder => counterbalanceOrder;

    private void Awake()
    {
        instance = this;
        DetermineUserStudySceneName();
    }

    private void OnEnable()
    {
        controllerMicButton?.action.Enable();
    }

    private void OnDisable()
    {
        controllerMicButton?.action.Disable();
    }

    #region CUI2025_Study

    public static string GetUserStudySceneName()
    {
        return userStudySceneName;
    }

    public static UserStudyScene GetUserStudyScene()
    {
        return instance.userStudyScene;
    }

    private void DetermineUserStudySceneName()
    {
        // Based on the Unity scene name, determine the user study scene name
        var sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        userStudySceneName = "";
        if (unitySceneNameMappping.ContainsKey(sceneName))
        {
            userStudySceneName = unitySceneNameMappping[sceneName];
            instance.userStudyScene = (UserStudyScene)System.Enum.Parse(typeof(UserStudyScene), userStudySceneName);
        }
        else
        {
            userStudySceneName = userStudyScene.ToString();
        }
    }

    private DelayDuration GetDelayLevelBasedOnConditionIdx(int conditionIdx)
    {
        // idx, delay, mitigation
        // 1, one, none,
        // 2, two, none,
        // 3, three, none,
        // 4, one, natural,
        // 5, two, natural,
        // 6, three, natural,
        // 7, one, artificial,
        // 8, two, artificial,
        // 9, three, artificial
        switch (conditionIdx)
        {
            case 1:
            case 4:
            case 7:
                return DelayDuration.One;

            case 2:
            case 5:
            case 8:
                return DelayDuration.Two;

            case 3:
            case 6:
            case 9:
                return DelayDuration.Three;
        }

        Debug.LogWarning("SOMETHING WENT VERY WRONG IN CONDITION INDEXING");
        return DelayDuration.None;
    }

    private WaitIndicatorType GetWaitIndicationTypeBasedOnConditionIdx(int conditionIdx)
    {
        // idx, delay, mitigation
        // 1, one, none,
        // 2, two, none,
        // 3, three, none,
        // 4, one, natural,
        // 5, two, natural,
        // 6, three, natural,
        // 7, one, artificial,
        // 8, two, artificial,
        // 9, three, artificial
        switch (conditionIdx)
        {
            case 1:
            case 2:
            case 3:
                return WaitIndicatorType.None;

            case 4:
            case 5:
            case 6:
                return WaitIndicatorType.Natural;

            case 7:
            case 8:
            case 9:
                return WaitIndicatorType.Artificial;
        }

        Debug.LogWarning("SOMETHING WENT VERY WRONG IN CONDITION INDEXING");
        return WaitIndicatorType.None;
    }

    private void SetUserStudyCondition(AgentType agentType)
    {
        // Ignore if counterbalancing is not enabled
        if (!doCounterbalancing)
        {
            return;
        }

        // Order of agents is:
        // scene 1 (shirts): friend, clerk, manager
        // scene 2 (hotel): receptionist, maintenance, waiter
        // scene 3 (museum): host, volunteer1, volunteer2
        // First we need to find the row that corresponds to current participant
        // Then we need to find the column that corresponds to the current scene and agent
        // Then we use that value to set the conditions

        List<List<int>> conditionOrders = new List<List<int>>
        {
            new List<int> { 1,2,9,3,8,4,7,5,6 },
            new List<int> { 7,6,8,5,9,4,1,3,2 },
            new List<int> { 3,4,2,5,1,6,9,7,8 },
            new List<int> { 9,8,1,7,2,6,3,5,4 },
            new List<int> { 5,6,4,7,3,8,2,9,1 },
            new List<int> { 2,1,3,9,4,8,5,7,6 },
            new List<int> { 7,8,6,9,5,1,4,2,3 },
            new List<int> { 4,3,5,2,6,1,7,9,8 },
            new List<int> { 9,1,8,2,7,3,6,4,5 },
            new List<int> { 6,5,7,4,8,3,9,2,1 },
            new List<int> { 2,3,1,4,9,5,8,6,7 },
            new List<int> { 8,7,9,6,1,5,2,4,3 },
            new List<int> { 4,5,3,6,2,7,1,8,9 },
            new List<int> { 1,9,2,8,3,7,4,6,5 },
            new List<int> { 6,7,5,8,4,9,3,1,2 },
            new List<int> { 3,2,4,1,5,9,6,8,7 },
            new List<int> { 8,9,7,1,6,2,5,3,4 },
            new List<int> { 5,4,6,3,7,2,8,1,9 },
        };
        // To determine order of conditions for this participant, we need to know the partcipant id
        // order of conditions = participant id % 18
        var conditionsOrder = conditionOrders[counterbalanceOrder % conditionOrders.Count];

        // Now let's find the condition index for the current scene and agent
        var conditionIdx = (int)GetUserStudyScene() * 3 + (int)agentType;

        waitIndicatorType = GetWaitIndicationTypeBasedOnConditionIdx(conditionsOrder[conditionIdx]);
        delayDuration = GetDelayLevelBasedOnConditionIdx(conditionsOrder[conditionIdx]);
    }

    #endregion CUI2025_Study

    private void Update()
    {
        // The Training agent owns the mic when the player stands near it (it has
        // no ActivationZone and runs its own pipeline). Stay out of the way so we
        // don't double-fire or play the "mic unavailable" sound over it.
        if (TrainingSceneController.PlayerNearTrainingAgent)
        {
            return;
        }

        if (Input.GetKeyDown(KeyCode.M))
        {
            HandleMicButtonInput();
            return;
        }

        if (controllerMicButton != null && controllerMicButton.action.WasPressedThisFrame())
        {
            HandleMicButtonInput();
            return;
        }
    }

    // SEAM (Pipecat): the push-to-talk mic button is gone. The WebRTC client
    // streams continuously; turn boundaries come from server VAD events, not a
    // button. Left as a no-op so the M/controller key bindings (above) don't
    // error. The conversationGateOpen flag is still the authority on when the
    // subject may talk — the Pipecat client should consult it before streaming.
    private void HandleMicButtonInput()
    {
        // intentionally empty — see seam note above.
    }

    // Stuck-mic guard. someoneIsThinking is set when the user finishes speaking
    // and is normally cleared on the success path (AgentSelectionController
    // .PlayAgentResponseAfterDelay). Under network impairment (netem loss/delay),
    // any failed ASR/TTS/download leaves it stuck true and the mic blocked
    // forever. This timeout resets it (and the agent's thinking indicator) so the
    // subject can talk again. Mirrors TrainingSceneController's ThinkingTimeout.
    private Coroutine thinkingTimeoutCoroutine;
    private const float kThinkingTimeoutSeconds = 30f;

    private void StartThinkingTimeout()
    {
        if (thinkingTimeoutCoroutine != null) StopCoroutine(thinkingTimeoutCoroutine);
        thinkingTimeoutCoroutine = StartCoroutine(ThinkingTimeout());
    }

    private System.Collections.IEnumerator ThinkingTimeout()
    {
        yield return new WaitForSeconds(kThinkingTimeoutSeconds);
        if (someoneIsThinking)
        {
            Debug.LogWarning("StudyControls: thinking timeout — resetting mic (response never arrived).");
            someoneIsThinking = false;
            AgentSelectionController.currentZone?.SetThinkingStatus(waitIndicatorType, false);
        }
        thinkingTimeoutCoroutine = null;
    }

    // Reset the mic/thinking pipeline at the end of a QoE run. Clears the
    // "someone is thinking" gate and stops the stuck-mic timeout so a late
    // response from the run that just ended can't flip state during the next
    // run. Called from QoeDeviceClient's end-of-run cleanup.
    public void ResetConversationState()
    {
        someoneIsThinking = false;
        if (thinkingTimeoutCoroutine != null)
        {
            StopCoroutine(thinkingTimeoutCoroutine);
            thinkingTimeoutCoroutine = null;
        }
        // SEAM (Pipecat): the WebRTC client owns mic capture now; nothing to stop here.
    }
}