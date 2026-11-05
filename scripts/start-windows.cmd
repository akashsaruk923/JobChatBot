@echo off
setlocal
cd /d "%~dp0.."
where dotnet >nul 2>&1 || (echo Install the .NET SDK specified in global.json first. & exit /b 1)
where npm >nul 2>&1 || (echo Install Node 18.20.8 and npm 10 first. & exit /b 1)
dotnet build backend\JobChatBot.Api --nologo
if errorlevel 1 exit /b 1
if not exist frontend\node_modules (
  pushd frontend
  call npm ci
  if errorlevel 1 (popd & exit /b 1)
  popd
)
start "JobChat API - keep open" cmd /k "cd /d backend\JobChatBot.Api && dotnet run --no-build --launch-profile http"
start "JobChat frontend - keep open" cmd /k "cd /d frontend && npm start"
echo Wait for both servers to start, then open http://127.0.0.1:4200
echo Save your experience, search Naukri, and sign in in the Edge window.
echo Close existing JobChat servers before running this script again.
