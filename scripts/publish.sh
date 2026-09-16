#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
output="${1:-$PWD/artifacts/publish}"
dotnet publish GeminiNexus.Server -c Release -o "$output/server" --disable-build-servers
dotnet publish GeminiNexus.Client.Wasm -c Release -o "$output/client" --disable-build-servers
