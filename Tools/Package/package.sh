#!/bin/sh
# Builds BeaverBuddies (the Steam build, as always) and zips the installed
# mod folder to ~/Downloads, named with the time it was built to the minute:
# BeaverBuddies-2026-10-06-11h02.zip. Pass another folder to zip into it.
set -e
repo="$(cd "$(dirname "$0")/../.." && pwd)"
mods="$HOME/Documents/Timberborn/Mods"
out="${1:-$HOME/Downloads}"
dotnet build "$repo/BeaverBuddies/BeaverBuddies.csproj" -c Release -nologo -v q
zip="$out/BeaverBuddies-$(date +%Y-%m-%d-%Hh%M).zip"
rm -f "$zip"
(cd "$mods" && zip -qr "$zip" BeaverBuddies -x "*.DS_Store")
echo "$zip"
