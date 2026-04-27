@echo off
rem Wrapper. Run from anywhere — switches to repo root and invokes build.ps1.
cd /d "%~dp0.."
powershell -NoProfile -ExecutionPolicy Bypass -File "build\build.ps1" %*
exit /b %errorlevel%
