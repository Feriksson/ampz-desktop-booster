#!/usr/bin/env bash
# Rebuilds the patched VirtualDesktopAccessor.dll and copies it to the repo root.
# Requires: git, Rust (rustup, x86_64-pc-windows-msvc) and the MSVC C++ linker (VS Build Tools).
# See README.md in this folder for why the DLL is patched.
set -euo pipefail

UPSTREAM="https://github.com/Ciantic/VirtualDesktopAccessor.git"
COMMIT="bbed6768711cbda12bdb4e93e00a012f3748ee74"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

git clone --quiet "$UPSTREAM" "$WORK/vda"
git -C "$WORK/vda" checkout --quiet "$COMMIT"
git -C "$WORK/vda" apply --ignore-whitespace "$HERE/fix-hstring-double-free.patch"

(cd "$WORK/vda" && cargo build --release -p dll)

cp "$WORK/vda/target/release/VirtualDesktopAccessor.dll" "$REPO_ROOT/VirtualDesktopAccessor.dll"
echo "OK -> $REPO_ROOT/VirtualDesktopAccessor.dll"
