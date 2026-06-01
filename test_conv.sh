#!/usr/bin/env bash
# Run the LLM conversation test script
# Requires: middleware venv set up with openai ollama edge-tts FastAPI[all]

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VENV_DIR="$SCRIPT_DIR/venv"
MIDDLEWARE_DIR="$SCRIPT_DIR/iva-cui-backend/python_middleware"

# Activate virtual environment
if [ ! -f "$VENV_DIR/bin/activate" ]; then
  echo "[ERROR] Virtual environment not found at $VENV_DIR"
  echo "Create it first:"
  echo "  python3 -m venv venv"
  echo "  source venv/bin/activate"
  echo "  pip install openai ollama edge-tts 'FastAPI[all]'"
  exit 1
fi

source "$VENV_DIR/bin/activate"
echo "[INFO] Virtual environment activated: $VENV_DIR"

cd "$MIDDLEWARE_DIR"
echo "[INFO] Running test_conv.py ..."
python test_conv.py
