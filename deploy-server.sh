#!/usr/bin/env bash
# Deploy on the server (tharmas): run from inside ~/threadtender after a push.
# (The post-receive hook does this automatically when main is pushed.)
# Same shape as cardlog's: sync the working tree, do a clean build, restart the
# user service.
set -euo pipefail
cd "$(dirname "$0")"

# NixOS: no system dotnet, and anything installed imperatively gets garbage
# collected. If dotnet is missing, re-run this script inside an ephemeral nix
# shell that has the .NET 10 SDK.
if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet not found, re-running inside nix shell (nixpkgs#dotnet-sdk_10)..."
  exec nix shell nixpkgs#dotnet-sdk_10 --command bash "$0" "$@"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

git reset --hard
rm -rf bin/publish
# --disable-build-servers: otherwise a lingering compiler server keeps the
# post-receive hook's output open and `git push` hangs until it idles out.
dotnet publish ThreadTender/ThreadTender.csproj -c Release -o bin/publish --disable-build-servers

# State (SQLite DB, held attachments) lives in ./data, set via DATA_DIR in the
# unit. The token comes from ./.env as DISCORD_TOKEN. Both are gitignored, so
# 'git reset --hard' leaves them alone.
# The bot refuses to start without a settings.json, even when DISCORD_TOKEN is
# set, so seed a placeholder (the env var overrides its token).
mkdir -p data
[ -f data/settings.json ] || cp settings.example.json data/settings.json

systemctl --user daemon-reload
if grep -qE '^DISCORD_TOKEN=.+' .env 2>/dev/null; then
  systemctl --user restart threadtender.service
else
  echo "No DISCORD_TOKEN in .env yet: built, but not (re)starting the service."
fi

# Server-only extras (gitignored): put more commands in deploy-server.local.sh.
if [ -f deploy-server.local.sh ]; then
  echo "Running deploy-server.local.sh..."
  bash deploy-server.local.sh
fi

echo "Deployed $(git rev-parse --short HEAD): $(git log -1 --format=%s)"
