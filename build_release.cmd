@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>&1
if errorlevel 1 (
  echo .NET 8 SDK is required.
  exit /b 1
)
if "%~1"=="" goto both
if /I "%~1"=="x64" goto single
if /I "%~1"=="x86" goto single
echo Usage: build_release.cmd [x64^|x86]
exit /b 2

:both
call :build x64
if errorlevel 1 exit /b 1
call :build x86
if errorlevel 1 exit /b 1
exit /b 0

:single
call :build %~1
exit /b %ERRORLEVEL%

:build
echo Building AIrCon Release %~1...
dotnet build "AIrCon.sln" -c Release -p:Platform=%~1 -p:TvAIrPublicRelease=true -p:TvAIrDeveloperDiagnostics=false -warnaserror
exit /b %ERRORLEVEL%
