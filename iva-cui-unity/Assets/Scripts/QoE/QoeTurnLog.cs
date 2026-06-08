using System;
using System.Collections.Generic;
using System.IO;
using LLMAgents;
using Newtonsoft.Json;
using UnityEngine;

namespace QoeDevice {
    /// <summary>
    /// QoE thesis — per-run conversation telemetry collector.
    ///
    /// Both audio pipelines (the zone pipeline: StudyControls → ServerInterface →
    /// AgentSelectionController; and the Training pipeline: TrainingSceneController)
    /// push one <see cref="TurnRecord"/> per user↔agent turn into here, tagged with
    /// the run identity QoeDeviceClient holds. At end-of-run the envelope is
    /// finalized, written to a JSON file on the device, and kept so it can be sent
    /// to the operator console when it asks (request_telemetry → POST /telemetry).
    ///
    /// Why a plain static (not a MonoBehaviour), mirroring SceneProfiling: it needs
    /// no Inspector wiring (the project has no .asmdef, so global statics are reached
    /// directly from both pipelines), and the run identity is PUSHED in via BeginRun
    /// rather than pulled, so this stays decoupled from QoeDeviceClient.
    ///
    /// The per-turn timestamps are read straight from SceneProfiling at the instant
    /// RecordTurn is called (playback start), which is an atomic snapshot within the
    /// turn — there is no yield between the pipeline setting them and RecordTurn
    /// copying them.
    ///
    /// Cross-RUN safety (a reply from a finished run completing after the next run has
    /// begun) is handled by a run epoch: BeginRun bumps a counter, each turn captures
    /// the epoch at dispatch (mic release) and carries it through the pipeline as a
    /// coroutine-local — immune to the shared SceneProfiling statics being overwritten
    /// by the next run — and RecordTurn drops any turn whose epoch no longer matches
    /// the active run. The user transcript is threaded the same way rather than held
    /// in a shared field, for the same reason.
    /// </summary>
    public static class QoeTurnLog {
        // ---- serializable payload DTOs (Newtonsoft; field names = exact JSON keys via JsonProperty) ----

        public class DeviceClock {
            // Lets an analyst map device Time.time (seconds since app start) to wall
            // clock: wall_ms(t) = unix_ms_at_run_start + (t - time_dot_time_at_run_start)*1000.
            // Captured back-to-back at run start; best-effort (no server time-sync).
            [JsonProperty("unix_ms_at_run_start")]      public long  unixMsAtRunStart;
            [JsonProperty("time_dot_time_at_run_start")] public float timeDotTimeAtRunStart;
        }

        public class TurnRecord {
            [JsonProperty("turn_index")] public int    turnIndex;
            [JsonProperty("request_id")] public int    requestId;   // SceneProfiling.randomRequestId — joins to latency_log.csv
            [JsonProperty("agent_type")] public string agentType;   // AgentType enum name (Agent1/2/3); disambiguate role with backend_scene
            [JsonProperty("pipeline")]   public string pipeline;    // "zone" | "training"
            [JsonProperty("complete")]   public bool   complete;    // true = full round-trip; recorded turns are always complete

            // Device-clock event timestamps (SECONDS, Time.time). Same fields the
            // existing latency_log.csv carries, so the two line up by request_id.
            [JsonProperty("t_speak_start")]        public float tSpeakStart;        // mic first pressed
            [JsonProperty("t_speak_end")]          public float tSpeakEnd;          // mic released — analysis t0 for "time to response"
            [JsonProperty("t_asr_start")]          public float tAsrStart;          // ASR upload POST sent
            [JsonProperty("t_asr_end")]            public float tAsrEnd;            // ASR response received
            [JsonProperty("t_tts_req_start")]      public float tTtsReqStart;       // /speak GET sent
            [JsonProperty("t_tts_req_end")]        public float tTtsReqEnd;         // /speak response received
            [JsonProperty("t_tts_download_start")] public float tTtsDownloadStart;  // /static mp3 GET begins
            [JsonProperty("t_tts_play_start")]     public float tTtsPlayStart;      // audio playback begins — "response heard"
            [JsonProperty("t_tts_play_end")]       public float tTtsPlayEnd;        // ESTIMATE: play_start + clip.length (not a hw callback)

            // Derived durations (SECONDS); redundant with t_* but precomputed for convenience.
            [JsonProperty("d_user_speak")]      public float dUserSpeak;       // speakEnd - speakStart
            [JsonProperty("d_asr")]             public float dAsr;             // asrEnd - asrStart
            [JsonProperty("d_tts_req")]         public float dTtsReq;          // ttsReqEnd - ttsReqStart
            [JsonProperty("d_tts_download")]    public float dTtsDownload;     // ttsVoicePlayStart - ttsVoiceDownloadStart
            [JsonProperty("d_total_response")]  public float dTotalResponse;   // ttsVoicePlayStart - speakEnd (device-observed time-to-response)
            [JsonProperty("d_audio_play")]      public float dAudioPlay;       // play_end - play_start (estimate; unreliable under impairment)

            // Server-reported fields threaded from the /speak response (NOT device clock).
            [JsonProperty("llm_generation_time_ms")]    public float  llmGenerationTimeMs;
            [JsonProperty("speech_generation_time_ms")] public float  speechGenerationTimeMs;
            [JsonProperty("llm_client_name")]           public string llmClientName;
            [JsonProperty("user_input_word_count")]     public int    userInputWordCount;
            [JsonProperty("response_word_count")]       public int    responseWordCount;
            [JsonProperty("transition_length")]         public int    transitionLength;
            [JsonProperty("conversation_over")]         public bool   conversationOver;

            // Transcript content, so the telemetry blob is self-contained per run.
            [JsonProperty("user_transcript")] public string userTranscript;  // ASR transcription of the user's utterance
            [JsonProperty("agent_reply")]     public string agentReply;      // SpeechResponse.message
        }

        // One raw RTVI data-channel message, captured verbatim. We store the whole
        // JSON string untouched (the operator/qoe-lab stores the envelope as an opaque
        // blob, so no client-side parsing of the metrics is needed) plus the device
        // Time.time at receipt so events can be ordered/aligned to the run clock in
        // offline analysis. This is the server-authoritative record of everything that
        // happened in the conversation (speaking events, transcripts, metrics, etc.).
        public class RawEvent {
            [JsonProperty("t")]   public float  t;     // Time.time at receipt (maps to device_clock)
            [JsonProperty("msg")] public string msg;   // verbatim RTVI JSON message
        }

        public class Envelope {
            // sid + condition_run_id are REQUIRED by the server, which strips them and
            // stores the rest of this object verbatim as an opaque JSON blob.
            [JsonProperty("sid")]                    public string      sid;            // null for debug runs (never POSTed)
            [JsonProperty("condition_run_id")]       public int         conditionRunId;
            [JsonProperty("schema_version")]         public int         schemaVersion = 2;
            [JsonProperty("task_number")]            public int?        taskNumber;     // null for training (mirrors start_task)
            [JsonProperty("task_index")]             public int         taskIndex;      // 0=Training, 1..9 zone
            [JsonProperty("label")]                  public string      label;
            [JsonProperty("backend_scene")]          public string      backendScene;   // Training/Shirts/Hotel/Museum
            [JsonProperty("pipeline")]               public string      pipeline;       // "zone" | "training"
            [JsonProperty("is_debug_run")]           public bool        isDebugRun;
            [JsonProperty("device_kind")]            public string      deviceKind;
            [JsonProperty("device_clock")]           public DeviceClock deviceClock;
            [JsonProperty("run_start_time")]         public float       runStartTime;   // Time.time at OnStartPressed
            [JsonProperty("run_end_time")]           public float       runEndTime;     // Time.time at FinishRun
            [JsonProperty("end_reason")]             public string      endReason;
            [JsonProperty("max_condition_duration_s")] public int       maxConditionDurationS;
            // The turn-records. Named "samples" so the server's `/telemetry` log line
            // (`samples=<n>`) reports the turn count, matching the XR simulator's body.
            [JsonProperty("samples")]                public List<TurnRecord> samples = new();

            // Raw, unparsed RTVI event stream for the run (Pipecat data-channel
            // messages: speaking events, transcripts, per-stage metrics). Stored whole
            // so turn-definition + latency analysis can be done offline from the
            // server-authoritative record, independent of the per-turn `samples` above.
            [JsonProperty("rtvi_events")]            public List<RawEvent> rtviEvents = new();
        }

        // ---- collector state ----

        static Envelope current;        // the run currently being recorded (null between runs)
        static Envelope lastFinalized;  // the most recently finished run, held for request_telemetry

        // Monotonic run epoch. Bumped each BeginRun; a turn captures the value live at
        // dispatch (see CurrentEpoch / mic release) and RecordTurn drops the turn if
        // it no longer matches — so a late reply from a finished run can't be recorded
        // into the next run. Starts at a non-zero value so an uncaptured/default 0
        // epoch from a stray call never accidentally matches.
        static int runEpoch = 1;

        static string DirPath => Path.Combine(Application.persistentDataPath, "Telemetry");

        public static bool HasRun => current != null;

        // The epoch of the run currently recording (or the run a turn should be bound
        // to). Captured at turn dispatch and passed back into RecordTurn. -1 when no
        // run is active, which never matches a real epoch.
        public static int CurrentEpoch => current != null ? runEpoch : -1;

        // Begin a fresh run envelope. Called from QoeDeviceClient.OnStartPressed —
        // the single run t0 shared by real and debug runs. Captures the clock base
        // (unix + Time.time back-to-back) so device timestamps can be mapped to wall
        // clock after the study.
        public static void BeginRun(string sid, int conditionRunId, int? taskNumber, int taskIndex,
                                    string label, string backendScene, bool isDebugRun,
                                    string deviceKind, int maxConditionDurationS, float runStartTime) {
            float t = runStartTime;
            long unix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            runEpoch++; // new run epoch — any turn still bound to the old epoch is now stale
            current = new Envelope {
                sid = sid,
                conditionRunId = conditionRunId,
                taskNumber = taskNumber,
                taskIndex = taskIndex,
                label = label,
                backendScene = backendScene,
                pipeline = taskIndex == 0 ? "training" : "zone",
                isDebugRun = isDebugRun,
                deviceKind = deviceKind,
                deviceClock = new DeviceClock { unixMsAtRunStart = unix, timeDotTimeAtRunStart = t },
                runStartTime = runStartTime,
                maxConditionDurationS = maxConditionDurationS,
            };
            QoeLog.Event("telemetry", $"run begin: run={conditionRunId} task={taskIndex} '{label}' pipeline={current.pipeline} debug={isDebugRun} epoch={runEpoch}");
        }

        // Record one completed turn. Called at PLAYBACK START (when the agent's reply
        // begins to play) from both pipelines. `epoch` is the run epoch captured when
        // this turn was dispatched (mic release); if it no longer matches the active
        // run, this reply belongs to a run that has already ended — drop it rather
        // than record cross-run garbage into the current envelope (its SceneProfiling
        // statics have since been overwritten by the new run). The server-side fields
        // are passed as primitives because the zone and Training pipelines use
        // distinct response types with no shared base; `userTranscript` is threaded
        // through the pipeline (not a shared field) for the same cross-run reason.
        public static void RecordTurn(int epoch, AgentType agentType, string pipeline, float clipLength,
                                      float llmGenerationTimeMs, float speechGenerationTimeMs, string llmClientName,
                                      int userInputWordCount, int responseWordCount, int transitionLength,
                                      bool conversationOver, string userTranscript, string agentReply) {
            if (current == null) {
                QoeLog.Warn("telemetry", "RecordTurn with no active run — dropping turn");
                return;
            }
            if (epoch != runEpoch) {
                QoeLog.Warn("telemetry", $"RecordTurn epoch {epoch} != active {runEpoch} — dropping stale turn from a finished run");
                return;
            }

            float speakStart        = SceneProfiling.speakStart;
            float speakEnd          = SceneProfiling.speakEnd;
            float asrStart          = SceneProfiling.asrStart;
            float asrEnd            = SceneProfiling.asrEnd;
            float ttsReqStart       = SceneProfiling.ttsReqStart;
            float ttsReqEnd         = SceneProfiling.ttsReqEnd;
            float ttsDownloadStart  = SceneProfiling.ttsVoiceDownloadStart;
            float ttsPlayStart      = SceneProfiling.ttsVoicePlayStart;
            float ttsPlayEnd        = ttsPlayStart + Mathf.Max(0f, clipLength); // estimate

            var rec = new TurnRecord {
                turnIndex = current.samples.Count,
                requestId = SceneProfiling.randomRequestId,
                agentType = agentType.ToString(),
                pipeline = pipeline,
                complete = true,

                tSpeakStart = speakStart,
                tSpeakEnd = speakEnd,
                tAsrStart = asrStart,
                tAsrEnd = asrEnd,
                tTtsReqStart = ttsReqStart,
                tTtsReqEnd = ttsReqEnd,
                tTtsDownloadStart = ttsDownloadStart,
                tTtsPlayStart = ttsPlayStart,
                tTtsPlayEnd = ttsPlayEnd,

                dUserSpeak = speakEnd - speakStart,
                dAsr = asrEnd - asrStart,
                dTtsReq = ttsReqEnd - ttsReqStart,
                dTtsDownload = ttsPlayStart - ttsDownloadStart,
                dTotalResponse = ttsPlayStart - speakEnd,
                dAudioPlay = ttsPlayEnd - ttsPlayStart,

                llmGenerationTimeMs = llmGenerationTimeMs,
                speechGenerationTimeMs = speechGenerationTimeMs,
                llmClientName = llmClientName,
                userInputWordCount = userInputWordCount,
                responseWordCount = responseWordCount,
                transitionLength = transitionLength,
                conversationOver = conversationOver,

                userTranscript = userTranscript ?? "",
                agentReply = agentReply ?? "",
            };
            current.samples.Add(rec);
            QoeLog.Event("telemetry", $"turn {rec.turnIndex} recorded ({pipeline}, {rec.agentType}): response in {rec.dTotalResponse:0.00}s");
        }

        // Append one raw RTVI data-channel message to the active run, verbatim. Called
        // from PipecatClient.OnDcMessage for every inbound message. No-op between runs
        // (so the per-encounter greeting that plays during briefing — before BeginRun —
        // is not captured, which is correct: it's not part of the measured run).
        public static void RecordRawEvent(string json) {
            if (current == null) return;
            current.rtviEvents.Add(new RawEvent { t = Time.time, msg = json });
        }

        // Finalize the run: stamp end reason + end time, write the envelope to a JSON
        // file on the device, and hold it as the last finalized run so it can be sent
        // when the operator console requests telemetry. Called from FinishRun for
        // every reason (timer / Done / operator), debug and real alike. Idempotent:
        // a second call with no active run is a no-op.
        public static void FinishRun(string endReason, float runEndTime) {
            if (current == null) return;
            current.endReason = endReason;
            current.runEndTime = runEndTime;
            lastFinalized = current;
            current = null;
            WriteToDisk(lastFinalized);
            QoeLog.Event("telemetry", $"run finalized: run={lastFinalized.conditionRunId} '{lastFinalized.label}' turns={lastFinalized.samples.Count} reason='{endReason}'");
        }

        // True when the last finalized run matches this (sid, condition_run_id) — so
        // a request_telemetry for the just-finished run can be answered.
        public static bool LastFinalizedMatches(string sid, int conditionRunId) {
            return lastFinalized != null
                && lastFinalized.conditionRunId == conditionRunId
                && (string.IsNullOrEmpty(sid) || lastFinalized.sid == sid);
        }

        // The POST /telemetry body for the last finalized run, or null if there is
        // none or it has no sid (debug run → never POSTed, disk only).
        public static string GetLastEnvelopeJson() {
            if (lastFinalized == null || string.IsNullOrEmpty(lastFinalized.sid)) return null;
            return JsonConvert.SerializeObject(lastFinalized);
        }

        public static int LastFinalizedRunId => lastFinalized?.conditionRunId ?? -1;

        static void WriteToDisk(Envelope env) {
            try {
                if (!Directory.Exists(DirPath)) Directory.CreateDirectory(DirPath);
                string sidPart = string.IsNullOrEmpty(env.sid) ? "debug" : env.sid;
                string fname = $"telemetry_{sidPart}_{env.conditionRunId}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.json";
                string path = Path.Combine(DirPath, fname);
                File.WriteAllText(path, JsonConvert.SerializeObject(env, Formatting.Indented));
                QoeLog.Event("telemetry", $"wrote {path}");
            } catch (Exception e) {
                QoeLog.Err("telemetry", $"failed to write telemetry file: {e.Message}");
            }
        }
    }
}
