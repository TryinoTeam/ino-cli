#!/bin/sh
# MCP/CLI P0a.5: builds the ino CLI as one self-contained, compressed file per platform, packs it and writes SHA256SUMS.
# macOS: when a "Developer ID Application" identity is in the keychain, the osx binaries are signed (hardened runtime, macos.entitlements) and
# notarized with the notarytool profile NOTARY_PROFILE (default ino-notary: `xcrun notarytool store-credentials ino-notary`). Without the identity
# they stay unsigned and the script says so. Windows signing (Azure Trusted Signing) and publishing (GitHub release, Homebrew tap, winget, npm) are
# separate steps.
#   ./publish.sh [out-dir]          default out-dir: ./dist      SIGN=0 skips macOS signing.
set -eu
cd "$(dirname "$0")"
OUT="${1:-dist}"
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
RIDS="osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64 win-arm64"
rm -rf "$OUT" && mkdir -p "$OUT"
NOTARY_PROFILE="${NOTARY_PROFILE:-ino-notary}"
MAC_ID=""
if [ "${SIGN:-1}" = 1 ] && command -v security >/dev/null 2>&1; then
  # By its SHA-1 fingerprint: the certificate's name has non-ASCII letters, which codesign does not match reliably.
  MAC_ID=$(security find-identity -v -p codesigning 2>/dev/null | awk '/"Developer ID Application:/ {print $2; exit}')
fi
[ -n "$MAC_ID" ] && echo "macOS: signing with Developer ID $MAC_ID, notarizing with profile $NOTARY_PROFILE" || echo "macOS: NOT signed (no Developer ID Application identity, or SIGN=0)"
for rid in $RIDS; do
  echo "== $rid"
  dotnet publish src/Ino.Cli -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none \
    -p:ContinuousIntegrationBuild=true -o "$OUT/build/$rid" -v q
  name="ino-$VERSION-$rid"
  if [ -n "$MAC_ID" ] && [ "${rid#osx-}" != "$rid" ]; then
    codesign --force --options runtime --timestamp --entitlements macos.entitlements --sign "$MAC_ID" "$OUT/build/$rid/ino" || {
      echo "STOP: codesign could not use the Developer ID key. Run this once in Terminal at the Mac and choose \"Always Allow\" when macOS asks:"
      echo "  codesign --force --sign $MAC_ID --options runtime /tmp/ino-sign-check && echo ok   (after: cp \"\$(command -v true)\" /tmp/ino-sign-check)"
      exit 1; }
    codesign --verify --strict --verbose=1 "$OUT/build/$rid/ino"
    # A bare binary cannot be stapled: Apple keeps the ticket and Gatekeeper checks it online on first run.
    ditto -c -k "$OUT/build/$rid/ino" "$OUT/build/$rid.notary.zip"
    xcrun notarytool submit "$OUT/build/$rid.notary.zip" --keychain-profile "$NOTARY_PROFILE" --wait | tee "$OUT/build/$rid.notary.log"
    grep -q "status: Accepted" "$OUT/build/$rid.notary.log" || { echo "STOP: Apple did not accept $rid (see xcrun notarytool log)"; exit 1; }
  fi
  case "$rid" in
    win-*) (cd "$OUT/build/$rid" && zip -q -X "../../$name.zip" ino.exe) ;;
    *)     COPYFILE_DISABLE=1 tar --no-xattrs -C "$OUT/build/$rid" -czf "$OUT/$name.tar.gz" ino ;;   # no macOS extended attributes in the archive
  esac
done
rm -rf "$OUT/build"
(cd "$OUT" && shasum -a 256 ino-* > SHA256SUMS)
echo "== $OUT"; cat "$OUT/SHA256SUMS"
