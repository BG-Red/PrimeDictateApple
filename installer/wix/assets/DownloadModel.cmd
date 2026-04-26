@echo off
setlocal EnableExtensions
rem Downloads the default Whisper GGML model next to PrimeDictate (used by the online MSI).
set "APP=%~dp0"
set "MODEL=%APP%models\ggml-large-v3-turbo.bin"
if exist "%MODEL%" exit /b 0
mkdir "%APP%models" 2>nul
rem Omit --silent so progress is captured in the MSI log when the installer runs this (WixQuietExec).
"%SystemRoot%\System32\curl.exe" --fail -L --show-error --progress-bar -o "%MODEL%" "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin"
exit /b %ERRORLEVEL%
