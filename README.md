# QoE Lab Modified IVA-CUI

> **Orientation for future readers (humans & AI agents) — read this first.**
>
> **What this fork is.** This is a *modified* fork of the CUI '25 IVA-CUI project (the original README is preserved further down, under the `# IVA-CUI` heading). It is being adapted for a **master's thesis on Quality of Experience (QoE) in XR** — specifically, how network impairment (latency, jitter, bandwidth, packet loss) affects a free-form voice conversation with an LLM-powered virtual agent. The upstream project was a linear, walking VR quest; this fork is being reshaped into a controlled QoE experiment.
>
> **The big architectural shift.** In this fork the Unity app is the **"XR device"** in a separate experiment-orchestration framework called **QoE Lab** (an Electron operator console; lives in WSL at `~/sources/qoe-lab`, *not* in this repo). QoE Lab drives each trial over a WebSocket: it sends `start_task`, the device teleports the player in front of one agent for a timed conversation, then the device reports back and the network impairment (netem, applied on the PC) changes per condition. The device-side client is **`Assets/Scripts/QoE/QoeDeviceClient.cs`**; the wire contract it implements is documented in `~/sources/qoe-lab/CONTRACT.md` (XR device section). `NETEM_TRAFFIC.md` in this repo describes what actually crosses the shaped link.
>
> **What changed from upstream (so the noise makes sense).**
> - The original 3 walking scenes (City/Hotel/Museum) + Training are being collapsed into **standalone, one-off short conversations** — the subject is *teleported* in front of each agent (no walking, no quest progression), talks for the trial duration, then is teleported to a neutral point.
> - Right now **Training + Hotel are merged into one scene, `Assets/Scenes/QoE_Shell.unity`**, with spawn points in front of each agent. City/Museum aren't merged in yet. Task switching = teleport the XR rig, *not* scene loading.
> - All shared controllers (mic, server I/O, study state, conversation logging, agent selection) are consolidated onto **one root `Scene Control` GameObject**. Much of the upstream per-scene/quest machinery (`StudyControls` counterbalancing, `PathRenderer`, inventory, `HotelSceneController` task transitions) is **dormant or only partly rewired** — don't assume a given upstream system is still on the critical path; verify against `QoeDeviceClient` + `Scene Control` first.
> - The Python backend (`iva-cui-backend/`) is mostly unchanged and still useful: it keeps **one global conversation handler** keyed by scene (`/refresh/{scene}/` rebuilds it and wipes history), and serves ASR (`:8083`), LLM/TTS speak (`:8000`). Each teleport refreshes the right scene so the agent's prompt + voice match.
>
> **Standing constraints for this fork.** Keep changes minimal and avoid over-engineering — it's a thesis prototype, not production. Editor/scene work is often done by the user (Claude edits code; scene mutations via Unity MCP are done with care and left unsaved for review). The conversations are meant to be **self-contained one-offs**, not a connected narrative.
>
> The lists below track outstanding work and what's already been done in this adaptation.

TODOs
- [x] IMPORTANT: Remove the artificial delay (done — `AgentSelectionController` + `TrainingSceneController` now play responses immediately; only real network delay is present). *Still open:* "use the same filler for all agents" — filler clips are still per-agent (`agent1/2/3AudioClips`), and filler only plays under `WaitIndicatorType.Natural/Artificial` which the QoE study leaves at `None`.
- [x] IMPORTANT: Add museum and city scenes (City + Museum roots are merged into `QoE_Shell`; `kTaskBackendScenes`/`taskSpawnPoints` extended to all 10 task indices).
- [ ] Depending on the task, we should determine if we end the condition based on if the task is finished or not, or if we let the timer run out, so basically check if we can reuse the original conversation end logic to check if the task is finished. *(Note: for one-off conversations the transition/"task finished" signal is now ignored — `StudyControls.oneOffConversations`. Re-enabling task-finished detection would mean re-wiring the per-scene transition handlers.)*
- [ ] Replace the mic indicator icon from Rec to mic
- [ ] Figure out how to present a pre-convo prompt for the test subject for each agent to have a bit of context. and we should probably pause? or disable convo before they read the context and press a button to start.

### QoE adaptation — deferred (do in separate commits)

**Blockers / correctness**
- [x] **`QoE_Shell` is in Build Settings as scene 0** (verified via MCP: active scene `QoE_Shell`, buildIndex 0).
- [x] **Stuck-mic on network failure.** Both pipelines now self-recover: `TrainingSceneController` already had a 30s `ThinkingTimeout`; added a matching timeout to `StudyControls` (zone pipeline) that resets `someoneIsThinking` + the agent's thinking indicator if no response arrives.

**Routing / multi-scene (needed before all 9 conversations work)**
- [x] **Scene-name routing no longer crashes on City/Museum.** `StudyControls.DetermineUserStudySceneName()` still can't distinguish the merged scenes (falls back to Hotel), but the `NotImplementedException` path is now dead: `StudyControls.oneOffConversations` makes `StudyTasks.HandleLLMDeterminedTask` ignore the LLM transition entirely, so `HotelSceneController.HandleLLMDeterminedTask` is never reached with a foreign transition string. Backend scene/prompt/voice routing is correct via `kTaskBackendScenes` + the per-agent proximity `ActivationZone`.
- [x] **Extended `kTaskBackendScenes` / `taskSpawnPoints` to all 10 task indices** (Training + 3 City + 3 Hotel + 3 Museum). `task_number` routes by index; City uses backend scene name `Shirts`.

**Prompts / conversation design (one-off conversations)**
- [ ] **Rewrite the agent system prompts for self-contained, one-off conversations** instead of a continuous linear quest. Each agent visit should stand alone with no dependency on prior agents/tasks. Prompt files: `iva-cui-backend/python_middleware/transition_prompts_<scene>.py`. (Backend `/refresh/{scene}/` already wipes all conversation history and reseeds the system prompt — see `app.py:125` / `conversation_handler.py`, so each refresh = a clean conversation. This is the desired behavior; the prompts just need to match the one-off framing and drop the quest-transition logic.)
- [ ] Decide whether per-agent refresh is needed. Currently `/refresh/Hotel/` rebuilds ALL three Hotel agents (resets the other two as well as the one being visited). Fine for one-off conversations, but means you can't teleport away and back mid-conversation without losing it.

**On-device (Quest) logging**
- [x] **Logs moved to `Application.persistentDataPath`** (`SceneProfiling`, `ConversationLogger`, `CollectInVRSurvey`) so on-device `File.Write/Append` won't throw.

**Polish**
- [ ] Wire `controllerMicButton` on `TrainingSceneController` (currently NULL → Training mic responds to the M key only, not the Quest trigger).
- [ ] Disable rig gravity / `CharacterController` around teleport so the player can't drift/fall after a teleport before pressing R.

### QoE adaptation — done
- **All 9 agents + Training reachable in one scene.** City + Museum roots merged into `QoE_Shell` alongside Training + Hotel; `QoeDeviceClient.taskSpawnPoints`/`kTaskBackendScenes` cover task indices 0–9 (Training, City friend/clerk/manager → backend `Shirts`, Hotel receptionist/maintenance/waiter, Museum host/volunteer1/volunteer2). **Editor step:** assign the 10 spawn points (each scene's `Agents/<role>/SpawnPoint`) to `taskSpawnPoints` on `QoeDeviceClient` in order.
- **One-off conversations.** `StudyControls.oneOffConversations` (default true) disables all quest progression (path arrows, inventory, surveys, LLM transition handling, initial task seeding). The old quest/survey UI scripts are now null-safe / early-out so the task UI canvas, hand-tracked UI, inventory, and in-world surveys can be deleted from `QoE_Shell` without runtime NREs.
- **No artificial response delay** — only real network delay is measured (the CUI'25 synthetic delay distribution is removed from both pipelines).
- **Training agent uses the configured backend host** instead of hardcoded `127.0.0.1:8000` (`ServerInterface.MiddlewareBaseUrl`), so it works on the netem split-machine topology.
- **Optional scene-root culling for performance** (`QoeDeviceClient.sceneRoots`): when assigned, each teleport keeps only the target scene root active and disables the other three (~70M tris → one scene's worth). No-op until assigned. See `PERFORMANCE.md` for the full optimization checklist (also: drop Quality from Ultra→Medium/Low).
- Merged Training + Hotel into a single `QoE_Shell` scene with 4 spawn points; task switching = teleport the XR rig (`QoeDeviceClient.TeleportToTask`), no scene loading on the critical path.
- Consolidated all shared controllers onto one root `Scene Control` GameObject (mic, server, study, logger, agent selection); removed the per-scene duplicates.
- Training mic gated by proximity to the robot head; `StudyControls` mirror-guards so the two mic pipelines don't double-fire.
- `ApplyVRSettings` only forces the Quest mic when that device is present (keeps the Inspector-selected mic on PC/Simulator).
- Replaced `ActivationZone` trigger colliders with proximity-based activation (`AgentSelectionController` polls camera distance) so teleporting in front of an agent activates its zone.
- Fixed agent voice/prompt routing: teleporting now refreshes the correct backend scene (`QoeDeviceClient.TeleportToTask` → `ServerInterface.RefreshScene`), instead of `TrainingSceneController` force-refreshing Training at startup and winning the race. Each teleport resets that scene's conversation fresh (one-off behavior).
- Split `ServerInterface` connection into separate host / port / whisper-port Inspector fields (whisper reuses the host IP).
- Reworked the real WS path to teleport instead of additively loading `Hotel_Scene`: `OnStartTask` parses `task_number` (null→Training index 0, N→index N per CONTRACT.md), and `Ready` (`SendReadyManual`→`TeleportThenStart`) teleports to that spawn + refreshes the backend scene, then sends `ready`. Removed the obsolete additive scene-load machinery (`taskSceneName`, `shellRig`, `LoadTaskScene`/`UnloadTaskScene`, the Debug-task scene toggle). Debug teleport buttons (Training/Task 1–3) retained.


# IVA-CUI

This repository contains the Python and Unity code for a [paper](https://doi.org/10.1145/3719160.3736636) titled
"**Mitigating Response Delays in Free-Form Conversations with LLM-powered Intelligent Virtual Agents**" to appear in the Proceedings of the 7th ACM Conference on Conversational User Interfaces [(CUI '25)](https://cui.acm.org/2025/). If you use this code or Unity environments in your research, please cite our paper (see [Citation](#citation) section below).

## Table of Contents

- [Unity Setup](#unity-setup)
  - [User study scenes](#user-study-scenes)
  - [How to run the scenes](#how-to-run-the-scenes)
  - [Desktop mode](#desktop-mode)
  - [VR mode](#vr-mode)
  - [Controls](#controls)
- [Python Setup](#python-setup)
  - [Setup Steps](#setup-steps)
    - [Running LLM locally on Windows](#running-llm-locally-on-windows)
    - [Running Python middleware on Windows](#running-python-backend-middleware-on-windows)
    - [Running the ASR model on WSL](#running-the-asr-model-on-wsl)
- [Citation](#citation)  

## Unity Setup

### User study scenes

All scenes are located in [iva-cui-unity/Assets/Scenes/](iva-cui-unity/Assets/Scenes/). List of licenses for third-party code and assets used in this project can be found in the [ASSET_LICENSES.md](iva-cui-unity/ASSET_LICENSES.md) file.

- `City_Scene.unity` -> Scenario 1
- `Hotel_Scene.unity` -> Scenario 2
- `Museum_Scene.unity` -> Scenario 3

### How to run the scenes

- Unity version: 2022.3.76f1
- Run [Python backend](#python-setup) before running the Unity scenes.
- VR and Desktop (non-VR) modes are supported. Follow instructions in [Desktop Mode](#desktop-mode) and [VR Mode](#vr-mode).
- To speak with agents, **toggle mic on before** and **toggle mic off after** you speak (see [Controls](#controls)). Adjust microphone on the `SceneControls` gameobject in scene hierarchy (see screenshot below, [Desktop Mode](#desktop-mode) and [VR Mode](#vr-mode)).
- Agents will respond after a short delay. If no agent can hear you or an agent is currently *thinking* or *speaking*, you will hear a **broken mic** sound.  
![mic setup](setup.png)

### Desktop mode

1. Enable `WASD Player` gameobject in hierarchy
2. Disable `XR Interaction Setup` gameobject in hierarchy
3. On the `SceneControls` gameobject, set a working microphone

### VR mode

1. Enable `XR Interaction Setup` gameobject in hierarchy
2. Disable `WASD Player` gameobject in hierarchy
3. On the `SceneControls` gameobject, set microphone to `Oculus Virtual Audio Device` (or other device equivalent)

### Controls

| **Action**            | **VR Mode**     | **Desktop Mode** |
| --------------------- | ------------------- | -------------------- |
| Toggle microphone     | A                   | M                    |
| Move                  | Left Stick          | WASD                 |
| Look around           | Right Stick         | Mouse                |
| Sprint                | –                   | Left Shift           |
| Interact with objects | Side Trigger (Grab) | –                    |

## Python Setup

Backend (we also call it 'middleware') is responsible for handling requests from Unity, processing audio files, and interacting with the LLM server. It is located in the [iva-cui-backend](iva-cui-backend/) directory.

### Setup Steps

The outcome from following these instructions should be:

- A local LLM server running on port `8082` (or `11434` for Ollama)
- A local ASR server running on port `8083`
- A local Python middleware server running on port `8000`

#### Running LLM locally on Windows

By default, backend runs using [Ollama](https://ollama.com/download/windows). We recommend using it, however, OpenAI API-style LLM server endpoints and locally-deployed options ([llamafile](https://github.com/Mozilla-Ocho/llamafile/releases) and [LMStudio](https://lmstudio.ai/)) are also supported. LLM API endpoints are specified in [iva-cui-backend/python_middleware/llm_backends.py](iva-cui-backend/python_middleware/llm_backends.py). If you want to switch to OpenAI-style endpoints, you can do so by changing the `LLM_BACKEND` variable in [iva-cui-backend/python_middleware/app.py](iva-cui-backend/python_middleware/app.py).

##### Ollama (local, recommended)

1. Download and install [Ollama](https://ollama.com/download/windows).
2. Run `ollama run llama3.1:8b-instruct-q5_K_M`.
3. Set the `LLM_BACKEND` variable in [iva-cui-backend/python_middleware/app.py](iva-cui-backend/python_middleware/app.py) to `ollama`.

##### LMStudio (local, OpenAI-style endpoints)

1. Download, install and run [LMStudio](https://lmstudio.ai/).
2. Download this model `lmstudio-community/Meta-Llama-3.1-8B-Instruct-GGUF/Meta-Llama-3.1-8B-Instruct-Q5_K_M.gguf`.
3. Set the UI mode to "Developer" or "Power User" (bottom left corner).
4. Go to "Developer" tab -> Settings -> Server Port and set it to `8082`.
5. Start the server by toggling the switch in the top left corner.
6. Set the `LLM_BACKEND` variable in [iva-cui-backend/python_middleware/app.py](iva-cui-backend/python_middleware/app.py) to `llamafile_llama3`.

##### llamafile (local, OpenAI-style endpoints)

1. Download [llamafile-0.9.0](https://github.com/Mozilla-Ocho/llamafile/releases/tag/0.9.0)
2. Rename `llamafile-0.9.0` to `llamafile-0.9.0.exe`
3. Download `Meta-Llama-3.1-8B-Instruct-Q5_K_M.gguf` from [huggingface](https://huggingface.co/bullerwins/Meta-Llama-3.1-8B-Instruct-GGUF/tree/828492ca0d7e7efd4b316e75af8d9cd582fdec34)
4. Run `llamafile-0.9.0.exe --server -ngl 9999 -m Meta-Llama-3.1-8B-Instruct-Q5_K_M.gguf --host 0.0.0.0 --port 8082`
5. Set the `LLM_BACKEND` variable in [iva-cui-backend/python_middleware/app.py](iva-cui-backend/python_middleware/app.py) to `llamafile_llama3`

##### Official OpenAI API (remote)

1. Create a file mentioned in the `load_openai_key()` function in [iva-cui-backend/python_middleware/llm_backends.py](iva-cui-backend/python_middleware/llm_backends.py) and put your OpenAI API key there. The file should contain only the key, no other text. Alternatively, modify that function to load the key from an environment variable. You can also make the function directly return the key in the code (not recommended).
2. Set the `LLM_BACKEND` variable in [iva-cui-backend/python_middleware/app.py](iva-cui-backend/python_middleware/app.py) to `openai_4` or `openai_4mini`. You can also use other models by directly setting the `model="gpt-4o"` in an appropriate class in the [iva-cui-backend/python_middleware/llm_backends.py](iva-cui-backend/python_middleware/llm_backends.py) file.

#### Running Python backend (middleware) on Windows

```bash
# create and activate virtual environment
python -m venv venv
venv\Scripts\activate

# install the required packages
pip install openai ollama edge-tts FastAPI[all]

# navigate to the directory and run the server
cd iva-cui-backend\python_middleware
uvicorn app:app --reload
```

#### Running the ASR model on WSL

```bash
# create a virtual environment
sudo apt update
sudo apt install python3-venv
python3 -m venv venv

# activate the virtual environment
source venv/bin/activate

# install the required packages
pip install nvidia-cublas-cu12 nvidia-cudnn-cu12==9.*

export LD_LIBRARY_PATH=`python -c 'import os; import nvidia.cublas.lib; import nvidia.cudnn.lib; print(os.path.dirname(nvidia.cublas.lib.__file__) + ":" + os.path.dirname(nvidia.cudnn.lib.__file__))'`

pip install faster_whisper FastAPI[all]

# navigate to directory and run the ASR server
cd iva-cui-backend\transcription_server
python whisper_server.py
```

### Test LLM Locally

```bash
cd iva-cui-backend\python_middleware
python test_conv.py
```

## Authors

[Mykola Maslych](https://github.com/maslychm), [Mohammadreza Katebi](https://github.com/MRkatebi99), [Christopher Lee](https://github.com/hpipyT), [Yahya Hmaiti](https://github.com/YHmaiti), [Amirpouya Ghasemaghaei](https://github.com/PouyaAghaei), [Christian Pumarada](https://github.com/Aurelius1824), [Janneese Palmer](https://github.com/janneese), [Esteban Segarra Martinez](https://overcodedstack.github.io/), [Marco Emporio](https://marcokero.github.io/), [Warren Snipes](https://github.com/LockedThread), [Ryan P. McMahan](https://orcid.org/0000-0001-9357-9696), [Joseph J. LaViola Jr.](https://orcid.org/0000-0003-1186-4130)

## Citation

If you use this code in your research, please cite our paper:

```bibtex
@inproceedings{Maslych2025Mitigating,
    author    = {Maslych, Mykola and Katebi, Mohammadreza and Lee, Christopher and Hmaiti, Yahya and Ghasemaghaei, Amirpouya and Pumarada, Christian and Palmer, Janneese and Segarra Martinez, Esteban and Emporio, Marco and Snipes, Warren and McMahan, Ryan P. and LaViola Jr., Joseph J.},
    title     = {Mitigating Response Delays in Free-Form Conversations with LLM-powered Intelligent Virtual Agents},
    year      = {2025},
    isbn      = {9798400715273},
    publisher = {Association for Computing Machinery},
    address   = {New York, NY, USA},
    url       = {https://doi.org/10.1145/3719160.3736636},
    doi       = {10.1145/3719160.3736636},
    booktitle = {Proceedings of the 7th ACM Conference on Conversational User Interfaces},
    articleno = {49},
    numpages  = {15},
    month     = {jul},
    series    = {CUI '25},
    location  = {Waterloo, ON, Canada},
}
```
