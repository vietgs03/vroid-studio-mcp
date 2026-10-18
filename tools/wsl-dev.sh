#!/usr/bin/env bash
# Dev loop for vroid-studio-mcp on WSL2 (Windows host, mirrored networking).
#
# Two installs are kept apart on purpose:
#   prod : the user's real VRoid Studio install, MCP 127.0.0.1:17891 (jcode server "vroid")
#   dev  : a sandbox copy of the app used for plugin development, MCP 127.0.0.1:17893
#
#   tools/wsl-dev.sh build              build the plugin against the dev sandbox interop assemblies
#   tools/wsl-dev.sh deploy [dev|prod]  copy the built dll (+ i18n/labels) into an install (default dev)
#   tools/wsl-dev.sh start  [dev|prod]  launch VRoid Studio detached
#   tools/wsl-dev.sh stop   [dev|prod]  quit cleanly over the raw protocol (exit discard)
#   tools/wsl-dev.sh status [dev|prod]  process + endpoint health
#   tools/wsl-dev.sh log    [dev|prod]  tail the BepInEx log
#   tools/wsl-dev.sh test   [dev|prod] [api_tests args...]  run the external harness
#   tools/wsl-dev.sh cycle              build + stop dev + deploy dev + start dev + wait ready
#
# Override paths with VROID_PROD_DIR / VROID_DEV_DIR. Requires the .NET SDK (~/.dotnet by default).
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROD_DIR="${VROID_PROD_DIR:-/mnt/c/Users/HASAKI/AppData/Local/Programs/VRoidStudio/2.14.0}"
DEV_DIR="${VROID_DEV_DIR:-/mnt/c/Users/HASAKI/dev/vroid-run/game}"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
DLL="$repo/plugin/VroidStudioMcp/bin/Release/VroidStudioMcp.dll"

dir_of()  { case "${1:-dev}" in prod) echo "$PROD_DIR";; dev) echo "$DEV_DIR";; *) echo "unknown target '$1' (dev|prod)" >&2; exit 2;; esac; }
tcp_of()  { case "${1:-dev}" in prod) echo 17890;; *) echo 17892;; esac; }
mcp_of()  { case "${1:-dev}" in prod) echo 17891;; *) echo 17893;; esac; }
win()     { wslpath -w "$1"; }

# PIDs of VRoidStudio.exe whose image lives in the given install dir.
pids_of() {
    local exe; exe="$(win "$1")\\VRoidStudio.exe"
    powershell.exe -NoProfile -Command \
        "Get-Process VRoidStudio -ErrorAction SilentlyContinue | Where-Object { \$_.Path -eq '$exe' } | ForEach-Object { \$_.Id }" \
        | tr -d '\r'
}

tcp_cmd() { # tcp_cmd <port> <line>
    python3 - "$1" "$2" <<'EOF'
import socket, sys
port, line = int(sys.argv[1]), sys.argv[2]
with socket.create_connection(("127.0.0.1", port), timeout=10) as s:
    s.sendall((line + "\n").encode())
    s.settimeout(30)
    buf = b""
    while not buf.endswith(b"\n"):
        chunk = s.recv(65536)
        if not chunk:
            break
        buf += chunk
print(buf.decode(errors="replace").strip())
EOF
}

wait_ready() { # wait_ready <target> [timeout_s]
    local port; port="$(tcp_of "$1")"
    local deadline=$(( $(date +%s) + ${2:-180} ))
    while (( $(date +%s) < deadline )); do
        if out="$(tcp_cmd "$port" state 2>/dev/null)" && [[ "$out" == *'"ok":true'* ]]; then
            echo "ready: $out"; return 0
        fi
        sleep 2
    done
    echo "timed out waiting for 127.0.0.1:$port" >&2; return 1
}

cmd_build() {
    [[ -f "$DEV_DIR/BepInEx/interop/Il2Cppmscorlib.dll" ]] || {
        echo "no interop assemblies in $DEV_DIR (start the dev app once so BepInEx generates them)" >&2; exit 1; }
    dotnet build "$repo/plugin/VroidStudioMcp/VroidStudioMcp.csproj" -c Release -p:GameDir="$DEV_DIR"
}

cmd_deploy() {
    local t="${1:-dev}" d; d="$(dir_of "$t")"
    [[ -f "$DLL" ]] || { echo "build first" >&2; exit 1; }
    if [[ -n "$(pids_of "$d")" ]]; then echo "$t is running; stop it first (the dll is locked)" >&2; exit 1; fi
    cp "$DLL" "$d/BepInEx/plugins/VroidStudioMcp.dll"
    mkdir -p "$d/i18n" "$d/labels"
    cp "$repo"/data/i18n/*.json "$d/i18n/"
    cp "$repo"/data/labels/*.json "$d/labels/"
    echo "deployed $(sha256sum "$DLL" | cut -c1-12) -> $t"
}

cmd_start() {
    local t="${1:-dev}" d; d="$(dir_of "$t")"
    if [[ -n "$(pids_of "$d")" ]]; then echo "$t already running"; return 0; fi
    powershell.exe -NoProfile -Command \
        "Start-Process -FilePath '$(win "$d")\\VRoidStudio.exe' -WorkingDirectory '$(win "$d")'" >/dev/null
    echo "started $t"
}

cmd_stop() {
    local t="${1:-dev}" d; d="$(dir_of "$t")"
    [[ -n "$(pids_of "$d")" ]] || { echo "$t not running"; return 0; }
    tcp_cmd "$(tcp_of "$t")" "exit discard" || true
    for _ in $(seq 1 30); do [[ -z "$(pids_of "$d")" ]] && { echo "stopped $t"; return 0; }; sleep 1; done
    echo "$t did not exit within 30s; not force-killing (unsaved work?)" >&2; return 1
}

cmd_status() {
    local t="${1:-dev}" d; d="$(dir_of "$t")"
    echo "target=$t dir=$d"
    echo "pids: $(pids_of "$d" | tr '\n' ' ')"
    tcp_cmd "$(tcp_of "$t")" state 2>/dev/null || echo "tcp $(tcp_of "$t"): down"
    curl -s -m 5 -X POST "http://127.0.0.1:$(mcp_of "$t")/mcp" -H 'Content-Type: application/json' \
        -d '{"jsonrpc":"2.0","id":1,"method":"ping"}' || echo "mcp $(mcp_of "$t"): down"
    echo
}

cmd_log() {
    local d; d="$(dir_of "${1:-dev}")"
    tr -d '\r' < "$d/BepInEx/LogOutput.log" | grep -v -i 'native detour' | tail -n "${LINES:-60}"
}

cmd_test() {
    local t="${1:-dev}"; shift || true
    local tmp; tmp="$(dirname "$DEV_DIR")/tmp"; mkdir -p "$tmp"
    python3 "$repo/tools/api-tests/api_tests.py" \
        --tcp "127.0.0.1:$(tcp_of "$t")" --mcp "http://127.0.0.1:$(mcp_of "$t")/mcp/" --tmp-dir "$tmp" "$@"
}

cmd_cycle() {
    cmd_build
    cmd_stop dev
    cmd_deploy dev
    cmd_start dev
    wait_ready dev
}

case "${1:-}" in
    build)  cmd_build ;;
    deploy) cmd_deploy "${2:-dev}" ;;
    start)  cmd_start "${2:-dev}" ;;
    stop)   cmd_stop "${2:-dev}" ;;
    status) cmd_status "${2:-dev}" ;;
    log)    cmd_log "${2:-dev}" ;;
    wait)   wait_ready "${2:-dev}" "${3:-180}" ;;
    test)   shift; cmd_test "$@" ;;
    cycle)  cmd_cycle ;;
    *) sed -n '2,17p' "${BASH_SOURCE[0]}"; exit 2 ;;
esac
