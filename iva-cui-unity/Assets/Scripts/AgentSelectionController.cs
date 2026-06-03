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

            ActivationZone nearest = null;
            float nearestDist = activationRange;
            foreach (ActivationZone zone in zones)
            {
                if (zone == null)
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
                if (zone == null)
                    continue;
                zone.SetPlayerInZone(zone == nearest);
            }
        }

        public static void PlayAudioForAgent(AgentType agentType, AudioClip audioClip, ServerInterface.SpeechResponse speechResponse)
        {
            foreach (ActivationZone zone in instance.zones)
            {
                if (zone.GetZoneAgentType() == agentType)
                {
                    // Get the delay duration for the agent -- from instance of StudyControls
                    var timeAsOfMicInputEnd = SceneProfiling.speakEnd;
                    var timeNow = Time.time;
                    var targetDelayDurationInDistribution = TrainingSceneController.GetNormalRandom(StudyControls.delayDurations[StudyControls.instance.delayDuration], 0.23f);

                    var remainingDelay = targetDelayDurationInDistribution - (timeNow - timeAsOfMicInputEnd);

                    Debug.Log($"Target delay: {targetDelayDurationInDistribution}.");

                    if (remainingDelay < 0)
                    {
                        Debug.LogWarning($"Target delay: {targetDelayDurationInDistribution}. DELAY WAS NEGATIVE ({remainingDelay}) -> RESPONSE WAS LATE");
                        remainingDelay = 0;
                    }

                    // But if the delay condition is "One", remove all delay since existing delay already follows the wanted distribution
                    if (StudyControls.instance.delayDuration == StudyControls.DelayDuration.One)
                    {
                        remainingDelay = 0.0f;
                    }

                    instance.StartCoroutine(instance.PlayAgentResponseAfterDelay(zone, audioClip, remainingDelay));
                    instance.StartCoroutine(StudyTasks.SetAgentFinishedTalkingAfterSeconds(audioClip.length + remainingDelay, agentType, speechResponse));
                    return;
                }
            }
        }

        private IEnumerator PlayAgentResponseAfterDelay(ActivationZone zone, AudioClip audioClip, float remainingDelay)
        {
            yield return new WaitForSeconds(remainingDelay);

            SceneProfiling.ttsVoicePlayStart = Time.time;
            StudyControls.someoneIsThinking = false;
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
    }
}