#!/usr/bin/env bash
# Start the ASR (Whisper) server (port 8083)
# Requires: venv set up at project root with nvidia-cublas-cu12 nvidia-cudnn-cu12==9.* faster_whisper FastAPI[all]

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VENV_DIR="$SCRIPT_DIR/venv"
ASR_DIR="$SCRIPT_DIR/iva-cui-backend/transcription_server"

# Activate virtual environment
if [ ! -f "$VENV_DIR/bin/activate" ]; then
  echo "[ERROR] Virtual environment not found at $VENV_DIR"
  echo "Create it first:"
  echo "  sudo apt update && sudo apt install python3-venv"
  echo "  python3 -m venv venv"
  echo "  source venv/bin/activate"
  echo "  pip install nvidia-cublas-cu12 'nvidia-cudnn-cu12==9.*' faster_whisper 'FastAPI[all]'"
  exit 1
fi

source "$VENV_DIR/bin/activate"
echo "[INFO] Virtual environment activated: $VENV_DIR"

# Set LD_LIBRARY_PATH for nvidia CUDA/cuDNN libraries
# nvidia.cublas.lib and nvidia.cudnn.lib are namespace packages (__file__ is None),
# so we use importlib.util.find_spec to locate their on-disk directories.
export LD_LIBRARY_PATH=$(python -c \
  'import importlib.util; \
   cublas = importlib.util.find_spec("nvidia.cublas.lib").submodule_search_locations[0]; \
   cudnn  = importlib.util.find_spec("nvidia.cudnn.lib").submodule_search_locations[0]; \
   print(cublas + ":" + cudnn)')
echo "[INFO] LD_LIBRARY_PATH=$LD_LIBRARY_PATH"

cd "$ASR_DIR"
echo "[INFO] Starting ASR (Whisper) server ..."
python whisper_server.py
