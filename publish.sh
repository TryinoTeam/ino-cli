#!/bin/sh
# MCP/CLI P0a.5: builds the ino CLI as one self-contained, compressed file per platform, packs it and writes SHA256SUMS.
# Signing (Apple notarization, Azure Trusted Signing) and publishing (GitHub release, Homebrew tap, winget, npm) are separate steps that need
# Tryino's organization accounts; this script does neither.
#   ./publish.sh [out-dir]          default out-dir: ./dist
set -eu
cd "$(dirname "$0")"
OUT="${1:-dist}"
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
RIDS="osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64 win-arm64"
rm -rf "$OUT" && mkdir -p "$OUT"
for rid in $RIDS; do
  echo "== $rid"
  dotnet publish src/Ino.Cli -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none \
    -p:ContinuousIntegrationBuild=true -o "$OUT/build/$rid" -v q
  name="ino-$VERSION-$rid"
  case "$rid" in
    win-*) (cd "$OUT/build/$rid" && zip -q -X "../../$name.zip" ino.exe) ;;
    *)     COPYFILE_DISABLE=1 tar --no-xattrs -C "$OUT/build/$rid" -czf "$OUT/$name.tar.gz" ino ;;   # no macOS extended attributes in the archive
  esac
done
rm -rf "$OUT/build"
(cd "$OUT" && shasum -a 256 ino-* > SHA256SUMS)
echo "== $OUT"; cat "$OUT/SHA256SUMS"
