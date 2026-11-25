#!/bin/bash
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
yak="/Applications/Rhino 8.app/Contents/Resources/bin/yak"
dotnet build "$root/Plugin/Go2Bed.csproj" --configuration Release "$@"
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
build="$root/Plugin/bin/Release/net7.0"
cp "$build"/{Go2Bed.rhp,Go2Bed.rui,Go2Bed.Grasshopper.gha,Go2Bed.Core.dll,Clipper2Lib.dll} "$stage/"
cp "$root/Plugin/obj/Release/net7.0/go2bed-mac-package/Go2Bed.rhp/License.txt" "$stage/Clipper2-LICENSE.txt"
cp "$root/packaging/manifest.yml" "$stage/"
cp "$root/brand/go2bed.png" "$stage/icon.png"
cd "$stage"
"$yak" build --platform mac
cp ./*.yak "$root/dist/"
