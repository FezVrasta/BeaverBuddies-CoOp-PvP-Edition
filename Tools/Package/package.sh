#!/bin/sh
# Builds BeaverBuddies (the Steam build, as always) and zips the installed
# mod folder to ~/Downloads, named with the time it was built to the minute:
# BeaverBuddies-2026-10-06-11h02.zip. Pass another folder to zip into it.
# The older BeaverBuddies zips there are deleted, so only the newest is left.
set -e
repo="$(cd "$(dirname "$0")/../.." && pwd)"
mods="$HOME/Documents/Timberborn/Mods"
out="${1:-$HOME/Downloads}"
dotnet build "$repo/BeaverBuddies/BeaverBuddies.csproj" -c Release -nologo -v q
zip="$out/BeaverBuddies-$(date +%Y-%m-%d-%Hh%M).zip"
rm -f "$zip"
(cd "$mods" && zip -qr "$zip" BeaverBuddies -x "*.DS_Store")
for old in "$out"/BeaverBuddies-[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]-[0-9][0-9]h[0-9][0-9].zip; do
    if [ -e "$old" ] && [ "$old" != "$zip" ]; then rm -f "$old"; fi
done
echo "$zip"
