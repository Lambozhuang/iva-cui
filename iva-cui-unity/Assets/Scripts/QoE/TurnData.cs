// Per-turn metadata handed to the avatar/QoE layer for one agent reply.
//
// Extracted from the old ServerInterface.SpeechResponse so the surviving QoE +
// avatar code (AgentSelectionController, StudyTasks, SceneProfiling) no longer
// depends on the deleted HTTP voice pipeline. Carries exactly the fields that
// cross the boundary into the harness; the WebRTC/Pipecat client will populate
// this later (or pass null, which every call site already tolerates).
//
// SEAM (Pipecat): when the streaming client is ported in, build a TurnData per
// agent turn and hand it to AgentSelectionController.PlayAudioForAgent.
[System.Serializable]
public class TurnData
{
    public string message;            // the agent's spoken reply text
    public bool conversation_over;    // user wrapped up -> end the run after this clip
    public string llm_client_name;
    public int user_input_word_count;
    public int response_word_count;
    public int transition_length;
    public float llm_generation_time;
    public float speech_generation_time;
}
