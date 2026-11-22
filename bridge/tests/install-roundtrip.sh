#!/bin/sh
# Runs bridge/scripts/install.sh and bridge/scripts/uninstall.sh against a temporary home folder, with a
# stand-in launchctl that keeps DOTNET_STARTUP_HOOKS in a file: your login environment, your
# LaunchAgents and your real home folder are never touched. Needs the .NET SDK.
set -u

repo=$(cd "$(dirname "$0")/.." && pwd -P)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
failures=0
check() { if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: expected [$2], got [$3]"; failures=$((failures + 1)); fi; }

mkdir -p "$work/bin" "$work/home"
cat > "$work/bin/launchctl" <<'EOF'
#!/bin/sh
state="$FAKE_LAUNCHCTL"
echo "$*" >> "$state.log"
case "$1" in
  getenv) [ -f "$state.env" ] && cat "$state.env"; exit 0 ;;
  setenv) printf '%s' "$3" > "$state.env" ;;
  unsetenv) rm -f "$state.env" ;;
esac
exit 0
EOF
chmod +x "$work/bin/launchctl"

export FAKE_LAUNCHCTL="$work/launchctl"
export PATH="$work/bin:$PATH"
export DOTNET_CLI_HOME="$HOME" NUGET_PACKAGES="$HOME/.nuget/packages"   # reuse your SDK caches
real_home=$HOME
real_agent="$real_home/Library/LaunchAgents/local.servicestudiobridge.plist"
fingerprint() { if [ -e "$1" ]; then shasum "$1" | cut -d' ' -f1; else echo absent; fi; }
real_before=$(fingerprint "$real_agent")   # a real install may exist: it must come out unchanged
export HOME="$work/home"
support="$HOME/Library/Application Support/ServiceStudioBridge"
dll="$support/bin/ServiceStudioBridge.Hook.dll"
other="/somewhere/Other Hook.dll"
printf '%s' "$other" > "$FAKE_LAUNCHCTL.env"   # another hook already set, as on a real machine

check "refuses a port next to Service Studio's MCP" 2 "$(sh "$repo/scripts/install.sh" --port 41821 >/dev/null 2>&1; echo $?)"
check "refuses unknown options" 2 "$(sh "$repo/scripts/install.sh" --allow-host x >/dev/null 2>&1; echo $?)"

sh "$repo/scripts/install.sh" > "$work/install.out" 2>&1
check "install succeeds" 0 $?
check "hook built" yes "$([ -f "$dll" ] && echo yes || echo no)"
check "bridge built next to the hook" yes "$([ -f "$support/bin/ServiceStudioBridge.dll" ] && echo yes || echo no)"
check "config has the default port and nothing else" '{ "port": 48720 }' "$(tr -s ' \n' '  ' < "$support/config.json" | sed 's/ $//')"
check "token is 64 hex characters" 64 "$(tr -dc 'a-f0-9' < "$support/token" | wc -c | tr -d ' ')"
check "token readable by the user only" "-rw-------" "$(ls -l "$support/token" | cut -c1-10)"
check "headers helper prints the header" "{\"Authorization\":\"Bearer $(cat "$support/token")\"}" "$("$support/headers.sh")"
check "login item is a valid plist" yes "$(plutil -lint "$HOME/Library/LaunchAgents/local.servicestudiobridge.plist" >/dev/null && echo yes || echo no)"
check "login item runs the named script" "$support/Service Studio MCP Bridge" "$(/usr/libexec/PlistBuddy -c 'Print :ProgramArguments:0' "$HOME/Library/LaunchAgents/local.servicestudiobridge.plist")"
check "login item script is executable" yes "$([ -x "$support/Service Studio MCP Bridge" ] && echo yes || echo no)"
check "login item names the hook" "$dll" "$(/usr/libexec/PlistBuddy -c 'Print :ProgramArguments:1' "$HOME/Library/LaunchAgents/local.servicestudiobridge.plist")"
check "variable keeps the other hook and adds ours" "$other:$dll" "$(cat "$FAKE_LAUNCHCTL.env")"
check "login item bootstrapped" yes "$(grep -q '^bootstrap gui/' "$FAKE_LAUNCHCTL.log" && echo yes || echo no)"

# The login item's own script, run as launchd would (minus the 5-second wait): must not add a duplicate.
sed 's/^sleep 5$//; s#/bin/launchctl#launchctl#g' "$support/Service Studio MCP Bridge" > "$work/login-item.sh"
sh "$work/login-item.sh" "$dll"
check "login item doesn't duplicate the entry" "$other:$dll" "$(cat "$FAKE_LAUNCHCTL.env")"

token=$(cat "$support/token")
touch "$support/Service Studio Bridge"   # an install from before the rename
sh "$repo/scripts/install.sh" --port 48730 > /dev/null 2>&1
check "re-install removes the pre-rename login script" no "$([ -e "$support/Service Studio Bridge" ] && echo yes || echo no)"
check "re-install keeps the token" "$token" "$(cat "$support/token")"
check "re-install with --port changes the port" yes "$(grep -q '"port": 48730' "$support/config.json" && echo yes || echo no)"
sh "$repo/scripts/install.sh" > /dev/null 2>&1
check "re-install without --port keeps it" yes "$(grep -q '"port": 48730' "$support/config.json" && echo yes || echo no)"
check "re-install leaves no build folders behind" "bin" "$(cd "$support" && ls -d bin* | tr '\n' ' ' | sed 's/ $//')"

sh "$repo/scripts/uninstall.sh" > /dev/null 2>&1
check "uninstall succeeds" 0 $?
check "variable keeps only the other hook" "$other" "$(cat "$FAKE_LAUNCHCTL.env")"
check "support folder removed" no "$([ -e "$support" ] && echo yes || echo no)"
check "login item removed" no "$([ -e "$HOME/Library/LaunchAgents/local.servicestudiobridge.plist" ] && echo yes || echo no)"

printf '%s' "$dll" > "$FAKE_LAUNCHCTL.env"
mkdir -p "$support"
sh "$repo/scripts/uninstall.sh" > /dev/null 2>&1
check "uninstall unsets the variable when ours was the only hook" no "$([ -f "$FAKE_LAUNCHCTL.env" ] && echo yes || echo no)"

check "real home untouched" "$real_before" "$(fingerprint "$real_agent")"

echo
[ "$failures" -eq 0 ] && echo "All checks passed." || { echo "$failures check(s) failed."; cat "$work/install.out"; exit 1; }
