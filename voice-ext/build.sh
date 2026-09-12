#!/usr/bin/env bash
# Build Oxide.Ext.RustQuestsVoice.dll against the live server's assemblies.
# Usage: ./build.sh [path-to-RustDedicated_Data/Managed] [output-dir]
set -euo pipefail
cd "$(dirname "$0")"

MANAGED="${1:?usage: build.sh <path-to-RustDedicated_Data/Managed> [output-dir]}"
OUT="${2:-.}"
CSC="/c/Program Files/dotnet/sdk/8.0.420/Roslyn/bincore/csc.dll"
DOTNET="/c/Program Files/dotnet/dotnet.exe"

RSP="$(mktemp)"
trap 'rm -f "$RSP" files.txt' EXIT
for f in "$MANAGED"/*.dll; do
    b="$(basename "$f")"
    # Oxide.References duplicates Newtonsoft — every JsonProperty goes ambiguous with it in.
    [ "$b" = "Oxide.References.dll" ] && continue
    echo "-r:\"$(cygpath -w "$f")\"" >> "$RSP"
done
find vendor src -name '*.cs' > files.txt

"$DOTNET" "$CSC" -nologo -nostdlib -noconfig -target:library -optimize+ -warnaserror \
    -out:"$(cygpath -w "$OUT/Oxide.Ext.RustQuestsVoice.dll")" @"$(cygpath -w "$RSP")" @files.txt

echo "Built $OUT/Oxide.Ext.RustQuestsVoice.dll"
