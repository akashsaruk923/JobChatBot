#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if test -f /workspace/.setup/activate.sh; then . /workspace/.setup/activate.sh; fi
cd "$ROOT_DIR"
dotnet build backend/JobChatBot.Api --nologo
(cd backend/JobChatBot.Api && ASPNETCORE_ENVIRONMENT=Development Notifications__DisableSending=true dotnet run --no-build --no-launch-profile --urls http://127.0.0.1:5080) &
API_PID=$!
trap 'kill "$API_PID" 2>/dev/null || true' EXIT INT TERM
cd frontend
npm start
