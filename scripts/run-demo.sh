#!/usr/bin/env bash
# Run Adelaide locally in DEMO MODE: the real web host + Angular review UI on the synthetic corpus in demo/.
# No cloud services, no credentials, no outbound network (see src/Adelaide.Web/DemoMode.cs).
#   scripts/run-demo.sh            build if needed, then serve on http://localhost:5080
#   scripts/run-demo.sh --reset    wipe the local demo state (.adelaide-demo/) first
#   PORT=6000 scripts/run-demo.sh  use another port
set -euo pipefail
cd "$(dirname "$0")/.."

STATE=".adelaide-demo"
PORT="${PORT:-5080}"
if [[ "${1:-}" == "--reset" ]]; then rm -rf "$STATE"; echo "demo state reset"; fi
mkdir -p "$STATE"

if [[ ! -f src/Adelaide.Web.Client/dist/adelaide-web-client/browser/index.html ]]; then
  echo "Building the Angular client (first run only)…"
  (cd src/Adelaide.Web.Client && npm ci --no-audit --no-fund && npm run build)
fi
dotnet build src/Adelaide.Web/Adelaide.Web.csproj -c Release --nologo -v q

export ADELAIDE_DEMO_MODE=1
export ASPNETCORE_ENVIRONMENT=Development          # enables the local dev identity below — never set on a deployed host
export ADELAIDE_DEV_ACTOR=demo-reviewer            # signs every request in as a Reviewer+Admin (Development only)
export ADELAIDE_AUDIT_DB="$STATE/audit.db"         # hash-chained SQLite ledger
export ADELAIDE_INBOX="$STATE/inbox"               # captured uploads + review sidecars
export ADELAIDE_LOG_DIR="$STATE/logs"
export ADELAIDE_DEMO_FILED="$STATE/filed"          # the local stand-in for the SharePoint library
export ASPNETCORE_URLS="http://localhost:$PORT"

echo "Adelaide demo → http://localhost:$PORT   (upload files from demo/samples/)"
exec dotnet src/Adelaide.Web/bin/Release/net10.0/Adelaide.Web.dll
