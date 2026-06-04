import importlib
from typing import Tuple

from llm_backends import (
    OpenAIClient_llama3,
    OpenAIClient_gpt4o,
    OpenAIClient_gpt4o_mini,
    OllamaClient,
)

import re


# QoE thesis: cap the spoken reply length so the LLM-generation + TTS-synthesis
# time (both scale with word count) stay comparable across all agents. Without
# this, agents told to "explain" (e.g. the museum volunteers) emit long replies
# that cost far more on both stages, adding variance on top of the netem delay
# we're actually measuring. The per-prompt "at most two short sentences" leash
# is the primary control; this is the hard ceiling that stops a runaway turn.
# ~110 tokens ≈ 80 words ≈ 3-4 short sentences, so a well-behaved 2-sentence
# reply finishes naturally and is never truncated mid-word.
SPEAK_MAX_TOKENS = 110


# QoE thesis: appended to EVERY agent's system prompt (all scenes) so the length
# leash and the free-form, non-linear framing are identical across all 10 agents.
# Centralising it here — rather than trusting each hand-written prompt — is what
# guarantees the conversational style is a controlled constant and not another
# source of between-agent variance. Two jobs:
#   1. Length: cap every reply at ~two short sentences so LLM+TTS time (and the
#      audio clip the device must download) stay comparable agent-to-agent.
#   2. Non-linear: these are standalone, open-ended chats — the agent must NOT
#      run a script or steer toward completing a task / handing over an item /
#      sending the user to another character. It just converses in character for
#      as long as the user keeps talking.
SHARED_STYLE = """

--- HOW YOU CONVERSE (most important) ---
This is a casual, open-ended, standalone conversation. There is no task to finish, no checklist, and no next person to send the user to. Do not follow a script or try to move the conversation toward any goal or conclusion. Simply stay in character and respond naturally to whatever the user says, for as long as they wish to talk.
Always reply in AT MOST two short sentences. Never give long explanations, monologues, or lists; if the user asks for more detail, give a little more in your next short reply rather than one long answer.
You only say things a real person would say out loud. Never describe actions, gestures, or emotions, and never use text between asterisks or parentheses.
"""


import_cache = {}


def dynamic_import(module_name, function_names):
    if module_name in import_cache:
        module = import_cache[module_name]
    else:
        module = importlib.import_module(module_name)
        import_cache[module_name] = module

    functions = {}
    for func_name in function_names:
        if hasattr(module, func_name):
            functions[func_name] = getattr(module, func_name)
        else:
            raise ImportError(f"Function {func_name} not found in module {module_name}")

    return functions


def remove_text_between_symbols(input_string):
    # Remove text between **
    modified_string = re.sub(r"\*[^*]*\*", "", input_string)
    # Remove text between ()
    modified_string = re.sub(r"\([^)]*\)", "", modified_string)
    # Remove double spaces
    modified_string = modified_string.replace("  ", " ")
    return modified_string


class Agent:
    def __init__(
        self,
        role: str,
        client,
        get_role_prompt,
        get_transition_check_message,
        voice: str,
    ):
        self.role: str = role
        self.state: int = 0
        self.client: OpenAIClient_llama3 = client
        self.messages: list = []
        self.get_role_prompt = get_role_prompt
        self.get_transition_check_message = get_transition_check_message
        self.voice: Tuple[str, str] = voice  # (voice, rate)

        # Append the shared QoE style/length/non-linear rules to whatever the
        # per-scene prompt file provides, so every agent gets them uniformly.
        self.set_system_message(get_role_prompt(role) + SHARED_STYLE)

    def set_system_message(self, message: str) -> None:
        self.messages.append({"role": "system", "content": message})

    def add_user_message(self, user_input: str) -> None:
        self.messages.append({"role": "user", "content": user_input})

    def generate_response(self) -> str:
        response_text = self.client.chat(
            messages=self.messages, max_tokens=SPEAK_MAX_TOKENS
        )
        response_text = remove_text_between_symbols(response_text)

        # handle generated response being empty
        if len(response_text) < 2:
            response_text = "I'm sorry, but I'm having issues understanding you. Could you please repeat that?"

        self.messages.append({"role": "assistant", "content": response_text})
        return response_text, ""

    def check_for_transition(self):
        sys_prompt, user_prompt, next_task_str = self.get_transition_check_message(
            self.role, self.state
        )

        if sys_prompt is None:
            return

        print(f">> Checking transition for {self.role} in state {self.state}")

        flat_messages = "\n".join(
            [msg["content"] for msg in self.messages if msg["role"] == "assistant"]
        )
        flat_messages = (
            "These are the character's messages:\n\n"
            + flat_messages
            + "\n\nThe above were the messages. Answer the following:\n"
            + user_prompt
        )

        msgs = [
            {"role": "system", "content": sys_prompt},
            {"role": "user", "content": flat_messages},
        ]

        response = self.client.chat(messages=msgs, temperature=0.0, max_tokens=3)

        print(f">> Transition on {self.role} from state {self.state}: {response}")

        if response == "yes":
            self.state += 1
            return next_task_str

        return "none"


class ConversationHandler:
    def __init__(self, scene: str, client_name: str):
        client_classes = {
            "llamafile_llama3": OpenAIClient_llama3,
            "openai_4": OpenAIClient_gpt4o,
            "openai_4mini": OpenAIClient_gpt4o_mini,
            "ollama": OllamaClient,
        }
        if client_name not in client_classes:
            raise ValueError(f"Unknown client name: {client_name}")
        self.client = client_classes[client_name]()

        transition_module = f"transition_prompts_{scene}"
        functions = dynamic_import(
            transition_module,
            ["get_role_voice", "get_role_prompt", "get_transition_check_message"],
        )

        self.agents = {
            "agent1": Agent(
                "agent1",
                self.client,
                functions["get_role_prompt"],
                functions["get_transition_check_message"],
                functions["get_role_voice"]("agent1"),
            ),
            "agent2": Agent(
                "agent2",
                self.client,
                functions["get_role_prompt"],
                functions["get_transition_check_message"],
                functions["get_role_voice"]("agent2"),
            ),
            "agent3": Agent(
                "agent3",
                self.client,
                functions["get_role_prompt"],
                functions["get_transition_check_message"],
                functions["get_role_voice"]("agent3"),
            ),
        }

    def process_user_message(self, role: str, user_input: str) -> list[str, str]:
        agent: Agent = self.agents[role]
        agent.add_user_message(user_input)
        response = agent.generate_response()
        return response

    def check_for_state_transition(self, role: str) -> str:
        agent: Agent = self.agents[role]
        transition = agent.check_for_transition()
        return transition

    def get_role_voice(self, role: str) -> str:
        return self.agents[role].voice

    def get_agent_history_debug(self, role: str) -> list:
        agent: Agent = self.agents[role]

        debug_history = ["==========================\n"]
        for msg in agent.messages:
            if msg["role"] == "system":
                debug_history.append("SYSTEM Message: ... (see role file)\n")
            elif msg["role"] == "user":
                debug_history.append(f"USER: {msg['content']}\n")
            elif msg["role"] == "assistant":
                debug_history.append(f"{role}: {msg['content']}\n")
        debug_history.append("==========================\n")

        return "".join(debug_history)
