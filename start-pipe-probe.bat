@echo off
setlocal
set "MPFEVER_IPC_PROBE=1"
set "MPFEVER_PIPE="
if not exist "%~dp0launcher\bin\PipeProbe\MPFever.exe" (
    echo PipeProbe build missing: launcher\bin\PipeProbe\MPFever.exe
    pause
    exit /b 1
)
if not exist "%~dp0launcher\bin\PipeProbe\winhttp.dll" (
    echo PipeProbe native module missing: launcher\bin\PipeProbe\winhttp.dll
    pause
    exit /b 1
)
start "" /D "%~dp0launcher\bin\PipeProbe" "%~dp0launcher\bin\PipeProbe\MPFever.exe" --dev
