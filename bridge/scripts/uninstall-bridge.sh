#!/bin/sh
# Removes Service Studio Bridge for the current macOS user: the login item, the hook's entry in
# DOTNET_STARTUP_HOOKS (other hooks stay), and ~/Library/Application Support/ServiceStudioBridge,
# downloaded modules included.
set -eu

support="$HOME/Library/Application Support/ServiceStudioBridge"
dll="$support/bin/ServiceStudioBridge.Hook.dll"
label=local.servicestudiobridge
agent="$HOME/Library/LaunchAgents/$label.plist"

launchctl bootout "gui/$(id -u)/$label" 2>/dev/null || true
rm -f "$agent"

hooks=$(launchctl getenv DOTNET_STARTUP_HOOKS || true)
rest=$(printf '%s' "$hooks" | tr ':' '\n' | grep -vxF "$dll" | paste -sd ':' - || true)
if [ -z "$rest" ]; then
  launchctl unsetenv DOTNET_STARTUP_HOOKS
else
  launchctl setenv DOTNET_STARTUP_HOOKS "$rest"
fi

rm -rf "$support"

cat <<EOF
Removed Service Studio Bridge. Quit and reopen Service Studio to unload it, and remove it from Claude Code:
  claude mcp remove --scope user servicestudio-bridge
EOF
