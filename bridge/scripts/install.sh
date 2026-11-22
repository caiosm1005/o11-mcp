#!/bin/sh
# Installs Service Studio MCP Bridge for the current macOS user: no package, no admin rights.
# Usage: bridge/scripts/install.sh [--port <port>]
#
# The bridge works with whichever environment Service Studio is signed in to: nothing here
# names one. Everything lands in your home folder:
#   ~/Library/Application Support/ServiceStudioBridge/   the hook and the bridge (bin/, with its VERSION), config.json, token, headers.sh, log,
#     and "Service Studio MCP Bridge", the login item's script
#   ~/Library/LaunchAgents/local.servicestudiobridge.plist
#     a login item that adds the hook to DOTNET_STARTUP_HOOKS at every login,
#     next to any hook already there. macOS lists it as "Service Studio MCP Bridge".
# Re-running it rebuilds the hook and keeps the token (and the port, unless you pass a new one):
# that's how an installed bridge is updated.
set -eu

repo=$(cd "$(dirname "$0")/.." && pwd -P)
support="$HOME/Library/Application Support/ServiceStudioBridge"
# The assembly DOTNET_STARTUP_HOOKS names: a .NET Standard loader that every .NET app can load
# and that loads the bridge (ServiceStudioBridge.dll, next to it) inside Service Studio only.
dll="$support/bin/ServiceStudioBridge.Hook.dll"
label=local.servicestudiobridge
agent="$HOME/Library/LaunchAgents/$label.plist"
# macOS names a login item after the program it runs, in its notice and in Login Items & Extensions.
login_script="$support/Service Studio MCP Bridge"
port=""

while [ $# -gt 0 ]; do
  case "$1" in
    --port) [ $# -ge 2 ] || { echo "--port needs a number" >&2; exit 2; }; port="$2"; shift 2 ;;
    -h|--help) sed -n '2,3p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

if [ -n "$port" ]; then
  case "$port" in *[!0-9]*) echo "--port must be a number" >&2; exit 2 ;; esac
  if [ "$port" -ge 41800 ] && [ "$port" -le 41860 ]; then
    echo "Port $port is in the range Service Studio's own MCP server may use (41820 and up): pick another." >&2
    exit 2
  fi
fi
command -v dotnet >/dev/null || { echo "Building the hook needs the .NET SDK (https://dot.net)." >&2; exit 1; }

# 1. Build, then swap the folder in whole: a running Service Studio keeps its copy of the old files.
mkdir -p "$support"
rm -rf "$support/bin.new" "$support/bin.old"
dotnet build "$repo/src/ServiceStudioBridge.csproj" -c Release -nologo -v q -o "$support/bin.new" >/dev/null
dotnet build "$repo/src/Hook/ServiceStudioBridge.Hook.csproj" -c Release -nologo -v q -o "$support/bin.new" >/dev/null
# The version agents compare with BRIDGE.md's to offer an update, readable without Service Studio.
version=$(dotnet msbuild "$repo/src/ServiceStudioBridge.csproj" -getProperty:Version)
printf '%s\n' "$version" > "$support/bin.new/VERSION"
[ -d "$support/bin" ] && mv "$support/bin" "$support/bin.old"
mv "$support/bin.new" "$support/bin"
rm -rf "$support/bin.old"

# 2. The port: written on first install or when given.
if [ -n "$port" ] || [ ! -f "$support/config.json" ]; then
  printf '{\n  "port": %s\n}\n' "${port:-48720}" > "$support/config.json"
fi

# 3. The token clients send, readable by you only, and the helper Claude Code runs to send it.
if [ ! -s "$support/token" ]; then
  (umask 077; LC_ALL=C tr -dc 'a-f0-9' < /dev/urandom | head -c 64 > "$support/token")
fi
chmod 600 "$support/token"
cat > "$support/headers.sh" <<'EOF'
#!/bin/sh
# Prints the bridge's Authorization header, for Claude Code's headersHelper.
printf '{"Authorization":"Bearer %s"}\n' "$(cat "$(dirname "$0")/token")"
EOF
chmod 700 "$support/headers.sh"

# 4. The login item. Its script waits a few seconds so other login items that change the variable
#    finish first, then appends the hook only if it isn't there yet.
cat > "$login_script" <<'EOF'
#!/bin/sh
# Service Studio MCP Bridge's login item: adds the hook ($1) to DOTNET_STARTUP_HOOKS for the apps you
# open from now on, keeping any hook already there. Installed by bridge/scripts/install.sh.
sleep 5
[ -f "$1" ] || exit 0
hooks=$(/bin/launchctl getenv DOTNET_STARTUP_HOOKS)
case ":$hooks:" in
  *":$1:"*) ;;
  *) /bin/launchctl setenv DOTNET_STARTUP_HOOKS "${hooks:+$hooks:}$1" ;;
esac
EOF
chmod 700 "$login_script"
rm -f "$support/Service Studio Bridge"   # the script's name before the rename
mkdir -p "$(dirname "$agent")"
cat > "$agent" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>$label</string>
  <key>ProgramArguments</key>
  <array>
    <string>$login_script</string>
    <string>$dll</string>
  </array>
  <key>RunAtLoad</key>
  <true/>
</dict>
</plist>
EOF
plutil -lint "$agent" >/dev/null

# 5. Load it, and set the variable now for apps opened from here on.
uid=$(id -u)
launchctl bootout "gui/$uid/$label" 2>/dev/null || true
launchctl bootstrap "gui/$uid" "$agent"
hooks=$(launchctl getenv DOTNET_STARTUP_HOOKS || true)
case ":$hooks:" in
  *":$dll:"*) ;;
  *) launchctl setenv DOTNET_STARTUP_HOOKS "${hooks:+$hooks:}$dll" ;;
esac

port=$(sed -n 's/.*"port": *\([0-9]*\).*/\1/p' "$support/config.json")
cat <<EOF
Installed Service Studio MCP Bridge $version.
  Hook:   $dll
  Config: $support/config.json
  Port:   ${port:-48720}

Next:
  1. Quit Service Studio and open it again, so it loads the hook.
  2. Register the bridge with Claude Code (once):
     claude mcp add-json --scope user servicestudio-bridge '{"type":"http","url":"http://127.0.0.1:${port:-48720}/mcp","headersHelper":"\\"$support/headers.sh\\""}'
EOF
