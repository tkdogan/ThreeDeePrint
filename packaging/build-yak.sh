#!/bin/bash
# Builds the Package Manager package into dist/: go2bed-<version>-rh7_13-any.yak.
# One package serves Rhino 7, 8 and 9 on Windows and Mac.
# Extra arguments go to dotnet build, e.g. -p:YakExe=/path/to/yak
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
dotnet build "$root/Plugin/Go2Bed.csproj" --configuration Release -t:Yak "$@"
ls -l "$root/dist"
