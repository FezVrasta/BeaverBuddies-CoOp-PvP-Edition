#!/usr/bin/env bash
# Gets a fresh checkout of BeaverBuddies ready to work in, for a cloud
# environment or a new machine: env.props, the .NET packages for the projects
# in the repo, and a check that the translations script runs.
#
#   Tools/setup.sh
#
# Runs from any directory. Steps that can't work here (no dotnet, no Timberborn
# install) say so and are skipped rather than failing the run; see README.md and
# Tools/Tests/README.md for what each one is for.
#
# Environment:
#   BB_SKIP_DOTNET=1   don't restore the .NET projects
set -euo pipefail

# The repo root, whatever the caller's cwd is (/home/user in a cloud environment).
# Not git rev-parse: it refuses a clone owned by another user ("dubious
# ownership"), which is what a container's root sees.
cd "$(dirname "${BASH_SOURCE[0]}")/.."
REPO="$PWD"
echo "==> BeaverBuddies setup in $REPO"

# --- env.props: the mod's build needs one, even to point at nothing real ------
if [ -f BeaverBuddies/env.props ]; then
    echo "==> env.props: already there, left alone"
else
    case "$(uname -s)" in
        Darwin | Linux) TEMPLATE=BeaverBuddies/env.props.unix-template ;;
        *) TEMPLATE=BeaverBuddies/env.props.windows-template ;;
    esac
    cp "$TEMPLATE" BeaverBuddies/env.props
    echo "==> env.props: copied from $(basename "$TEMPLATE") (point it at your Timberborn install before building the mod)"
fi

# --- .NET: TimberNet, FakePlayer, Inspector, and the mod's packages ----------
# TimberNet and FakePlayer build without the game. The mod itself can only
# build against a real Timberborn install (its CheckEnv target errors out
# without one), so this restores packages and leaves building to
# Tools/Tests/fast.sh on a machine that has the game. Inspector targets
# netcoreapp3.1, which newer SDKs restore but can't run.
if [ "${BB_SKIP_DOTNET:-}" = "1" ]; then
    echo "==> .NET: SKIPPED (BB_SKIP_DOTNET=1)"
elif ! command -v dotnet > /dev/null; then
    echo "==> .NET: SKIPPED, no dotnet SDK on this machine (needed for TimberNet, FakePlayer and the mod build)"
else
    echo "==> .NET: dotnet $(dotnet --version)"
    dotnet build TimberNet/TimberNet.csproj -nologo -v q > /dev/null \
        && echo "    TimberNet builds" \
        || echo "    WARNING: TimberNet doesn't build ('dotnet build TimberNet/TimberNet.csproj' failed)"
    dotnet restore Tools/FakePlayer/FakePlayer.csproj -v q \
        && echo "    FakePlayer restored" \
        || echo "    WARNING: FakePlayer NOT restored ('dotnet restore Tools/FakePlayer/FakePlayer.csproj' failed)"
    dotnet restore Inspector/Inspector.csproj -v q > /dev/null 2>&1 \
        && echo "    Inspector restored" \
        || echo "    Inspector NOT restored (it targets netcoreapp3.1)"
    dotnet restore BeaverBuddies/BeaverBuddies.csproj -v q > /dev/null 2>&1 \
        && echo "    mod packages restored" \
        || echo "    mod packages NOT restored (expected without a Timberborn install)"
fi

# --- Python: Tools/Tests/translations.py --------------------------------------
# Standard library only, so there's no venv or requirements file: this just
# checks the interpreter is there and the script runs.
if ! command -v python3 > /dev/null; then
    echo "==> Python: SKIPPED, no python3 on this machine (Tools/Tests/translations.py needs it)"
else
    echo "==> Python: python3 $(python3 -c 'import platform; print(platform.python_version())')"
    python3 Tools/Tests/translations.py > /dev/null \
        && echo "    translations check passes" \
        || echo "    WARNING: 'python3 Tools/Tests/translations.py' failed"
fi

echo "==> Setup done. Fast checks: Tools/Tests/fast.sh (needs dotnet and a Timberborn install for the build steps)."
