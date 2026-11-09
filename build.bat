@echo off
rem Builds the whole MPFever release from source: native DLL, launcher, then the zip in release\.
rem Requires .NET SDK 10 and Visual Studio C++ tools. The separate server-list service still needs the .NET 4.8 targeting pack.
setlocal
cd /d "%~dp0"
echo MPFever build %date% %time%
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (echo vswhere.exe not found: install Visual Studio & exit /b 1)
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%i"
if not defined MSBUILD (echo MSBuild not found & exit /b 1)
rem the release's version: the archives' names, and the product version of winhttp.dll and the .exe files (Properties >
rem Details, checked by code signing: the same for every signed file of a release)
set "VERSION=0.7.13-beta.1"

echo === 1/4 native module
call native\build.bat || exit /b 1

echo === 2/4 launcher
set "VERPROPS=-p:Version=%VERSION% -p:IncludeSourceRevisionInInformationalVersion=false"
where dotnet >nul 2>&1 || (echo .NET SDK 10 not found & exit /b 1)
dotnet publish launcher\MPFever.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false %VERPROPS% -o launcher\bin\ReleasePublish --nologo || exit /b 1
rem the public server list (a service of its own, for the PC that keeps the list: not in the players' archive)
"%MSBUILD%" serverlist\MPFeverServerList.csproj -restore -p:Configuration=Release %VERPROPS% -v:minimal -nologo || exit /b 1

rem (the signatures next to the launcher of the build folder too: without them, on a game build the module does not know,
rem every hook stays off and the games drift apart)
if exist native\mpfever_sigs.txt copy /y native\mpfever_sigs.txt launcher\bin\ReleasePublish\ >nul
copy /y native\out\winhttp.dll launcher\bin\ReleasePublish\ >nul || exit /b 1

rem every check that needs no game (tests\run_all.py): one failure and no archive is made
echo === 3/4 tests
where python >nul 2>&1 || (echo Python not found: install Python 3 and lupa ^(pip install lupa^) & exit /b 1)
call tests\build_native_ipc_probe.bat || exit /b 1
set "MPFEVER_IPC_NATIVE_TEST=%CD%\native\out\native_ipc_probe.exe"
python tests\run_all.py --exe launcher\bin\ReleasePublish\MPFever.exe || (echo TESTS FAILED: no archive made & exit /b 1)

echo === 4/4 package
python tools\package_release.py %VERSION% || exit /b 1
