@echo off
setlocal EnableExtensions
rem Prompts UAC once, then runs DownloadModel.cmd with admin rights (needed to write under Program Files).
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -Verb RunAs -Wait -FilePath '%~dp0DownloadModel.cmd'"
exit /b %ERRORLEVEL%
