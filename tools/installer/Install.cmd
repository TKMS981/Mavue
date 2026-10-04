@echo off
rem Installs Mavue for the current user (no administrator rights). See README.txt.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
if errorlevel 1 pause
