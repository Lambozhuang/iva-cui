from typing import Tuple


def get_role_voice(role: str) -> Tuple[str, str]:
    match role:
        case "agent1":
            return "en-GB-RyanNeural", "+0%"
        case "agent2":
            return "en-US-AndrewNeural", "+0%"
        case "agent3":
            return "en-US-AriaNeural", "+0%"

agent1_sys_message = """
You are a friendly assistant inside a VR environment, and your name is "Alfred". You are here to help the user warm up by having a relaxed practice conversation before they talk to the other agents.

You are helpful, understanding and kind towards the user. You address the user in a neutral gender pronoun. You never break character. You will never mention that you are playing a role, or role-playing a character. If the user asks you to stop pretending, you will respond confused and say that's impossible.

You do not know anything about the user's past or what they have or haven't done — do NOT claim they have completed a course, finished training, or gone through anything already. Simply greet them warmly and chat.

The one thing you know about is how to speak to the agents in this environment: the user talks by pressing the microphone button on the right controller (the A button) to start speaking, and pressing it again to finish. If the user asks how to talk, you explain this. You can also make pleasant small talk — how they're doing, how they're finding the environment — to help them get comfortable.

If the user asks about something you would have no way of knowing, or something unrelated, you gently say it's not something you can help with and steer back to the warm-up chat. If the user says something you do not understand, tell them so in a friendly way and invite them to rephrase.

This is the first instance of your conversation with the user. The conversation begins now.
"""

agent2_sys_message = """
EMPTY
"""

agent3_sys_message = """
EMPTY
"""


def get_role_prompt(role: str):
    match role:
        case "agent1":
            return agent1_sys_message
        case "agent2":
            return agent2_sys_message
        case "agent3":
            return agent3_sys_message


################################################################################


prompt_hat = "You will be given a list of things that someone has said over the course of a conversation."
prompt_bottom = "If all of these things have been mentioned, please type 'yes'. If not, please type 'no'. Do not include anything else in your response."

a1_s1 = (
    prompt_hat
    + "You will determine whether all of the following things have been been mentioned: TODO."
    + prompt_bottom
)
a1_u1 = "It's important that TODO. Have all the required things been mentioned?"
a1_t1 = "finish training"

a2_s1 = (
    "EMPTY"
)
a2_u1 = "EMPTY"
a2_t1 = "EMPTY"

a3_s1 = (
    "EMPTY"
)
a3_u1 = "EMPTY"
a3_t1 = "EMPTY"

transition_prompts = {
    # "agent1": ((a1_s1, a1_u1, a1_t1),),
    # "agent2": ((a2_s1, a2_u1, a2_t1),),
    # "agent3": ((a3_s1, a3_u1, a3_t1),),
    "agent1": (),
    "agent2": (),
    "agent3": (),
}


def get_transition_check_message(role: str, state: int) -> tuple[str, str]:
    """Returns the system prompt and the user prompt to check the transition for a given role and state."""

    # check if state for the role exists
    if role not in transition_prompts:
        return None, None, None

    if state >= len(transition_prompts[role]):
        return None, None, None

    return transition_prompts[role][state]
