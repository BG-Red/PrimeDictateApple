@echo off
setlocal EnableExtensions
rem Downloads the default Whisper ONNX model next to PrimeDictate (used by the online MSI).
set "APP=%~dp0"
set "ROOT=%APP%models\whisper"
set "MODEL=%ROOT%\sherpa-onnx-whisper-base.en"
set "ARCHIVE=%ROOT%\sherpa-onnx-whisper-base.en.tar.bz2.download"
set "EXTRACT=%ROOT%\sherpa-onnx-whisper-base.en.extract"
if exist "%MODEL%\base.en-encoder.int8.onnx" if exist "%MODEL%\base.en-decoder.int8.onnx" if exist "%MODEL%\base.en-tokens.txt" exit /b 0
mkdir "%ROOT%" 2>nul
if exist "%ARCHIVE%" del /f /q "%ARCHIVE%"
if exist "%EXTRACT%" rmdir /s /q "%EXTRACT%"
if exist "%MODEL%" rmdir /s /q "%MODEL%"
mkdir "%EXTRACT%" 2>nul
rem Omit --silent so progress is captured in the MSI log when the installer runs this (WixQuietExec).
"%SystemRoot%\System32\curl.exe" --fail -L --show-error --progress-bar -o "%ARCHIVE%" "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-base.en.tar.bz2"
if errorlevel 1 exit /b %ERRORLEVEL%
"%SystemRoot%\System32\tar.exe" -xjf "%ARCHIVE%" -C "%EXTRACT%"
if errorlevel 1 exit /b %ERRORLEVEL%
move "%EXTRACT%\sherpa-onnx-whisper-base.en" "%MODEL%" >nul
set "RESULT=%ERRORLEVEL%"
if exist "%ARCHIVE%" del /f /q "%ARCHIVE%"
if exist "%EXTRACT%" rmdir /s /q "%EXTRACT%"
exit /b %RESULT%
