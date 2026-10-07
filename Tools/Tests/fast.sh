#!/bin/bash
# The checks that need no game, for BeaverBuddies: it builds (the Steam build, and the one without Steam
# networking and matchmaking, so code that should compile either way is caught), into a scratch folder
# so nothing in the Mods folder changes. Seconds. (See README.md.)
#
#   Tools/Tests/fast.sh
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
SCRATCH="${TMPDIR:-/tmp}/bb-fast-docs"
mkdir -p "$SCRATCH/Timberborn/Mods"
FAILED=0

step() { # name command...
    local name=$1 out
    shift
    if out=$("$@" 2>&1); then echo "  ok    $name"; else echo "  FAIL  $name"; echo "$out" | tail -15 | sed 's/^/        /'; FAILED=$((FAILED + 1)); fi
}

echo "BeaverBuddies, fast checks"
step "builds (Steam)" bash -c "cd '$REPO/BeaverBuddies' && dotnet build -c Release -nologo -v q -p:DocumentsPath='$SCRATCH/' 2>&1 | tee /dev/stderr | grep -q '0 Error(s)'"
step "builds without Steam networking and matchmaking" bash -c "cd '$REPO/BeaverBuddies' && dotnet build -c Release -nologo -v q -p:Steam=false -p:DocumentsPath='$SCRATCH/' 2>&1 | tee /dev/stderr | grep -q '0 Error(s)'"
# The Steam build again, so what's left in bin and obj is the one that ships
step "builds (Steam) again" bash -c "cd '$REPO/BeaverBuddies' && dotnet build -c Release -nologo -v q -p:DocumentsPath='$SCRATCH/' 2>&1 | tee /dev/stderr | grep -q '0 Error(s)'"
echo
[ $FAILED -eq 0 ] && echo "All fast checks passed." || echo "$FAILED failed."
exit $((FAILED > 0))
