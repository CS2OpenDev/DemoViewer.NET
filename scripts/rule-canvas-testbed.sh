#!/usr/bin/env bash
# Runs the desktop app with the Rule Canvas testbed fixtures as the user rules directory, so the canvas
# picker lists them as editable rulesets labelled "testbed" beside the shipped set.
#
#   scripts/rule-canvas-testbed.sh [--reset] [--no-build] [--prepare] [--dir <path>]
#
#   --reset     wipe the scratch directory and copy every fixture again, discarding edits made in the app
#   --no-build  run the last Release build of the desktop app without building it
#   --prepare   copy the fixtures and print the environment line to launch any build yourself, without running
#   --dir       the scratch directory (default $DV_TESTBED_DIR, else
#               $HOME/.cache/demoviewer-rule-canvas-testbed/rules); refused when it is the fixture
#               directory or already holds files this script did not put there
#
# The fixtures live in src/Extensions/RuleCanvas/testbed/rules and are never edited in place. A plain run
# copies only the fixtures missing from the scratch directory, so edits survive a relaunch.
#
# While the app runs this way its analysis reads the testbed instead of your own user rules, and the
# fixtures' highlights change the highlight fingerprint, so the library may re-scan demos once now and
# again on the next normal launch.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FIXTURES="$ROOT/src/Extensions/RuleCanvas/testbed/rules"
DIR="${DV_TESTBED_DIR:-$HOME/.cache/demoviewer-rule-canvas-testbed/rules}"
RESET=0
NO_BUILD=0
PREPARE=0

while [ $# -gt 0 ]; do
    case "$1" in
        --reset) RESET=1; shift ;;
        --no-build) NO_BUILD=1; shift ;;
        --prepare) PREPARE=1; shift ;;
        --dir)
            [ $# -ge 2 ] || { echo "--dir needs a path" >&2; exit 2; }
            DIR="$2"; shift 2 ;;
        -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
        *) echo "unknown argument '$1'" >&2
           echo "usage: $0 [--reset] [--no-build] [--prepare] [--dir <path>]" >&2
           exit 2 ;;
    esac
done

# --reset deletes the directory, so it must be one this script made: never the fixtures themselves and
# never a folder that already held something else, such as real user rules.
MARKER=".rule-canvas-testbed"
if [ -d "$DIR" ]; then
    if [ "$(cd "$DIR" && pwd -P)" = "$(cd "$FIXTURES" && pwd -P)" ]; then
        echo "refusing: $DIR is the fixture directory itself; pick a scratch directory" >&2
        exit 2
    fi
    if [ ! -e "$DIR/$MARKER" ] && [ -n "$(ls -A "$DIR")" ]; then
        echo "refusing: $DIR already holds files this script did not put there" >&2
        exit 2
    fi
fi

if [ "$RESET" -eq 1 ] && [ -d "$DIR" ]; then
    rm -rf "$DIR"
fi
mkdir -p "$DIR"
touch "$DIR/$MARKER"

for fixture in "$FIXTURES"/*.rules.yaml; do
    target="$DIR/$(basename "$fixture")"
    if [ ! -e "$target" ]; then
        cp "$fixture" "$target"
    fi
done

echo "testbed rules: $DIR"
for file in "$DIR"/*.rules.yaml; do
    name="$(basename "$file" .rules.yaml)"
    if [ -e "$FIXTURES/$name.rules.yaml" ] && ! cmp -s "$file" "$FIXTURES/$name.rules.yaml"; then
        echo "  $name (edited; --reset restores it)"
    else
        echo "  $name"
    fi
done
echo "your own user rules are not read while this runs"

if [ "$PREPARE" -eq 1 ]; then
    echo "launch any build with:"
    echo "  CS2DEMOKIT_USER_RULES_DIR=\"$DIR\" artifacts/bin/DemoViewer.NET.Desktop/debug/DemoViewer.NET.Desktop"
    exit 0
fi

export CS2DEMOKIT_USER_RULES_DIR="$DIR"
if [ "$NO_BUILD" -eq 1 ]; then
    exec dotnet run --project "$ROOT/src/App/DemoViewer.NET.Desktop" -c Release --no-build
fi
exec dotnet run --project "$ROOT/src/App/DemoViewer.NET.Desktop" -c Release
