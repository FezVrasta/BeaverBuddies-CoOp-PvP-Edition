#!/bin/bash
# Uploads the zip release.sh made to the mod's page on mod.io, with the
# newest changelog entry as the change note:
#
#   Tools/Package/upload-modio.sh [--dry-run]
#
# Needs an OAuth2 access token with write access in ~/.config/timberborn-mod-uploader/modio-token
# (an API key alone can only read). upload.sh runs this after the Workshop upload.
set -euo pipefail

# ---- This repo ---------------------------------------------------------------
game=3659
mod=6446808
prefix="BeaverBuddies"
changelog="BeaverBuddies/changelog.txt"
# -----------------------------------------------------------------------------

repo="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$repo"
tokenfile="$HOME/.config/timberborn-mod-uploader/modio-token"
dry=0
for a in "$@"; do
    case "$a" in
        --dry-run) dry=1 ;;
        *) echo "unknown option $a" >&2; exit 1 ;;
    esac
done

die() { echo "$*" >&2; exit 1; }
[ -s "$tokenfile" ] || die "no mod.io token in $tokenfile"
version=$(sed -n '1s/^v//p' "$changelog")
note=$(sed -n '2,/^$/p' "$changelog" | sed '/^$/d')
[ -n "$version" ] && [ -n "$note" ] || die "no entry at the top of $changelog"
zip=$(ls -t "$HOME"/Downloads/$prefix-[0-9]*.zip 2>/dev/null | head -1)
[ -n "$zip" ] || die "no $prefix zip in Downloads: run release.sh first"
have=$(unzip -p "$zip" "$prefix/version-*/version.txt" 2>/dev/null | tr -d '[:space:]')
[ "$have" = "$version" ] || die "$zip holds ${have:-no version.txt}, the changelog is at $version: run release.sh first"

echo "uploading $(basename "$zip") to mod.io as $have with:"
printf '%s\n' "$note"
[ "$dry" = 0 ] || { echo "(dry run: nothing uploaded)"; exit 0; }

# The token goes in a config file, not on the command line where ps shows it
cfg=$(mktemp); trap 'rm -f "$cfg"' EXIT
printf 'header = "Authorization: Bearer %s"\n' "$(cat "$tokenfile")" > "$cfg"
resp=$(curl -sS -K "$cfg" -w '\n%{http_code}' "https://g-$game.modapi.io/v1/games/$game/mods/$mod/files" \
    -F "filedata=@$zip" -F "version=$have" -F "changelog=$note" -F "active=true")
code=${resp##*$'\n'}
body=${resp%$'\n'*}
if [ "$code" = 201 ]; then
    echo "uploaded to mod.io: file $(printf '%s' "$body" | sed -nE 's/.*"id": *([0-9]+).*/\1/p' | head -1)"
else
    die "mod.io said $code: $(printf '%s' "$body" | sed -nE 's/.*"message": *"([^"]*)".*/\1/p' | head -1)"
fi
