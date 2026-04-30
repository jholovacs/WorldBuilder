#!/usr/bin/env bash
# Run WorldBuilder: ASP.NET Core API + Angular dev server (requires Node/npm).
# Usage: chmod +x run.sh && ./run.sh
# Works in Git Bash, WSL, macOS, Linux.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

cleanup() {
  if [[ -n "${DOTNET_PID:-}" ]] && kill -0 "$DOTNET_PID" 2>/dev/null; then
    kill "$DOTNET_PID" 2>/dev/null || true
    wait "$DOTNET_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

echo "Starting API on http://localhost:5011 ..."
dotnet run --project "$ROOT/WorldBuilder.Api/WorldBuilder.Api.csproj" --urls "http://localhost:5011" &
DOTNET_PID=$!

echo "Waiting for API startup ..."
sleep 4

cd "$ROOT/web"
if [[ ! -d node_modules ]]; then
  echo "Installing npm dependencies ..."
  npm install
fi

echo "Starting Angular on http://localhost:4200 (proxies /api to the API) ..."
npm start
