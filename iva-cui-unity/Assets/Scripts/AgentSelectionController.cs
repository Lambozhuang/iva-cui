using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace LLMAgents
{
    public enum AgentType
    {
        Agent1 = 0, Agent2 = 1, Agent3 = 2, None = 100
    }

    public class AgentSelectionController : MonoBehaviour
    {
        public static AgentSelectionController instance;

        [Tooltip("Auto-populated at startup from every ActivationZone in the scene " +
                 "(including currently-inactive ones under culled scene roots). The " +
                 "Inspector list is ignored — leave it empty. This is what keeps all 9 " +
                 "agents across all merged scenes registered without hand-maintaining a list.")]
        public List<ActivationZone> zones;

        public List<AudioClip> agent1AudioClips;
        public List<AudioClip> agent2AudioClips;
        public List<AudioClip> agent3AudioClips;

        public static ActivationZone currentZone;
        public static ActivationZone lastZone;

        [Tooltip("Max distance (m) from the player camera to an agent's head for that agent's zone to activate. Replaces the old trigger-collider zones.")]
        public float activationRange = 3f;

        private void Awake()
        {
            instance = this;

            // Auto-discover every ActivationZone in the scene rather than relying on
            // a hand-maintained Inspector list. The merged QoE_Shell has 9 zones
            // (3 per scene); a manually-assigned list silently broke when scene
            // objects were added/removed (e.g. only Hotel's 3 were registered, so
            // City/Museum agents couldn't hear the player). Include inactive zones
            // so the three scene roots that scene-culling disables are still in the
            // list — Update() re-checks isActiveAndEnabled each frame anyway.
            var found = FindObjectsByType<ActivationZone>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            zones = new List<ActivationZone>(found);
            Debug.Log($"AgentSelectionController: auto-registered {zones.Count} ActivationZone(s).");
        }

        // Proximity-based zone activation. The player teleports in front of an
        // agent (no walking), so we poll distance each frame and activate the
        // nearest agent within activationRange — the old trigger colliders never
        // fired reliably on a teleport. Sets/clears AgentSelectionController.currentZone
        // exactly like the old OnTriggerEnter/Exit did.
        private void Update()
        {
            if (zones == null || zones.Count == 0)
                return;

            Camera cam = Camera.main;
            if (cam == null)
                return;

            Vector3 camPos = cam.transform.position;

            // Only consider zones whose GameObject is active. Scene-root culling
            // (QoeDeviceClient.sceneRoots) disables the three scenes the player
            // isn't in, and calling SetPlayerInZone on an inactive zone would try
            // to StartCoroutine on an inactive GameObject (Unity error). agentType
            // is NOT unique — each scene has an Agent1/2/3 — so skipping inactive
            // zones is also what keeps the nearest match in the current scene.
            ActivationZone nearest = null;
            float nearestDist = activationRange;
            foreach (ActivationZone zone in zones)
            {
                if (zone == null || !zone.isActiveAndEnabled)
                    continue;
                float dist = Vector3.Distance(camPos, zone.GetProximityPoint());
                if (dist <= nearestDist)
                {
                    nearestDist = dist;
                    nearest = zone;
                }
            }

            // Exactly one zone is "in range" at a time (the nearest). Everyone
            // else is out. SetPlayerInZone no-ops when state is unchanged.
            foreach (ActivationZone zone in zones)
            {
                if (zone == null || !zone.isActiveAndEnabled)
                    continue;
                zone.SetPlayerInZone(zone == nearest);
            }
        }

        public static void PlayAudioForAgent(AgentType agentType, AudioClip audioClip, TurnData speechResponse, int turnEpoch, string userTranscript)
        {
            // Play on the zone the player is actually standing in, not the first
            // zone in the list that matches agentType. agentType is NOT unique:
            // each merged scene (City/Hotel/Museum) has an Agent1/2/3, so a
            // type-only match always resolved to the first scene's zone (Hotel),
            // which scene-culling disables when the player is elsewhere — hence the
            // "Can not play a disabled audio source" error in City/Museum.
            ActivationZone zone = currentZone;
            if (zone == null || zone.GetZoneAgentType() != agentType)
            {
                // Fallback: nearest active zone of this type (e.g. if the player
                // stepped out of range between speaking and the reply arriving).
                foreach (ActivationZone z in instance.zones)
                {
                    if (z != null && z.isActiveAndEnabled && z.GetZoneAgentType() == agentType)
                    {
                        zone = z;
                        break;
                    }
                }
            }
            if (zone == null)
            {
                Debug.LogWarning($"PlayAudioForAgent: no active zone for {agentType} — dropping response audio.");
                return;
            }

            // QoE thesis: NO artificial response delay. The CUI'25 paper injected a
            // synthetic delay distribution here as its manipulation; this thesis
            // measures the *real* delay caused by network impairment (netem), so any
            // added delay would corrupt the dependent variable. Play the response
            // the instant the audio has arrived.
            instance.StartCoroutine(instance.PlayAgentResponseAfterDelay(zone, audioClip, 0f, agentType, speechResponse, turnEpoch, userTranscript));
            instance.StartCoroutine(StudyTasks.SetAgentFinishedTalkingAfterSeconds(audioClip.length, agentType, speechResponse));
        }

        private IEnumerator PlayAgentResponseAfterDelay(ActivationZone zone, AudioClip audioClip, float remainingDelay, AgentType agentType, TurnData speechResponse, int turnEpoch, string userTranscript)
        {
            yield return new WaitForSeconds(remainingDelay);

            SceneProfiling.ttsVoicePlayStart = Time.time;
            StudyControls.someoneIsThinking = false;

            // QoE telemetry: record the turn the instant the reply begins to play.
            // Every "time to response" timestamp (speakEnd → ttsVoicePlayStart) is set
            // by now, and recording here — rather than clip.length seconds later —
            // means a run that ends (timer/Done/operator) mid-clip can't drop this
            // turn before it's captured. turnEpoch (captured at mic release) lets
            // RecordTurn drop a reply that arrived after its run already ended.
            // No-ops when there's no active QoE run.
            if (speechResponse != null)
            {
                QoeDevice.QoeTurnLog.RecordTurn(
                    turnEpoch, agentType, "zone", audioClip != null ? audioClip.length : 0f,
                    speechResponse.llm_generation_time, speechResponse.speech_generation_time, speechResponse.llm_client_name,
                    speechResponse.user_input_word_count, speechResponse.response_word_count, speechResponse.transition_length,
                    speechResponse.conversation_over, userTranscript, speechResponse.message);
            }

            if (StudyControls.USE_NEW_LOOKAWAY)
            {
                zone.LookAwayFromPlayerWhileThinking(false);
            }
            zone.LookAtPlayer(true);
            zone.PlayAudio(audioClip);
        }

        public AgentType GetActiveAgentType()
        {
            foreach (ActivationZone zone in zones)
                if (zone.GetIsActivated)
                    return zone.GetZoneAgentType();

            return AgentType.None;
        }

        public static bool SomeoneIsSpeaking()
        {
            foreach (ActivationZone zone in instance.zones)
                if (zone.AvatarCurrentlySpeaking)
                    return true;

            return false;
        }

        // Cut every agent's audio immediately. Called at the end of a QoE run so a
        // clip still playing when the timer expires (or the operator ends early)
        // doesn't keep talking after the subject is teleported to neutral. Loops
        // all zones (not just currentZone) because scene-culling may already have
        // moved the player, and a previously-active zone could still be playing.
        public static void StopAllAgents()
        {
            if (instance == null || instance.zones == null) return;
            foreach (ActivationZone zone in instance.zones)
                if (zone != null) zone.StopSpeaking();
        }
    }
}