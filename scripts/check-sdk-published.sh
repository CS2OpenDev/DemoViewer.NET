#!/usr/bin/env bash
# Checks that the extension SDK and the app agree on CS2DemoKit, so neither ships a version the other
# does not declare.
#
#   scripts/check-sdk-published.sh [--report-only]     the latest SDK release on nuget.org
#   scripts/check-sdk-published.sh --packed <dir>      the .nupkg files in <dir>, before they are pushed
#
# The first form is release.yml's gate. It passes when the newest stable DemoViewer.NET.Extensions.Sdk
# on nuget.org, together with every other package scripts/sdk-packages.sh names at that same version,
# declares each CS2DemoKit package at the version Directory.Packages.props pins. That is the release an
# extension author restores today, so it is the one that has to load on this app. The SDK version at
# HEAD is not required to be published: an SDK-path change that leaves CS2DemoKit alone does not hold up
# an app release. nbgv can't decide it anyway on a v* tag, which is not one of the SDK's public refs.
#
# The second form is release-sdk.yml's check that a pack took its CS2DemoKit version from the central pin.
#
# --report-only prints the verdict and exits 0 whatever nuget.org says; a dry run wants that. Exit 1 is
# drift, exit 2 a usage error or a lookup that failed. DV_CS2DEMOKIT_PIN replaces the pin for every
# CS2DemoKit package, for testing the check itself; the run says so when it is set.
set -euo pipefail
cd "$(dirname "$0")/.."

REPORT_ONLY=0
PACKED_DIR=""
while [ $# -gt 0 ]; do
    case "$1" in
        --report-only) REPORT_ONLY=1; shift ;;
        --packed) PACKED_DIR="${2:?--packed needs a directory}"; shift 2 ;;
        -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
        *) echo "unexpected argument '$1'" >&2; exit 2 ;;
    esac
done

FLAT=https://api.nuget.org/v3-flatcontainer
SDK_ID=DemoViewer.NET.Extensions.Sdk

# Annotations on a runner, plain text everywhere else.
say() {
    local level="$1"; shift
    if [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::$level::$*"; else echo "$level: $*"; fi
}

# The version Directory.Packages.props pins for one package id, or nothing when it pins none.
pin_of() {
    if [ -n "${DV_CS2DEMOKIT_PIN:-}" ]; then
        echo "$DV_CS2DEMOKIT_PIN"; return
    fi
    local matches
    matches=$(grep -oE "PackageVersion Include=\"$1\" Version=\"[^\"]+\"" Directory.Packages.props || true)
    if [ "$(printf '%s' "$matches" | grep -c . || true)" -gt 1 ]; then
        echo "error: Directory.Packages.props pins $1 more than once" >&2
        exit 2
    fi
    printf '%s' "$matches" | sed -E 's/.*Version="([^"]+)".*/\1/'
}

# Prints "<id> <version>" for every CS2DemoKit dependency in a nuspec on stdin. The version is the
# range's lower bound: nuget writes a central pin as a bare minimum ("0.14.0"), an exact one as "[0.14.0]".
kit_deps() {
    grep -oE '<dependency [^>]*id="CS2DemoKit\.[^"]+"[^>]*>' \
        | sed -E 's/.*id="([^"]+)".*version="([^"]+)".*/\1 \2/; s/ [[(]?([^],)]*).*/ \1/' || true
}

# Checks one nuspec's text against the pins. Prints a line per dependency; returns 1 on a mismatch. The
# text is an argument, not stdin: a pipe would run this in a subshell and lose PARSER_SEEN.
check_nuspec() {
    local label="$1" deps id ver pin bad=0
    deps=$(printf '%s' "$2" | kit_deps)
    if [ -z "$deps" ]; then
        echo "  $label: no CS2DemoKit dependency"
        return 0
    fi
    while read -r id ver; do
        pin=$(pin_of "$id")
        if [ -z "$pin" ]; then
            echo "  $label: $id $ver, which Directory.Packages.props does not pin"
            bad=1
        elif [ "$ver" = "$pin" ]; then
            echo "  $label: $id $ver"
            PARSER_SEEN=$((PARSER_SEEN + $([ "$id" = CS2DemoKit.Parser ] && echo 1 || echo 0)))
        else
            echo "  $label: $id $ver, the pin is $pin"
            bad=1
        fi
    done <<< "$deps"
    return "$bad"
}

PIN=$(pin_of CS2DemoKit.Parser)
if [ -z "$PIN" ]; then
    echo "error: Directory.Packages.props has no CS2DemoKit.Parser pin" >&2
    exit 2
fi
if [ -n "${DV_CS2DEMOKIT_PIN:-}" ]; then
    say warning "DV_CS2DEMOKIT_PIN is set: checking against CS2DemoKit $PIN, not Directory.Packages.props"
fi
PARSER_SEEN=0

# ── Release-sdk mode: the packages this run is about to push ─────────────────────────────────────────
if [ -n "$PACKED_DIR" ]; then
    echo "[check-sdk] packed packages in $PACKED_DIR against CS2DemoKit $PIN"
    bad=0
    found=0
    for nupkg in "$PACKED_DIR"/*.nupkg; do
        [ -e "$nupkg" ] || continue
        found=1
        check_nuspec "$(basename "$nupkg")" "$(unzip -p "$nupkg" '*.nuspec')" || bad=1
    done
    if [ "$found" = 0 ]; then
        echo "error: no .nupkg in $PACKED_DIR" >&2
        exit 2
    fi
    if [ "$bad" = 1 ]; then
        say error "a packed SDK package declares a CS2DemoKit version other than Directory.Packages.props pins. The pack should take it from the central pin; look for a VersionOverride."
        exit 1
    fi
    if [ "$PARSER_SEEN" = 0 ]; then
        say error "no packed package declares CS2DemoKit.Parser, so there is nothing for release.yml's gate to match"
        exit 1
    fi
    echo "[check-sdk] ok"
    exit 0
fi

# ── Release mode: what nuget.org serves ──────────────────────────────────────────────────────────────
# The version the next sdk/v tag on this commit would carry, for the message only.
HEAD_SDK="<unknown>"
if dotnet nbgv --version >/dev/null 2>&1 || dotnet tool restore >/dev/null 2>&1; then
    HEAD_SDK=$(dotnet nbgv get-version -p src/Sdk/$SDK_ID -v SimpleVersion 2>/dev/null || echo "<unknown>")
fi

# A failed lookup is not a verdict on the SDK, so it gets its own exit code and message.
lookup_failed() {
    say error "$1"
    if [ "$REPORT_ONLY" = 1 ]; then exit 0; fi
    exit 2
}

fetch() {
    curl -fsS --retry 3 --max-time 30 "$1"
}

lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }

sdk_lc=$(lower "$SDK_ID")
if ! index=$(fetch "$FLAT/$sdk_lc/index.json" 2>&1); then
    lookup_failed "could not read $SDK_ID's version list from nuget.org: $index"
fi
# Stable versions only: release-sdk publishes from sdk/v tags, which are public refs, so a prerelease
# there is not a release anyone builds against.
LATEST=$(printf '%s' "$index" | grep -oE '"[0-9]+\.[0-9]+\.[0-9]+"' | tr -d '"' | sort -V | tail -1)
if [ -z "$LATEST" ]; then
    lookup_failed "nuget.org lists no stable $SDK_ID version"
fi

echo "[check-sdk] pin: CS2DemoKit $PIN; SDK at HEAD: $HEAD_SDK; latest SDK on nuget.org: $LATEST"

DIRS=$(bash scripts/sdk-packages.sh)
bad=0
for dir in $DIRS; do
    id=$(grep -oE '<PackageId>[^<]+' "$dir"/*.csproj | sed 's/.*<PackageId>//' || true)
    [ -n "$id" ] || id=$(basename "$dir")
    id_lc=$(lower "$id")
    if ! nuspec=$(fetch "$FLAT/$id_lc/$LATEST/$id_lc.nuspec" 2>&1); then
        case "$nuspec" in
            *404*) echo "  $id: not published at $LATEST"; bad=1; continue ;;
            *) lookup_failed "could not read $id $LATEST's nuspec from nuget.org: $nuspec" ;;
        esac
    fi
    check_nuspec "$id $LATEST" "$nuspec" || bad=1
done

if [ "$bad" = 0 ] && [ "$PARSER_SEEN" = 0 ]; then
    echo "  no package in SDK $LATEST declares CS2DemoKit.Parser"
    bad=1
fi

if [ "$bad" = 0 ]; then
    if [ "$HEAD_SDK" != "$LATEST" ]; then
        echo "[check-sdk] note: the SDK at HEAD ($HEAD_SDK) is not published. It declares the same CS2DemoKit, so an app release can go ahead without it."
    fi
    echo "[check-sdk] ok: SDK $LATEST on nuget.org declares CS2DemoKit $PIN, the version this commit pins"
    exit 0
fi

DECLARED=$(fetch "$FLAT/$sdk_lc/$LATEST/$sdk_lc.nuspec" 2>/dev/null | kit_deps | awk '$1 == "CS2DemoKit.Parser" { print $2 }' || true)
target="sdk/v$HEAD_SDK"
if [ "$HEAD_SDK" = "<unknown>" ] || [ "$HEAD_SDK" = "$LATEST" ]; then target="the SDK from this commit"; fi
msg="publish $target first; nuget.org's latest SDK ($LATEST) declares CS2DemoKit ${DECLARED:-<none>}, this commit pins $PIN"
if [ "$REPORT_ONLY" = 1 ]; then
    say warning "$msg (dry run, not failing)"
    exit 0
fi
say error "$msg"
exit 1
