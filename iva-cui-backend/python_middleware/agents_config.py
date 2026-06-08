# Multi-agent persona+voice registry for the Pipecat bot, keyed by agent_id.
#
# Ported (NOT rewritten) from the legacy transition_prompts_*.py role prompts +
# the SHARED_STYLE leash, so the Pipecat agents are identical in persona/style to
# the old middleware. agent_id matches Unity's task index:
#   t0 = Training, t1..t3 = City, t4..t6 = Hotel, t7..t9 = Museum
# (parallel to QoeDeviceClient.kTaskLabels / taskAgentAudioSources).
#
# USAGE on the Mac bot (bot.py):
#   from agents_config import AGENTS, DEFAULT_AGENT
#   agent_id = request.get("agent_id", DEFAULT_AGENT)
#   if agent_id not in AGENTS: agent_id = DEFAULT_AGENT
#   if not request.get("voice"): voice = AGENTS[agent_id]["voice"]   # agent default
#   ... run_bot(connection, voice, agent_id) ...
#   system_prompt = AGENTS[agent_id]["prompt"]   # inject as the system/context message
#
# This file imports the four legacy prompt modules (each is dependency-free, just
# `typing`), so they must sit alongside it (they're in this same python_middleware/
# dir and sync via the repo). SHARED_STYLE is inlined below (copied verbatim from
# conversation_handler.py) so we DON'T import conversation_handler, which would drag
# in the legacy llm_backends/HTTP-middleware deps the Pipecat bot doesn't need.

import transition_prompts_Hotel as _hotel
import transition_prompts_Museum as _museum
import transition_prompts_Shirts as _city        # the "City" scene module is named Shirts
import transition_prompts_Training as _training

# Verbatim from conversation_handler.py:36-52 (the QoE length / non-linear / <END>
# leash appended to every agent prompt by the old middleware).
SHARED_STYLE = """

--- HOW YOU CONVERSE (most important) ---
This is a casual, open-ended, standalone conversation. There is no task to finish, no checklist, and no next person to send the user to. Do not follow a script or try to move the conversation toward any goal or conclusion. Simply stay in character and respond naturally to whatever the user says, for as long as they wish to talk.
Always reply in AT MOST two short sentences. Never give long explanations, monologues, or lists; if the user asks for more detail, give a little more in your next short reply rather than one long answer.
You only say things a real person would say out loud. Never describe actions, gestures, or emotions, and never use text between asterisks or parentheses.

--- NEVER STALL OR LEAVE THE USER WAITING (critical) ---
You exist only in this spoken conversation. You cannot perform any action, look anything up, fetch anything, check a system, or step away — and there is no one else for you to consult. So NEVER say things like "just a moment", "let me check", "one second", "I'll look that up", "please hold", or "let me go get that": you would simply fall silent and the user would be left waiting, which must never happen. Every reply must be a complete conversational turn that hands the floor back to the user.
You may ask the user a question, including for a detail like a reservation number or confirmation code if it fits your role — but NEVER block the conversation waiting on it. Whatever the user gives you, accept it warmly and carry straight on; if they don't have it, wave it off as no problem and continue. Never refuse to proceed until you get a particular piece of information, and never go quiet.

--- KEEP THE CONVERSATION OPEN ---
After you help with something or answer a question, do NOT wrap things up or give a closing/farewell line. Keep the conversation going by warmly inviting more — e.g. "Is there anything else I can help you with?", "Anything else you'd like to know?", or a friendly follow-up question. Never say things like "enjoy your stay", "have a great day", or "take care" until the user themselves signals they are finished. Assume the user still has more to talk about unless they clearly say otherwise.

--- ENDING THE CONVERSATION ---
Only when the USER clearly signals they are finished — they say goodbye, "that's all", "I'm done", "nothing else", or similar — give a short, warm, in-character farewell (one sentence) and append the exact tag <END> to the very end of it. Only ever use the <END> tag on such a closing farewell, never in the middle of an ongoing conversation. Do not explain the tag or say the word "end"; just place <END> as the final characters of your closing message.
"""


def _p(mod, role):
    # role prompt + the shared style leash, exactly as the middleware built it
    # (conversation_handler.py:153: get_role_prompt(role) + SHARED_STYLE).
    return mod.get_role_prompt(role) + SHARED_STYLE


# agent_id -> {"prompt": <full system instruction>, "voice": <Kokoro voice id>}
# Voices: Kokoro (cached by prewarm.py). Mapped from the old edge-tts voices,
# gender-matched. Museum agent2 (German Florian) and agent3 (HK Yan) have no
# Kokoro equivalent, approximated with distinct UK voices.
AGENTS = {
    "t0": {"prompt": _p(_training, "agent1"), "voice": "bm_george"},   # Training (Alfred)        <- en-GB-Ryan
    "t1": {"prompt": _p(_city,     "agent1"), "voice": "af_aoede"},     # City friend             <- en-US-Ava
    "t2": {"prompt": _p(_city,     "agent2"), "voice": "am_michael"},   # City clerk              <- en-US-Andrew
    "t3": {"prompt": _p(_city,     "agent3"), "voice": "af_bella"},     # City manager            <- en-US-Aria
    "t4": {"prompt": _p(_hotel,    "agent1"), "voice": "af_heart"},     # Hotel receptionist      <- en-US-Michelle
    "t5": {"prompt": _p(_hotel,    "agent2"), "voice": "am_fenrir"},    # Hotel maintenance       <- en-US-Guy
    "t6": {"prompt": _p(_hotel,    "agent3"), "voice": "am_puck"},      # Hotel waiter            <- en-US-Brian
    "t7": {"prompt": _p(_museum,   "agent1"), "voice": "af_nicole"},    # Museum receptionist     <- en-US-Emma
    "t8": {"prompt": _p(_museum,   "agent2"), "voice": "bm_fable"},     # Museum volunteer 1      <- de-DE-Florian (approx)
    "t9": {"prompt": _p(_museum,   "agent3"), "voice": "bf_emma"},      # Museum volunteer 2      <- en-HK-Yan (approx)
}

DEFAULT_AGENT = "t4"  # hotel receptionist — the agent already proven end-to-end
