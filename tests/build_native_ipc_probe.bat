@echo off
setlocal
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR exit /b 1
set /p VCVER=<"%VSDIR%\VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt"
set "VC=%VSDIR%\VC\Tools\MSVC\%VCVER%"
set "PATH=%VC%\bin\Hostx64\x64;%PATH%"
set "LIB=%VC%\lib\x64"
cd /d "%~dp0.."
if not exist native\out mkdir native\out
lib /nologo /def:native\kernel32.def /out:native\out\kernel32.lib /machine:x64 || exit /b 1
cl /nologo /c /WX /O2 /GS- /Zl /EHs-c- /GR- /std:c++17 native\mpf_ipc.cpp /Fo:native\out\mpf_ipc.obj || exit /b 1
cl /nologo /c /WX /O2 /GS- /Zl /EHs-c- /GR- /std:c++17 tests\native_ipc_probe.cpp /Fo:native\out\native_ipc_probe.obj || exit /b 1
cl /nologo /c /WX /O2 /GS- /Zl /EHs-c- /GR- /std:c++17 tests\native_lua_ipc.cpp /Fo:native\out\native_lua_ipc.obj || exit /b 1
rem ExitProcess belongs to the helper only; generate its tiny import library separately.
>native\out\probe_exit.def echo LIBRARY kernel32.dll
>>native\out\probe_exit.def echo EXPORTS
>>native\out\probe_exit.def echo ExitProcess
lib /nologo /def:native\out\probe_exit.def /out:native\out\probe_exit.lib /machine:x64 || exit /b 1
ml64 /nologo /c /Fo native\out\chkstk.obj native\chkstk.asm || exit /b 1
link /nologo /WX /NODEFAULTLIB /ENTRY:mainCRTStartup /SUBSYSTEM:CONSOLE /OUT:native\out\native_ipc_probe.exe native\out\native_ipc_probe.obj native\out\native_lua_ipc.obj native\out\mpf_ipc.obj native\out\chkstk.obj native\out\kernel32.lib native\out\probe_exit.lib || exit /b 1
