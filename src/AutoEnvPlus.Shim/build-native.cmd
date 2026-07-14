@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "SOURCE=%~1"
set "OUTPUT=%~2"
if not defined SOURCE exit /b 2
if not defined OUTPUT exit /b 2

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if defined VCToolsInstallDir goto compile
if not exist "%VSWHERE%" goto missing_tools
for /f "usebackq tokens=*" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSROOT=%%I"
if not defined VSROOT goto missing_tools
call "%VSROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%

:compile

for %%I in ("%OUTPUT%") do set "OUTDIR=%%~dpI"
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

cl /nologo /std:c++20 /permissive- /utf-8 /O2 /GL /MT /EHsc /GR- /DUNICODE /D_UNICODE "%SOURCE%" /Fo"%OUTDIR%autoenvplus-shim.obj" /link /LTCG /OPT:REF /OPT:ICF /INCREMENTAL:NO /OUT:"%OUTPUT%" windowsapp.lib
set "RESULT=%ERRORLEVEL%"
if exist "%OUTDIR%autoenvplus-shim.obj" del /q "%OUTDIR%autoenvplus-shim.obj"
exit /b %RESULT%

:missing_tools
echo Native Shim build requires Visual Studio Build Tools with C++. 1>&2
exit /b 3
