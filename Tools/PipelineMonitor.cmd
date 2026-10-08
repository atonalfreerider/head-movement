@echo off
rem Starts the dancecap Pipeline Monitor from the workspace that holds this project.
rem Set HEADMOVEMENT_PIPELINE_MONITOR to the launcher if the workspace is laid out differently.
setlocal
set "EXE=%HEADMOVEMENT_PIPELINE_MONITOR%"
if not defined EXE set "EXE=%~dp0..\..\tools\PipelineMonitor\PipelineMonitor.exe"
if not exist "%EXE%" (
  echo Pipeline Monitor not found: "%EXE%"
  echo Build it: powershell -NoProfile -ExecutionPolicy Bypass -File tools\PipelineMonitor\build.ps1
  pause
  exit /b 1
)
start "" "%EXE%" %*
