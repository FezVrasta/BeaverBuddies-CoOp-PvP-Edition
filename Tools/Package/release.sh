#!/bin/bash
# Releases a new version, ready for the game's own mod uploader:
#
#   Tools/Package/release.sh patch|minor|major [--dry-run] [--no-push]
#
# Bumps the version in the manifest and the csproj, drafts the changelog
# entry from the commit subjects since the last release tag and opens it in
# $VISUAL or $EDITOR (nano without either; used as drafted when there's no
# terminal), then builds, installs into Mods and zips through package.sh,
# commits "Release <version>", tags v<version> and pushes both. It prints
# the entry at the end, for the uploader's change note.
#
# Refuses to run off the release branch, with uncommitted changes, or behind
# the remote. --dry-run only says what it would do.
set -euo pipefail

# ---- This repo ---------------------------------------------------------------
name="BeaverBuddies"
branch="master"
remote="fork"  # FezVrasta/BeaverBuddies-CoOp-PvP-Edition, never origin (upstream)
manifest="BeaverBuddies/manifest.json"
csproj="BeaverBuddies/BeaverBuddies.csproj"
changelog="BeaverBuddies/changelog.txt"
# -----------------------------------------------------------------------------

repo="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$repo"

part="${1:-}"; shift || true
dry=0; push=1
for a in "$@"; do
    case "$a" in
        --dry-run) dry=1 ;;
        --no-push) push=0 ;;
        *) echo "unknown option $a" >&2; exit 1 ;;
    esac
done
case "$part" in patch|minor|major) ;; *) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 1 ;; esac

die() { echo "$*" >&2; exit 1; }
[ "$(git branch --show-current)" = "$branch" ] || die "not on $branch"
[ -z "$(git status --porcelain --untracked-files=no)" ] || die "uncommitted changes: commit or stash them first"
git fetch -q "$remote"
[ -z "$(git log --oneline "HEAD..$remote/$branch")" ] || die "$remote/$branch has commits this branch doesn't: pull first"

# The version is the mod's own semver, after the Timberborn version and a
# dash where there is one (1.1.0-1.2.0)
old=$(sed -nE 's/.*"Version": *"([^"]+)".*/\1/p' "$manifest" | head -1)
[ -n "$old" ] || die "no Version in $manifest"
if [[ "$old" == *-* ]]; then prefix="${old%-*}-"; semver="${old##*-}"; else prefix=""; semver="$old"; fi
IFS=. read -r ma mi pa <<< "$semver"
case "$part" in
    major) ma=$((ma + 1)); mi=0; pa=0 ;;
    minor) mi=$((mi + 1)); pa=0 ;;
    patch) pa=$((pa + 1)) ;;
esac
new="$prefix$ma.$mi.$pa"
git rev-parse -q --verify "refs/tags/v$new" >/dev/null && die "v$new is already tagged"

# What went in since the last release
last=$(git describe --tags --abbrev=0 --match 'v*' 2>/dev/null || true)
range="${last:+$last..}HEAD"
# A template both BSD and GNU mktemp take (GNU refuses -t without X's)
notes=$(mktemp "${TMPDIR:-/tmp}/$name-release.XXXXXX")
trap 'rm -f "$notes"' EXIT
{
    git log --reverse --no-merges --format='* %s.' "$range" | grep -vE '^\* Release ' || true
    echo "# $name $old -> $new, from ${last:-the first commit}. Edit this into the changelog entry:"
    echo "# one '* ' line each, for players. Lines starting with # are dropped; empty cancels."
} > "$notes"
[ -n "$(grep -v '^#' "$notes" | tr -d '[:space:]')" ] || die "nothing since $last to release"

echo "$name $old -> $new ($(git rev-list --count --no-merges "$range") commits since ${last:-the start})"
if [ "$dry" = 1 ]; then
    grep -v '^#' "$notes"
    echo "(dry run: nothing changed)"
    exit 0
fi
if [ -t 0 ] && [ -t 1 ]; then "${VISUAL:-${EDITOR:-nano}}" "$notes"; fi
entry=$(grep -v '^#' "$notes" | sed '/^[[:space:]]*$/d')
[ -n "$entry" ] || die "empty changelog entry: cancelled"

undo() {
    git checkout -q -- "$manifest" "$csproj" "$changelog" 2>/dev/null || true
    [ -n "$(git ls-files "$changelog")" ] || rm -f "$changelog"
    echo "release cancelled: the version and changelog are back to $old" >&2
}
trap 'rm -f "$notes"; [ "${done:-0}" = 1 ] || undo' EXIT

# -i with a suffix works on BSD and GNU sed alike ('' alone is BSD-only)
sed -i.bak -E "s/(\"Version\": *\")$old\"/\1$new\"/" "$manifest" && rm -f "$manifest.bak"
sed -i.bak "s|<Version>$old</Version>|<Version>$new</Version>|" "$csproj" && rm -f "$csproj.bak"
{ printf 'v%s\n%s\n\n' "$new" "$entry"; cat "$changelog" 2>/dev/null || true; } > "$changelog.new"
mv "$changelog.new" "$changelog"
grep -q "\"Version\": *\"$new\"" "$manifest" || die "couldn't set the version in $manifest"
grep -q "<Version>$new</Version>" "$csproj" || die "couldn't set the version in $csproj"

zip=$("$repo/Tools/Package/package.sh" | tail -1)

git add "$manifest" "$csproj" "$changelog"
git commit -q -m "Release $new"
git tag "v$new"
done=1
if [ "$push" = 1 ]; then
    git push -q "$remote" "$branch"
    git push -q "$remote" "v$new"
    echo "pushed $branch and v$new to $remote"
else
    echo "not pushed: git push $remote $branch v$new"
fi
echo "installed in Mods and zipped to $zip"
echo "change note for the uploader:"
echo
printf '%s\n' "$entry"
