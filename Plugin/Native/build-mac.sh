#!/bin/sh
# Rebuild the vendored universal macOS boolean backend from its pinned release.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
url=https://files.pythonhosted.org/packages/9e/f0/6884cbd1f12f80345eff5e78a94eb5c606d45ee37d9be454ab9547557fa6/manifold3d-3.5.5.tar.gz
curl -fsSL "$url" -o "$work/src.tar.gz"
(cd "$work" && echo 'c506df413e749c80d323998c3ec7e2cb6e29e1a97b7d73e8740ddfb3490aa64f  src.tar.gz' | shasum -a 256 -c -)
tar -xzf "$work/src.tar.gz" -C "$work"
src="$work/manifold3d-3.5.5"
cmake -S "$src" -B "$work/build" -DMANIFOLD_CROSS_SECTION=OFF -DMANIFOLD_TEST=OFF -DMANIFOLD_PYBIND=OFF -DMANIFOLD_CBIND=OFF -DBUILD_SHARED_LIBS=OFF -DCMAKE_BUILD_TYPE=Release '-DCMAKE_OSX_ARCHITECTURES=arm64;x86_64' -DCMAKE_OSX_DEPLOYMENT_TARGET=12.0
cmake --build "$work/build" --parallel
clang++ -std=c++17 -O2 -arch arm64 -arch x86_64 -mmacosx-version-min=12.0 -dynamiclib -I"$src/include" -I"$work/build/include" "$here/solid.cpp" "$work/build/src/libmanifold.a" -Wl,-install_name,@rpath/libThreeDeeSolid.dylib -o "$here/libThreeDeeSolid.dylib"
codesign -s - -f "$here/libThreeDeeSolid.dylib"
