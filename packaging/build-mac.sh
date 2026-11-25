#!/bin/bash
# Builds the Mac installers into dist/:
#   Go2Bed.macrhi              (drag-and-drop installer, from the Release build)
#   go2bed-<version>-*-mac.yak (Package Manager package)
# Extra arguments go to dotnet build, e.g. -p:YakExe=/path/to/yak
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
dotnet build "$root/Plugin/Go2Bed.csproj" --configuration Release -t:Yak "$@"
ls -l "$root/dist"
