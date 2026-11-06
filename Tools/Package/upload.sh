#!/bin/bash
# Uploads the mod installed in Mods to its Steam Workshop item, with the
# newest changelog entry as the change note:
#
#   Tools/Package/upload.sh [--dry-run]
#
# Goes through the Mod Uploader mod (~/Developer/TimberbornModUploader, it has
# to be in Mods): it starts a copy of Timberborn of its own, next to any that's
# already open, waits for the upload at the main menu and closes that copy. Steam has to be running and signed in. The same request uploads the
# release zip to mod.io when there's a token for it. It refuses when the
# installed version (version.txt, the manifest only has x.y.0) isn't the one the
# changelog's newest entry is for: run
# release.sh first.
set -euo pipefail

# ---- This repo ---------------------------------------------------------------
id="BeaverBuddies"
modio_game=3659
modio_mod=6446808
changelog="BeaverBuddies/changelog.txt"
# -----------------------------------------------------------------------------

repo="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$repo"
timberborn="$HOME/Documents/Timberborn"
# The newest version-x.y folder holds the manifest
installed=$(ls -d "$timberborn/Mods/$id"/version-*/manifest.json 2>/dev/null | sort -V | tail -1)
flag="$timberborn/ModUploader-request.json"
result="$flag.result"

dry=0
for a in "$@"; do
    case "$a" in
        --dry-run) dry=1 ;;
        *) echo "unknown option $a" >&2; exit 1 ;;
    esac
done

die() { echo "$*" >&2; exit 1; }
# The newest entry: from its v<version> line to the first empty line
version=$(sed -n '1s/^v//p' "$changelog")
note=$(sed -n '2,/^$/p' "$changelog" | sed '/^$/d')
[ -n "$version" ] && [ -n "$note" ] || die "no entry at the top of $changelog"
have=$(head -1 "$(dirname "$installed")/version.txt" 2>/dev/null | tr -d '[:space:]')
[ "$have" = "$version" ] || die "Mods has $id ${have:-nothing}, the changelog is at $version: run release.sh first"

echo "uploading $id $version with:"
printf '%s\n' "$note"
[ "$dry" = 0 ] || { echo "(dry run: nothing uploaded)"; exit 0; }

rm -f "$result"
zip=$(ls -t "$HOME"/Downloads/$id-[0-9]*.zip 2>/dev/null | head -1 || true)
[ -s "$HOME/.config/timberborn-mod-uploader/modio-token" ] || zip=""
python3 - "$flag" "$id" "$note" "$zip" "$version" "$modio_game" "$modio_mod" <<'PY'
import json, sys
flag, mod, note, zip, version, game, modio = sys.argv[1:]
request = {"mod": mod, "changelog": note}
if zip:
    request["modio"] = {"game": int(game), "mod": int(modio), "zip": zip, "version": version}
json.dump(request, open(flag, "w"))
PY
pid=""
cleanup() {
    rm -f "$flag"
    [ -z "$pid" ] || kill "$pid" 2>/dev/null || true
}
trap cleanup EXIT
pgrep -x steam_osx >/dev/null || die "Steam isn't running"
before=$(pgrep -x Timberborn | sort || true)
echo "starting a copy of Timberborn..."
open -n -a "$HOME/Library/Application Support/Steam/steamapps/common/Timberborn/Timberborn.app" --env SteamAppId=1062090 --env SteamGameId=1062090 --args -skipModManager
for _ in $(seq 30); do
    pid=$(comm -13 <(echo "$before") <(pgrep -x Timberborn | sort) | head -1)
    [ -z "$pid" ] || break
    sleep 1
done
[ -n "$pid" ] || die "Timberborn didn't start"
waited=0
until [ -f "$result" ]; do
    [ "$waited" -lt 300 ] || die "no answer from Timberborn after 5 minutes: is Mod Uploader enabled in it?"
    sleep 1
    waited=$((waited + 1))
done
outcome=$(cat "$result")
rm -f "$result"
failed=0
while read -r target status rest; do
    case "$target $status" in
        "steam ok") echo "uploaded: https://steamcommunity.com/sharedfiles/filedetails/?id=$rest" ;;
        "modio ok") echo "uploaded to mod.io: file $rest" ;;
        *) echo "$target $status $rest" >&2; failed=1 ;;
    esac
done <<< "$outcome"
[ "$failed" = 0 ] || exit 1
