@echo off
rem UniversalCaptions launcher (packaged install).
rem Sets the offline runtime/model knobs ONLY for the UniversalCaptions process, then starts the
rem app. Nothing here requires elevation or modifies global/user environment variables. All paths
rem are relative to the install root (%~dp0), so the bundle works on a clean machine with no repo.
setlocal

set "UC_FW_PYTHON=%~dp0py\python.exe"
set "UC_ARGOS_PYTHON=%~dp0py\python.exe"
set "UC_FW_MODEL=%~dp0models\faster-whisper-small"
set "UC_STT_MODEL_PATH=%~dp0models\ggml-base.bin"
set "ARGOS_PACKAGES_DIR=%~dp0argos-packages"
set "HF_HOME=%~dp0models\hf"
set "HF_HUB_OFFLINE=1"
set "TRANSFORMERS_OFFLINE=1"
rem Never write __pycache__ bytecode: keeps the installed tree static so uninstall removes
rem everything Inno installed (Python stdlib .pyc files would otherwise be left behind).
set "PYTHONDONTWRITEBYTECODE=1"

start "" "%~dp0UniversalCaptions.App.exe"
endlocal
