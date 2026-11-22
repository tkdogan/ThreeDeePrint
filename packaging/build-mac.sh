#!/bin/bash
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
yak="/Applications/Rhino 8.app/Contents/Resources/bin/yak"
dotnet build "$root/Plugin/ThreeDeePrint.csproj" --configuration Release "$@"
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
build="$root/Plugin/bin/Release/net7.0"
cp "$build"/{ThreeDeePrint.rhp,Go2Bed.Grasshopper.gha,Go2Bed.Core.dll,Clipper2Lib.dll,libThreeDeeSolid.dylib,Manifold-LICENSE.txt,Manifold-AUTHORS.txt} "$stage/"
cp "$root/Plugin/obj/Release/net7.0/mac-package/ThreeDeePrint.rhp/License.txt" "$stage/Clipper2-LICENSE.txt"
cp "$root/packaging/manifest.yml" "$stage/"
cp "$root/brand/go2bed.png" "$stage/icon.png"
cd "$stage"
"$yak" build --platform mac
cp ./*.yak "$root/dist/"
