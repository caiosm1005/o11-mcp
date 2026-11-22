#!/bin/sh
# Checks that the installed Service Studio still has every member the bridge's openModule and
# addDependency resolve, without starting Service Studio: it loads its assemblies next to the
# bridge's and prints the bridge's status Capabilities. Run it after a Service Studio update.
# Usage: bridge/tests/servicestudio-check.sh [Service Studio's assembly folder]
# Needs the .NET SDK. Touches nothing outside a temporary folder.
set -u

repo=$(cd "$(dirname "$0")/.." && pwd -P)
studio=${1:-/Applications/ServiceStudio.app/Contents/Resources/app/ServiceStudio.app/Contents/MacOS}
[ -f "$studio/ServiceStudio.Presenter.dll" ] || { echo "No Service Studio assemblies in $studio: pass the folder that has ServiceStudio.Presenter.dll."; exit 2; }
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

dotnet build "$repo/src/ServiceStudioBridge.csproj" -c Release -nologo -v q -o "$work/bridge" >/dev/null || exit 1
dotnet build "$repo/tests/servicestudio-check/ServiceStudioCheck.csproj" -c Release -nologo -v q -o "$work/check" >/dev/null || exit 1

version=$(plutil -extract CFBundleShortVersionString raw "$studio/../Info.plist" 2>/dev/null || echo unknown)
echo "Service Studio $version"
export SSBRIDGE_HOME="$work/home"   # the bridge's log and config stay in the temporary folder
mkdir -p "$SSBRIDGE_HOME"
if dotnet "$work/check/ServiceStudioCheck.dll" "$work/bridge" "$studio"; then
  echo "All capabilities resolve."
else
  echo "Some capabilities don't resolve in this Service Studio build: see above."
  exit 1
fi
