#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
output="${1:-$PWD/artifacts/publish}"
g++ -O3 -shared -fPIC native/nexus_native.cpp -o native/libnexus_native.so
dotnet publish GeminiNexus.Server -c Release -r linux-x64 -p:PublishAot=true -p:StripSymbols=true -o "$output/server" --disable-build-servers
dotnet publish GeminiNexus.Client.Wasm -c Release -o "$output/client" --disable-build-servers
