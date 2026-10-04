#!/usr/bin/env bash
# Builds an installable Android APK into dist/.
#
#   ./build-apk.sh            Release build (default)
#   ./build-apk.sh Debug      Debug build
#
# Without a keystore the APK is signed with the local debug key, which is fine for
# sideloading onto your own tablet. To sign with a real key, set:
#
#   ANDROID_KEYSTORE=path/to/release.keystore
#   ANDROID_KEY_ALIAS=mkbmixer
#   ANDROID_KEYSTORE_PASSWORD=...
#   ANDROID_KEY_PASSWORD=...     (defaults to the keystore password)
#
# Install the result with:  adb install -r dist/<apk>

set -euo pipefail

CONFIG="${1:-Release}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT/src/Mkb.Mixer.App.Android/Mkb.Mixer.App.Android.csproj"
DIST="$ROOT/dist"
PUBLISH="$ROOT/src/Mkb.Mixer.App.Android/bin/$CONFIG/net10.0-android/publish"

case "$CONFIG" in
  Release|Debug) ;;
  *) echo "usage: $0 [Release|Debug]" >&2; exit 2 ;;
esac

if ! dotnet workload list 2>/dev/null | grep -q '^android '; then
  echo "The .NET Android workload is missing. Install it with:" >&2
  echo "  dotnet workload install android" >&2
  exit 1
fi

args=(
  -c "$CONFIG"
  # A Debug build normally leaves the .NET assemblies out of the APK and pushes
  # them separately (fast deployment), so the APK would not run on its own.
  -p:EmbedAssembliesIntoApk=true
)

if [[ -n "${ANDROID_KEYSTORE:-}" ]]; then
  : "${ANDROID_KEY_ALIAS:?ANDROID_KEY_ALIAS must be set with ANDROID_KEYSTORE}"
  : "${ANDROID_KEYSTORE_PASSWORD:?ANDROID_KEYSTORE_PASSWORD must be set with ANDROID_KEYSTORE}"
  # Passwords go through env: references so they never appear on the command line.
  export MKB_KEYSTORE_PASS="$ANDROID_KEYSTORE_PASSWORD"
  export MKB_KEY_PASS="${ANDROID_KEY_PASSWORD:-$ANDROID_KEYSTORE_PASSWORD}"
  args+=(
    -p:AndroidKeyStore=true
    -p:AndroidSigningKeyStore="$(realpath "$ANDROID_KEYSTORE")"
    -p:AndroidSigningKeyAlias="$ANDROID_KEY_ALIAS"
    -p:AndroidSigningStorePass=env:MKB_KEYSTORE_PASS
    -p:AndroidSigningKeyPass=env:MKB_KEY_PASS
  )
  echo "Signing with $ANDROID_KEYSTORE ($ANDROID_KEY_ALIAS)"
else
  echo "No ANDROID_KEYSTORE set: signing with the local debug key"
fi

# Clear previous APKs. Otherwise a stale one can be picked up below, and an
# incremental build skips re-signing, so switching keys would silently not apply.
rm -rf "$PUBLISH"
rm -f "$(dirname "$PUBLISH")"/*.apk

echo "Building $CONFIG APK..."
dotnet publish "$PROJECT" "${args[@]}"

APK="$(find "$PUBLISH" -maxdepth 1 -name '*-Signed.apk' | head -n 1)"
if [[ -z "$APK" ]]; then
  echo "Build finished but no signed APK was found in $PUBLISH" >&2
  exit 1
fi

VERSION="$(sed -n 's:.*<ApplicationDisplayVersion>\(.*\)</ApplicationDisplayVersion>.*:\1:p' "$PROJECT")"
OUT="$DIST/mkb-mixer-${VERSION:-0}-$(echo "$CONFIG" | tr '[:upper:]' '[:lower:]').apk"

mkdir -p "$DIST"
cp "$APK" "$OUT"

echo
echo "APK: $OUT ($(du -h "$OUT" | cut -f1))"
echo "Install: adb install -r \"$OUT\""
