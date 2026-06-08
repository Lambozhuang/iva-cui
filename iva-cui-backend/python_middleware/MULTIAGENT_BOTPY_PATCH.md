# bot.py patch — per-agent persona + voice (apply on the Mac)

`agents_config.py` (next to this file) is ready and verified — it builds the
`AGENTS` registry (t0–t9 → prompt + Kokoro voice) from the ported prompts. It
imports only the four dependency-free `transition_prompts_*.py` modules, so copy
these alongside the Mac `bot.py`:

```
agents_config.py
transition_prompts_Hotel.py
transition_prompts_Museum.py
transition_prompts_Shirts.py
transition_prompts_Training.py
```

Then apply these four edits to the Mac `bot.py`. (Line numbers are approximate —
match by context. The shape mirrors the `voice` handling we already added.)

### 1. Import (top of bot.py, near the other imports)
```python
from agents_config import AGENTS, DEFAULT_AGENT
```

### 2. /api/offer handler — read agent_id, derive voice from it (after the existing `voice = ...` parse)
```python
agent_id = request.get("agent_id", DEFAULT_AGENT)
if agent_id not in AGENTS:
    agent_id = DEFAULT_AGENT
# Per-agent default voice unless Unity sent an explicit non-empty override.
if not request.get("voice"):
    voice = AGENTS[agent_id]["voice"]
```

### 3. Launch run_bot with agent_id
```python
# was: background_tasks.add_task(run_bot, pipecat_connection, voice)
background_tasks.add_task(run_bot, pipecat_connection, voice, agent_id)
```

### 4. run_bot — accept agent_id and use its prompt as the system instruction
```python
# signature:
async def run_bot(webrtc_connection, voice: str = DEFAULT_VOICE, agent_id: str = DEFAULT_AGENT):
    ...
    # where the context is built — swap ONLY the content string, keep the role
    # (whatever role your bot.py uses today, "user" or "system"):
    system_prompt = AGENTS.get(agent_id, AGENTS[DEFAULT_AGENT])["prompt"]
    context = OpenAILLMContext([{ "role": "user", "content": system_prompt }])
```

### Behaviour
- Unity sends `agent_id` (t0–t9) in the offer → bot serves that persona + its
  default Kokoro voice.
- Unknown / missing `agent_id` → falls back to `DEFAULT_AGENT` (t4, Hotel
  receptionist — the proven one).
- Explicit non-empty `voice` in the offer still wins (testing override); empty →
  the agent's default voice.

### Test (no Unity needed)
```bash
# t6 (Hotel waiter) — expect am_puck + waiter persona in the bot log:
curl -s -X POST http://localhost:7860/api/offer \
  -H 'Content-Type: application/json' \
  -d '{"sdp":"...","type":"offer","agent_id":"t6"}'   # (real SDP needed for a full connect)
```
Easier: just run the study from Unity (Step 2/3) and watch the bot log print the
selected agent_id + voice per connection.
