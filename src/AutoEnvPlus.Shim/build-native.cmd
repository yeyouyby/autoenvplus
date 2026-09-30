@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "SOURCE=%~1"
set "OUTPUT=%~2"
set "RESOURCE=%~3"
set "VERSION_MAJOR=%~4"
set "VERSION_MINOR=%~5"
set "VERSION_PATCH=%~6"
set "VERSION_REVISION=%~7"
set "ICON=%~8"
if not defined SOURCE exit /b 2
if not defined OUTPUT exit /b 2
if not defined RESOURCE exit /b 2
if not defined VERSION_MAJOR exit /b 2
if not defined VERSION_MINOR exit /b 2
if not defined VERSION_PATCH exit /b 2
if not defined VERSION_REVISION exit /b 2
if not defined ICON exit /b 2

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if defined VCToolsInstallDir goto compile
if not exist "%VSWHERE%" goto missing_tools
for /f "usebackq tokens=*" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSROOT=%%I"
if not defined VSROOT goto missing_tools
call "%VSROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%

:compile

for %%I in ("%OUTPUT%") do set "OUTDIR=%%~dpI"
for %%I in ("%ICON%") do set "ICONDIR=%%~dpI"
for %%I in ("%ICONDIR%.") do set "ICONDIR=%%~fI"
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

rc /nologo /i "%ICONDIR%" /d AUTOENVPLUS_VERSION_MAJOR=%VERSION_MAJOR% /d AUTOENVPLUS_VERSION_MINOR=%VERSION_MINOR% /d AUTOENVPLUS_VERSION_PATCH=%VERSION_PATCH% /d AUTOENVPLUS_VERSION_REVISION=%VERSION_REVISION% /fo "%OUTDIR%autoenvplus-shim.res" "%RESOURCE%"
if errorlevel 1 goto resource_failed

cl /nologo /std:c++20 /permissive- /utf-8 /O2 /GL /MT /EHsc /GR- /DUNICODE /D_UNICODE "%SOURCE%" "%OUTDIR%autoenvplus-shim.res" /Fo"%OUTDIR%autoenvplus-shim.obj" /link /LTCG /OPT:REF /OPT:ICF /INCREMENTAL:NO /OUT:"%OUTPUT%" windowsapp.lib
set "RESULT=%ERRORLEVEL%"
:cleanup
if exist "%OUTDIR%autoenvplus-shim.obj" del /q "%OUTDIR%autoenvplus-shim.obj"
if exist "%OUTDIR%autoenvplus-shim.res" del /q "%OUTDIR%autoenvplus-shim.res"
exit /b %RESULT%

:resource_failed
set "RESULT=%ERRORLEVEL%"
goto cleanup

:missing_tools
echo Native Shim build requires Visual Studio Build Tools with C++. 1>&2
exit /b 3
