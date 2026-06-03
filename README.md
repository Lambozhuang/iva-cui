# QoE Lab Modified IVA-CUI

TODOs
- [ ] Replace the mic indicator icon from Rec to mic
- [ ] Figure out how to present a pre-convo prompt for the test subject for each agent to have a bit of context. and we should probably pause? or disable convo before they read the context and press a button to start.

### QoE adaptation — deferred (do in separate commits)

**Blockers / correctness**
- [ ] **`QoeDeviceClient` Ready/Debug-task scene-load path contradicts the new single-scene teleport model.** `taskSceneName` is still `Hotel_Scene`, so pressing **Ready** additively loads a second copy of the Hotel scene on top of `QoE_Shell` → duplicate XR rig, camera, AudioListener, and duplicate singletons (`ServerInterface`/`StudyControls`/`AgentSelectionController`) that overwrite the originals. Fix: clear `taskSceneName` and/or disable the additive load path so only teleport is reachable.
- [ ] **Add `QoE_Shell` to Build Settings (as scene 0).** Currently only `Hotel_Scene` is in the build list, so a Quest build would boot into the wrong scene. (In-editor "play from open scene" is unaffected.)
- [ ] **Stuck-mic on network failure.** `someoneIsThinking` (Hotel, `StudyControls`) and `isThinking` (Training, `TrainingSceneController`) are only reset on the success path. Any ASR/TTS/download failure leaves the mic permanently blocked with no timeout — high relevance under netem loss/delay. Add a timeout/error reset.

**Routing / multi-scene (needed before all 9 conversations work)**
- [ ] **Scene-name routing is hardwired to Hotel.** With everything in one `QoE_Shell` scene, `StudyControls.DetermineUserStudySceneName()` can't distinguish City/Hotel/Museum and falls back to the serialized `userStudyScene` (Hotel). Needs `task_number`-driven role/scene selection. `HotelSceneController.HandleLLMDeterminedTask` throws `NotImplementedException` if a non-Hotel transition string arrives.
- [ ] **Parse `task_number` in `QoeDeviceClient.OnStartTask`** and map it to the correct teleport target + agent role.

**Prompts / conversation design (one-off conversations)**
- [ ] **Rewrite the agent system prompts for self-contained, one-off conversations** instead of a continuous linear quest. Each agent visit should stand alone with no dependency on prior agents/tasks. Prompt files: `iva-cui-backend/python_middleware/transition_prompts_<scene>.py`. (Backend `/refresh/{scene}/` already wipes all conversation history and reseeds the system prompt — see `app.py:125` / `conversation_handler.py`, so each refresh = a clean conversation. This is the desired behavior; the prompts just need to match the one-off framing and drop the quest-transition logic.)
- [ ] Decide whether per-agent refresh is needed. Currently `/refresh/Hotel/` rebuilds ALL three Hotel agents (resets the other two as well as the one being visited). Fine for one-off conversations, but means you can't teleport away and back mid-conversation without losing it.

**On-device (Quest) logging**
- [ ] **`SceneProfiling` / `ConversationLogger` write to `streamingAssetsPath`**, which is read-only inside the APK on Android — `File.Write/Append` will throw on-device. Move logs to `Application.persistentDataPath` for Quest builds.

**Polish**
- [ ] Wire `controllerMicButton` on `TrainingSceneController` (currently NULL → Training mic responds to the M key only, not the Quest trigger).
- [ ] Disable rig gravity / `CharacterController` around teleport so the player can't drift/fall after a teleport before pressing R.

### QoE adaptation — done
- Merged Training + Hotel into a single `QoE_Shell` scene with 4 spawn points; task switching = teleport the XR rig (`QoeDeviceClient.TeleportToTask`), no scene loading on the critical path.
- Consolidated all shared controllers onto one root `Scene Control` GameObject (mic, server, study, logger, agent selection); removed the per-scene duplicates.
- Training mic gated by proximity to the robot head; `StudyControls` mirror-guards so the two mic pipelines don't double-fire.
- `ApplyVRSettings` only forces the Quest mic when that device is present (keeps the Inspector-selected mic on PC/Simulator).
- Replaced `ActivationZone` trigger colliders with proximity-based activation (`AgentSelectionController` polls camera distance) so teleporting in front of an agent activates its zone.
- Fixed agent voice/prompt routing: teleporting now refreshes the correct backend scene (`QoeDeviceClient.TeleportToTask` → `ServerInterface.RefreshScene`), instead of `TrainingSceneController` force-refreshing Training at startup and winning the race. Each teleport resets that scene's conversation fresh (one-off behavior).
- Split `ServerInterface` connection into separate host / port / whisper-port Inspector fields (whisper reuses the host IP).


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
