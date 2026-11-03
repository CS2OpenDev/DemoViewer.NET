#!/bin/bash
# Wraps a self-contained publish into a Velopack install/auto-update package:
#   Windows → Setup.exe · macOS → .app + Setup.pkg · Linux → AppImage, each with delta updates.
#
#   ./scripts/pack-velopack.sh [RID]
#
#   RID   win-x64 | osx-arm64 | linux-x64  (default: this host's RID)
#
# vpk builds the installer for the HOST OS only — you cannot build a Windows Setup.exe on
# macOS. So run this on each platform (that's why CI is a tri-OS matrix). The RID
# must match the host OS family; a mismatch is rejected up front.
#
# Signing. Unset means unsigned. The DV_MAC_* names are read only for osx-arm64 and one of them on
# another RID is an error, so this script cannot hand a macOS secret to the Windows vpk. vpk also
# binds every signing option to a VPK_* variable it reads straight from the environment, which this
# script has no say over, so those names are refused outright.
#   macOS (osx-arm64), all of the first three or none:
#     DV_MAC_SIGN_APP_IDENTITY      "Developer ID Application: ..."   (--signAppIdentity)
#     DV_MAC_SIGN_INSTALL_IDENTITY  "Developer ID Installer: ..."     (--signInstallIdentity)
#     DV_MAC_NOTARY_PROFILE         notarytool keychain profile name   (--notaryProfile)
#     DV_MAC_KEYCHAIN               keychain holding both identities and the profile (--keychain)
#     DV_MAC_SIGN_ENTITLEMENTS      entitlements plist (--signEntitlements); vpk's .NET default if unset
#   Windows: none yet; Windows signing is deferred.
#   DV_REQUIRE_SIGNING=1          fail instead of packing unsigned. release.yml sets it wherever it signs.
#   DV_PACK_RETRIES               extra `vpk pack` attempts after a notarization network drop
#                                 (default 1, signed packs only; 0 disables).
#   DV_VPK_EXTRA_ARGS             escape hatch for anything not modeled
#
# Delta updates: vpk diffs against the *.nupkg files already in --outputDir, so keep a
# release dir warm (download the previous channel release into it in CI) to emit deltas.
#
# Beta: a version with a -betaNNNN label (v0.9.0-beta0001) packs into the <os>-beta channel
# instead of the default <os> one. DV_BETA=1 forces that for a non-tag dry run.

set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

PACK_ID="DemoViewer.NET"
PACK_TITLE="DemoViewer.NET"
PACK_AUTHORS="DemoViewer.NET"

host_rid() {
    local os arch
    case "$(uname -s)" in
        Darwin) os=osx ;; Linux) os=linux ;; *) os=win ;;
    esac
    case "$(uname -m)" in
        arm64|aarch64) arch=arm64 ;; *) arch=x64 ;;
    esac
    echo "$os-$arch"
}

RID="${1:-$(host_rid)}"
HOST_OS="$(uname -s)"
case "$RID" in
    win-x64)   MAIN_EXE="DemoViewer.NET.Desktop.exe"; NEED_OS="MINGW|MSYS|CYGWIN|Windows" ;;
    osx-arm64) MAIN_EXE="DemoViewer.NET.Desktop";     NEED_OS="Darwin" ;;
    linux-x64) MAIN_EXE="DemoViewer.NET.Desktop";     NEED_OS="Linux" ;;
    *) echo "unsupported RID: $RID (first-party: win-x64, osx-arm64, linux-x64)" >&2; exit 2 ;;
esac
if ! echo "$HOST_OS" | grep -qE "$NEED_OS"; then
    echo "ERROR: $RID installers must be built on their own OS ($NEED_OS); host is $HOST_OS." >&2
    echo "       vpk cannot cross-build — run this on a $RID host (or in the CI matrix)." >&2
    exit 2
fi

OUT="artifacts/publish/DemoViewer.NET-$RID"
REL="artifacts/velopack/$RID"
VERSION="$(dotnet nbgv get-version -v NuGetPackageVersion)"

# Tag/version agreement guard. version.json is the single source of the stamped version — nbgv
# does NOT read it from the tag — so tagging v0.6.0 while version.json still says 0.5.1 silently
# ships 0.5.1 artifacts under a 0.6.0 release. That was always true, but it used to be visible:
# a tag build produced "0.5.1-g<sha>", which looked wrong at a glance. Now that version tags are
# a publicReleaseRefSpec match the output is a clean "0.5.1", which reads as legitimate. Same
# mistake, quieter failure — so fail loudly here instead.
TAG_REF="${GITHUB_REF:-}"
case "$TAG_REF" in
    refs/tags/v*)
        TAG_VER="${TAG_REF#refs/tags/v}"
        if [ "$TAG_VER" != "$VERSION" ]; then
            echo "ERROR: tag v$TAG_VER does not match the stamped version $VERSION." >&2
            echo "       version.json drives the artifact version; bump it to $TAG_VER, or" >&2
            echo "       re-tag to v$VERSION. (Ship-plan step 16 is the bump step.)" >&2
            exit 2
        fi
        echo "tag/version agreement: v$TAG_VER == $VERSION"
        # The app's release-notes lookup and beta detection only understand these two shapes.
        if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-beta[0-9]{4})?$ ]]; then
            echo "ERROR: release tags are vX.Y.Z or vX.Y.Z-betaNNNN; got v$VERSION." >&2
            exit 2
        fi
        ;;
esac

# Beta packs go to <os>-beta. vpk's default channel is the OS short name, which is the RID's
# prefix, and the RID was checked against the host above, so this cannot name another platform.
CHANNEL=""
if [ "${DV_BETA:-0}" = "1" ] || [[ "$VERSION" =~ -beta[0-9]+ ]]; then
    CHANNEL="${RID%%-*}-beta"
    echo "beta build: channel $CHANNEL"
fi

# Signing inputs. The old shared names reached every platform's vpk; refuse them rather than ignore
# them, so a leftover secret cannot silently produce an unsigned build.
for old in DV_SIGN_APP_IDENTITY DV_SIGN_INSTALL_IDENTITY DV_SIGN_ENTITLEMENTS DV_NOTARY_PROFILE; do
    if [ -n "${!old:-}" ]; then
        echo "ERROR: $old was replaced by the DV_MAC_* variables (see the header of this script)." >&2
        exit 2
    fi
done

# vpk reads these itself (`vpk pack -H` lists them), so they reach it whatever the checks below decide.
for v in VPK_SIGN_APP_IDENTITY VPK_SIGN_INSTALL_IDENTITY VPK_SIGN_ENTITLEMENTS VPK_NOTARY_PROFILE VPK_KEYCHAIN; do
    if [ -n "${!v:-}" ]; then
        echo "ERROR: $v goes straight to vpk and bypasses this script's per-RID signing checks." >&2
        echo "       Use the DV_MAC_* variables instead." >&2
        exit 2
    fi
done

SIGN_ARGS=()
MAC_VARS=(DV_MAC_SIGN_APP_IDENTITY DV_MAC_SIGN_INSTALL_IDENTITY DV_MAC_NOTARY_PROFILE DV_MAC_KEYCHAIN DV_MAC_SIGN_ENTITLEMENTS)
SIGNED=0
if [ "$RID" = "osx-arm64" ]; then
    have=0
    for v in DV_MAC_SIGN_APP_IDENTITY DV_MAC_SIGN_INSTALL_IDENTITY DV_MAC_NOTARY_PROFILE; do
        if [ -n "${!v:-}" ]; then have=$((have + 1)); fi
    done
    # Signed but not notarized is no better than unsigned to Gatekeeper, so it is all three or none.
    if [ "$have" -ne 0 ] && [ "$have" -ne 3 ]; then
        echo "ERROR: macOS signing needs DV_MAC_SIGN_APP_IDENTITY, DV_MAC_SIGN_INSTALL_IDENTITY and" >&2
        echo "       DV_MAC_NOTARY_PROFILE together; only $have of the 3 are set." >&2
        exit 2
    fi
    if [ "$have" -eq 3 ]; then
        SIGN_ARGS+=(--signAppIdentity "$DV_MAC_SIGN_APP_IDENTITY")
        SIGN_ARGS+=(--signInstallIdentity "$DV_MAC_SIGN_INSTALL_IDENTITY")
        SIGN_ARGS+=(--notaryProfile "$DV_MAC_NOTARY_PROFILE")
        if [ -n "${DV_MAC_KEYCHAIN:-}" ]; then SIGN_ARGS+=(--keychain "$DV_MAC_KEYCHAIN"); fi
        if [ -n "${DV_MAC_SIGN_ENTITLEMENTS:-}" ]; then SIGN_ARGS+=(--signEntitlements "$DV_MAC_SIGN_ENTITLEMENTS"); fi
        SIGNED=1
    fi
else
    for v in "${MAC_VARS[@]}"; do
        if [ -n "${!v:-}" ]; then
            echo "ERROR: $v is a macOS signing input, but this pack is $RID." >&2
            exit 2
        fi
    done
fi

if [ "${DV_REQUIRE_SIGNING:-0}" = "1" ] && [ "$SIGNED" != "1" ]; then
    echo "ERROR: DV_REQUIRE_SIGNING=1, but no signing inputs are set for $RID." >&2
    exit 2
fi
echo "signing: $([ "$SIGNED" = 1 ] && echo "signed and notarized ($RID)" || echo "unsigned")"

# 1. Self-contained publish (+ asset/native layout + guards).
./scripts/publish.sh "$RID"

# 1b. Delta seeding (opt-in: DV_DELTA_SEED=1, set by release.yml). vpk generates a delta whenever a
#     previous release's .nupkg is already sitting in --outputDir, so we fetch the current published
#     release into $REL before packing. Without this the dir starts empty every CI run and every
#     release ships full-only — which is what v0.5.0 and v0.5.1 did.
#
#     Stable passes no channel to either command, so both take vpk's OS default and agree by
#     construction. Beta passes $CHANNEL to both.
#
#     Stable seeds with --pre false and beta with --pre true. Betas live on prereleases, and
#     diffing a stable package against a beta builds a delta no stable user can apply.
#
#     Never fatal. No previous release, no network, a rate-limited token: all mean "no delta base",
#     and a full-only release is a correct release. Failing the build here would turn a size
#     optimisation into an outage.
if [ "${DV_DELTA_SEED:-0}" = "1" ]; then
    echo "delta seed: fetching current release into $REL"
    mkdir -p "$REL"
    if dotnet vpk download github \
        --outputDir "$REL" \
        --repoUrl "${DV_REPO_URL:-https://github.com/CS2OpenDev/DemoViewer.NET}" \
        ${CHANNEL:+--channel "$CHANNEL"} \
        --pre "$([ -n "$CHANNEL" ] && echo true || echo false)" \
        ${DV_GITHUB_TOKEN:+--token "$DV_GITHUB_TOKEN"}; then
        echo "delta seed: base packages now in $REL:"
        ls -la "$REL" | grep -i nupkg || echo "  (none — first release on this channel; full-only is expected)"
    else
        echo "delta seed: download failed or no prior release — packing FULL-ONLY (not an error)" >&2
    fi
fi

# 2. Pack. Optional flags are appended only when their inputs exist/are set.
mkdir -p "$REL"
args=(
    --packId "$PACK_ID"
    --packTitle "$PACK_TITLE"
    --packAuthors "$PACK_AUTHORS"
    --packVersion "$VERSION"
    --packDir "$OUT"
    --mainExe "$MAIN_EXE"
    --runtime "$RID"
    --outputDir "$REL"
)
[ -n "$CHANNEL" ] && args+=(--channel "$CHANNEL")
# The ${x[@]+...} form: macOS runs this under Apple's bash 3.2, where an empty "${x[@]}" is an
# unbound-variable error under set -u.
args+=(${SIGN_ARGS[@]+"${SIGN_ARGS[@]}"})

# shellcheck disable=SC2206
[ -n "${DV_VPK_EXTRA_ARGS:-}" ] && args+=($DV_VPK_EXTRA_ARGS)

# Notarization is the only part of a pack that waits on the network for tens of minutes, and vpk
# throws away a signed build when that wait drops. Invalid or Rejected is Apple's verdict on the
# submission and will not change, so neither is ever retried.
pack_retryable() {
    if grep -qE 'status: (Invalid|Rejected)' "$1"; then return 1; fi
    grep -qE 'NSURLErrorDomain|HTTPError\(statusCode: 5|The request timed out' "$1"
}

# Apple keeps processing a submission whose wait was interrupted, so report the id rather than
# leaving a finished notarization unreachable.
pack_submission_id() {
    # grep finds nothing when the failure never reached Apple, and set -e must not take the caller out.
    grep -oE 'submissions/[0-9a-f]{8}-[0-9a-f-]{27}' "$1" | tail -1 | cut -d/ -f2 || true
}

ATTEMPTS=1
[ "$SIGNED" = "1" ] && ATTEMPTS=$(( ${DV_PACK_RETRIES:-1} + 1 ))
PACK_LOG="$(mktemp "${TMPDIR:-/tmp}/vpk-pack.XXXXXX")"
PACK_SNAP=""
pack_restore() { [ -n "$PACK_SNAP" ] || return 0; rm -rf "$REL"; cp -Rp "$PACK_SNAP" "$REL"; }
pack_cleanup() {
    rm -f "$PACK_LOG"
    if [ -n "$PACK_SNAP" ]; then rm -rf "$(dirname "$PACK_SNAP")"; fi
}
trap pack_cleanup EXIT
if [ "$ATTEMPTS" -gt 1 ]; then
    # Restore $REL from a copy, not by deleting what the failed attempt added: vpk may also rewrite
    # the seeded release metadata in place, and a second pack must not diff against its own output.
    PACK_SNAP="$(mktemp -d "${TMPDIR:-/tmp}/vpk-rel.XXXXXX")/rel"
    cp -Rp "$REL" "$PACK_SNAP"
fi

echo "vpk pack: id=$PACK_ID version=$VERSION rid=$RID channel=${CHANNEL:-<os default>} -> $REL"
PACK_TRY=1
while :; do
    if dotnet vpk pack "${args[@]}" 2>&1 | tee "$PACK_LOG"; then
        break
    fi
    PACK_RC=${PIPESTATUS[0]}
    [ "$PACK_RC" -eq 0 ] && PACK_RC=1
    if [ "$PACK_TRY" -ge "$ATTEMPTS" ] || ! pack_retryable "$PACK_LOG"; then
        echo "ERROR: vpk pack failed on attempt $PACK_TRY of $ATTEMPTS (exit $PACK_RC)." >&2
        PACK_SUB="$(pack_submission_id "$PACK_LOG")"
        if [ -n "$PACK_SUB" ]; then
            echo "       Apple may still be processing submission $PACK_SUB. Its verdict:" >&2
            echo "         xcrun notarytool info $PACK_SUB --key <p8> --key-id <id> --issuer <uuid>" >&2
        fi
        # A half-written package here becomes a delta base for the next run. Only a pack with
        # retries enabled holds a snapshot to restore from.
        pack_restore
        exit "$PACK_RC"
    fi
    PACK_TRY=$(( PACK_TRY + 1 ))
    echo "vpk pack lost its connection while notarizing. Retrying: attempt $PACK_TRY of $ATTEMPTS." >&2
    echo "       Apple finishes the abandoned submission anyway; nothing needs cleaning up." >&2
    pack_restore
    sleep 30
done

echo "velopack packages:"
ls -la "$REL"
