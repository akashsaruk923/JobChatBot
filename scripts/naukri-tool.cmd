@echo off
setlocal
cd /d "%~dp0.."
where dotnet >nul 2>&1 || (echo Install .NET SDK 10.0.403 first. & exit /b 1)
echo NaukriTool - no Angular server, no API key. Edge must be installed.
dotnet run --project backend\NaukriTool -- %*
if errorlevel 1 pause
