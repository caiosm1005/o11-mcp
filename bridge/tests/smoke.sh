#!/bin/sh
# Offline smoke test: loads the hook into a fake process named ServiceStudio through
# DOTNET_STARTUP_HOOKS, as Service Studio would, and checks the HTTP and MCP surface.
# Touches nothing outside a temporary folder; needs the .NET SDK and curl.
set -u

repo=$(cd "$(dirname "$0")/.." && pwd -P)
work=$(mktemp -d)
port=48799
pids=""
failures=0
trap 'for p in $pids; do kill "$p" 2>/dev/null; wait "$p" 2>/dev/null; done; rm -rf "$work"' EXIT

dotnet build "$repo/src/ServiceStudioBridge.csproj" -c Release -nologo -v q -o "$work/hook" >/dev/null || exit 1
dotnet build "$repo/src/Hook/ServiceStudioBridge.Hook.csproj" -c Release -nologo -v q -o "$work/hook" >/dev/null || exit 1
dotnet build "$repo/tests/fake-host/FakeHost.csproj" -c Release -f net10.0 -nologo -v q -o "$work/host" >/dev/null || exit 1

export SSBRIDGE_HOME="$work/home"
mkdir -p "$SSBRIDGE_HOME"
printf '{ "port": %s }\n' "$port" > "$SSBRIDGE_HOME/config.json"

check() { # name, expected, actual
  if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: expected [$2], got [$3]"; failures=$((failures + 1)); fi
}

listening() { curl -s -o /dev/null --max-time 1 "http://127.0.0.1:$port/mcp"; }

start() { # command…: starts a host with the hook; sets $pid and records it for cleanup (no subshell)
  DOTNET_STARTUP_HOOKS="$work/hook/ServiceStudioBridge.Hook.dll" "$@" >"$work/host.out" 2>&1 &
  pid=$!
  pids="$pids $pid"
}

stop() { kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null; }

listening && { echo "Port $port is already in use: stop whatever holds it first."; exit 1; }

wait_port() { i=0; while [ $i -lt 50 ]; do listening && return 0; sleep 0.2; i=$((i + 1)); done; return 1; }

# Processes that aren't Service Studio, or that opt out, must not start the bridge.
start dotnet "$work/host/ServiceStudio.dll"; sleep 2
check "no bridge in a process named dotnet" "closed" "$(listening && echo open || echo closed)"
check "bridge assembly not even loaded there" yes "$(grep -q 'bridge loaded: False' "$work/host.out" && echo yes || echo no)"
stop
# (A VAR=value prefix on a function call outlives the call in POSIX sh: export and unset instead.)
export SSBRIDGE_DISABLE=1
start "$work/host/ServiceStudio"; sleep 2
check "no bridge with SSBRIDGE_DISABLE=1" "closed" "$(listening && echo open || echo closed)"
stop
unset SSBRIDGE_DISABLE

# DOTNET_STARTUP_HOOKS reaches apps on older runtimes too, and a hook that can't load stops them
# from starting. They must start, named ServiceStudio or not (the bridge itself stays off there).
alive() { kill -0 "$pid" 2>/dev/null && grep -q 'fake host' "$work/host.out" && echo yes || echo no; }
if dotnet --list-runtimes | grep -q '^Microsoft.NETCore.App 8\.'; then
  dotnet build "$repo/tests/fake-host/FakeHost.csproj" -c Release -f net8.0 -nologo -v q -o "$work/host8" >/dev/null || exit 1
  start dotnet "$work/host8/ServiceStudio.dll"; sleep 2
  check "a .NET 8 app starts with the hook" yes "$(alive)"
  stop
  start "$work/host8/ServiceStudio"; sleep 2
  check "a .NET 8 app named ServiceStudio starts with the hook" yes "$(alive)"
  check "no bridge on .NET 8" "closed" "$(listening && echo open || echo closed)"
  stop
else
  echo "skip .NET 8 checks: no .NET 8 runtime installed"
fi

# The real thing: a process named ServiceStudio.
start "$work/host/ServiceStudio"
wait_port || { echo "FAIL the bridge never listened"; cat "$SSBRIDGE_HOME/bridge.log" 2>/dev/null; exit 1; }
check "host process still running" "yes" "$(kill -0 "$pid" 2>/dev/null && echo yes || echo no)"
check "bridge assembly loaded by the hook" yes "$(grep -q 'bridge loaded: True' "$work/host.out" && echo yes || echo no)"
check "token file readable by the user only" "-rw-------" "$(ls -l "$SSBRIDGE_HOME/token" | cut -c1-10)"

token=$(cat "$SSBRIDGE_HOME/token")
url="http://127.0.0.1:$port/mcp"
status() { curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$@"; }
rpc() { curl -s --max-time 10 -H "Authorization: Bearer $token" -H 'Content-Type: application/json' -d "$1" "$url"; }

check "no token -> 401" 401 "$(status -X POST -d '{}' "$url")"
check "wrong token -> 401" 401 "$(status -X POST -H 'Authorization: Bearer nope' -d '{}' "$url")"
check "browser Origin -> 403" 403 "$(status -X POST -H "Authorization: Bearer $token" -H 'Origin: https://evil.example' -d '{}' "$url")"
# HttpListener itself answers 404 for a Host it has no prefix for; the bridge answers 403 otherwise.
code=$(status -X POST -H "Authorization: Bearer $token" -H "Host: evil.example:$port" -d '{}' "$url")
check "foreign Host refused" yes "$(if [ "$code" = 403 ] || [ "$code" = 404 ]; then echo yes; else echo "no ($code)"; fi)"
check "GET -> 405" 405 "$(status -H "Authorization: Bearer $token" "$url")"
check "other path -> 404" 404 "$(status -H "Authorization: Bearer $token" "http://127.0.0.1:$port/")"
check "notification -> 202" 202 "$(status -X POST -H "Authorization: Bearer $token" -d '{"jsonrpc":"2.0","method":"notifications/initialized"}' "$url")"

out=$(rpc '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}')
check "initialize echoes a supported protocol" '"2025-06-18"' "$(printf '%s' "$out" | grep -o '"protocolVersion":"[^"]*"' | cut -d: -f2)"
check "initialize names the server" yes "$(printf '%s' "$out" | grep -q '"name":"servicestudio-bridge"' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
check "tools/list has 19 tools" 19 "$(printf '%s' "$out" | grep -o '"inputSchema"' | wc -l | tr -d ' ')"
for t in status listApplications listModules getModuleVersions getConsumers getElementConsumers searchPublicElements getApplicationDependencies listReferenceable downloadModule openModule addDependency uploadModules layoutFlows createApplication updateApplication createModule convertModule setReferenceTenantIdentifier; do
  check "tool $t listed" yes "$(printf '%s' "$out" | grep -q "\"name\":\"$t\"" && echo yes || echo no)"
done
# downloadModule writes a file; openModule, addDependency, layoutFlows, createModule and convertModule change what's
# open in Service Studio, as does setReferenceTenantIdentifier; uploadModules adds versions on the server; createApplication and
# updateApplication change applications.
check "ten tools aren't read-only" 10 "$(printf '%s' "$out" | grep -o '"readOnlyHint":false' | wc -l | tr -d ' ')"

out=$(rpc '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"status","arguments":{}}}')
check "status succeeds outside Service Studio" yes "$(printf '%s' "$out" | grep -q '"isError":false' && echo yes || echo no)"
check "status reports the missing presenter" yes "$(printf '%s' "$out" | grep -q "RuntimeImplementation isn" && echo yes || echo no)"
# Agents compare an installed bridge's version with BRIDGE.md's to offer an update (BRIDGE.md § Versioning).
current=$(sed -n 's/^\*\*Current version: \([0-9.]*\)\.\*\*.*/\1/p' "$repo/../servicestudio-mcp-oml/BRIDGE.md")
[ -n "$current" ] || current="(no **Current version: x.y.z.** line in BRIDGE.md)"
check "status reports BRIDGE.md's current version" "$current" "$(printf '%s' "$out" | grep -o 'Bridge\\":\\"[0-9.]*' | sed 's/.*"//')"
check "status shows the configured port" yes "$(printf '%s' "$out" | grep -q "Port\\\\\":$port" && echo yes || echo no)"
check "status reports capabilities" yes "$(printf '%s' "$out" | grep -q 'Capabilities' && printf '%s' "$out" | grep -q 'AddDependency' && printf '%s' "$out" | grep -q 'DialogWatch' && printf '%s' "$out" | grep -q 'AnsweredPrompts' && printf '%s' "$out" | grep -q '"Upload' && printf '%s' "$out" | grep -q 'ConvertModule' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"listModules","arguments":{}}}')
check "listModules fails cleanly outside Service Studio" yes "$(printf '%s' "$out" | grep -q '"isError":true' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"openModule","arguments":{"module":"X"}}}')
check "openModule fails cleanly outside Service Studio" yes "$(printf '%s' "$out" | grep -q '"isError":true' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"addDependency","arguments":{"module":"X"}}}')
check "addDependency needs the element" yes "$(printf '%s' "$out" | grep -q 'element is required' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":14,"method":"tools/call","params":{"name":"addDependency","arguments":{"module":"X","element":"Y"}}}')
check "addDependency fails cleanly outside Service Studio" yes "$(printf '%s' "$out" | grep -q '"isError":true' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":17,"method":"tools/call","params":{"name":"uploadModules","arguments":{}}}')
check "uploadModules fails cleanly outside Service Studio" yes "$(printf '%s' "$out" | grep -q '"isError":true' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":15,"method":"tools/call","params":{"name":"layoutFlows","arguments":{}}}')
check "layoutFlows needs the module" yes "$(printf '%s' "$out" | grep -q 'module is required' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":16,"method":"tools/call","params":{"name":"layoutFlows","arguments":{"module":"X","dryRun":true}}}')
check "layoutFlows fails cleanly outside Service Studio" yes "$(printf '%s' "$out" | grep -q '"isError":true' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"downloadModule","arguments":{}}}')
check "missing required argument is a tool error" yes "$(printf '%s' "$out" | grep -q 'module is required' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"downloadModule","arguments":{"module":"X","outputDir":"relative/dir"}}}')
check "relative outputDir refused" yes "$(printf '%s' "$out" | grep -q 'outputDir must be an absolute path' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"downloadModule","arguments":{"module":"X","version":"latest-mine-unpublished","outputDir":"/tmp"}}}')
check "named shortcuts beyond published/latest are gone" yes "$(printf '%s' "$out" | grep -q '"isError":true' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"listModules","arguments":{"limit":"lots"}}}')
check "non-numeric limit refused" yes "$(printf '%s' "$out" | grep -q 'limit must be a whole number' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"getConsumers","arguments":{"module":"X","limit":"lots"}}}')
check "getConsumers takes a limit" yes "$(printf '%s' "$out" | grep -q 'limit must be a whole number' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":18,"method":"tools/call","params":{"name":"getElementConsumers","arguments":{}}}')
check "getElementConsumers needs the element" yes "$(printf '%s' "$out" | grep -q 'element is required' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":19,"method":"tools/call","params":{"name":"getElementConsumers","arguments":{"element":"Y"}}}')
check "getElementConsumers fails cleanly outside Service Studio" yes "$(printf '%s' "$out" | grep -q '"isError":true' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":60,"method":"tools/call","params":{"name":"getElementConsumers","arguments":{"element":"Y","part":"Z","partKind":"Widget"}}}')
check "getElementConsumers refuses an unknown partKind" yes "$(printf '%s' "$out" | grep -q 'partKind is Record, Attribute or Action' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":61,"method":"tools/call","params":{"name":"getElementConsumers","arguments":{"element":"Y","partKind":"Record"}}}')
check "getElementConsumers partKind needs a part" yes "$(printf '%s' "$out" | grep -q 'partKind needs a part' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":62,"method":"tools/call","params":{"name":"getElementConsumers","arguments":{"element":"Y","part":"Z","countUsages":false}}}')
check "getElementConsumers part always counts usages" yes "$(printf '%s' "$out" | grep -q 'part always counts usages' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":63,"method":"tools/call","params":{"name":"getElementConsumers","arguments":{"element":"Y","countUsages":"maybe"}}}')
check "getElementConsumers countUsages is a flag" yes "$(printf '%s' "$out" | grep -q 'countUsages must be true or false' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":64,"method":"tools/call","params":{"name":"getElementConsumers","arguments":{"job":"nosuchjob"}}}')
check "getElementConsumers reports an unknown job" yes "$(printf '%s' "$out" | grep -q 'No search job nosuchjob' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":20,"method":"tools/call","params":{"name":"createApplication","arguments":{"name":"X"}}}')
check "createApplication needs the kind" yes "$(printf '%s' "$out" | grep -q 'kind is required' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"createApplication","arguments":{"name":"X","kind":"Blue"}}}')
check "createApplication refuses an unknown kind" yes "$(printf '%s' "$out" | grep -q 'kind must be one of' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":22,"method":"tools/call","params":{"name":"createApplication","arguments":{"name":"X","kind":"Reactive","primaryColor":"red"}}}')
check "createApplication refuses a colour that isn't #RRGGBB" yes "$(printf '%s' "$out" | grep -q 'primaryColor must be a #RRGGBB' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":23,"method":"tools/call","params":{"name":"createApplication","arguments":{"name":"X","kind":"Reactive","icon":"icon.png"}}}')
check "createApplication refuses a relative icon path" yes "$(printf '%s' "$out" | grep -q 'icon must be an absolute path' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":24,"method":"tools/call","params":{"name":"updateApplication","arguments":{"application":"X"}}}')
check "updateApplication needs a change" yes "$(printf '%s' "$out" | grep -q 'Pass at least one of' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":25,"method":"tools/call","params":{"name":"updateApplication","arguments":{"application":"X","kind":"Reactive"}}}')
check "updateApplication converts an application only to Service" yes "$(printf '%s' "$out" | grep -q 'only to Service' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":26,"method":"tools/call","params":{"name":"createModule","arguments":{"application":"X"}}}')
check "createModule needs the name" yes "$(printf '%s' "$out" | grep -q 'name is required' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":27,"method":"tools/call","params":{"name":"convertModule","arguments":{"module":"X","to":"Reactive"}}}')
check "convertModule doesn't convert to Reactive" yes "$(printf '%s' "$out" | grep -q "convert to Reactive" && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":28,"method":"tools/call","params":{"name":"convertModule","arguments":{"module":"X","to":"Service"}}}')
check "convertModule fails cleanly outside Service Studio" yes "$(printf '%s' "$out" | grep -q '"isError":true' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":29,"method":"tools/call","params":{"name":"setReferenceTenantIdentifier","arguments":{"module":"X","producer":"Y","entity":"Z"}}}')
check "setReferenceTenantIdentifier needs the value" yes "$(printf '%s' "$out" | grep -q 'value is required' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":30,"method":"tools/call","params":{"name":"setReferenceTenantIdentifier","arguments":{"module":"X","producer":"Y","entity":"Z","value":"maybe"}}}')
check "setReferenceTenantIdentifier takes true or false" yes "$(printf '%s' "$out" | grep -q 'value must be true or false' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":31,"method":"tools/call","params":{"name":"setReferenceTenantIdentifier","arguments":{"module":"X","producer":"Y","entity":"Z","value":true}}}')
check "setReferenceTenantIdentifier fails cleanly outside Service Studio" yes "$(printf '%s' "$out" | grep -q '"isError":true' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"deleteModule","arguments":{}}}')
check "unknown tool -> -32602" yes "$(printf '%s' "$out" | grep -q '"code":-32602' && echo yes || echo no)"

out=$(rpc '{"jsonrpc":"2.0","id":8,"method":"resources/list"}')
check "unknown method -> -32601" yes "$(printf '%s' "$out" | grep -q '"code":-32601' && echo yes || echo no)"

check "bad JSON -> 400" 400 "$(status -X POST -H "Authorization: Bearer $token" -d '{not json' "$url")"

echo
[ "$failures" -eq 0 ] && echo "All checks passed." || { echo "$failures check(s) failed."; exit 1; }
