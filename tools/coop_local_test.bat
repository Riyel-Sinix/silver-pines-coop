@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Silver Pines Coop two-copy test. Keep this launcher ASCII; PowerShell handles the Unicode text.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0coop_local_test.ps1" %*
exit /b %ERRORLEVEL%
