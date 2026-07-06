#!/usr/bin/env bash
#
# Builds SamedisCare.SplSync.Tray for win-x64 (cross-compile from macOS works)
# and assembles a ready-to-copy deploy/ folder containing everything you need
# to run on a Windows machine.
#
# Usage:
#   ./build-deploy.sh              # full build
#   ./build-deploy.sh --skip-tests # skip dotnet test
#   ./build-deploy.sh --zip        # also create deploy.zip beside deploy/
#
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="${ROOT_DIR}/deploy"
TRAY_PROJECT="${ROOT_DIR}/src/SamedisCare.SplSync.Tray"
PUBLISH_DIR="${TRAY_PROJECT}/bin/Release/net8.0-windows/win-x64/publish"
RUNTIME="win-x64"
CONFIGURATION="Release"

SKIP_TESTS=false
MAKE_ZIP=false
for arg in "$@"; do
    case "$arg" in
        --skip-tests) SKIP_TESTS=true ;;
        --zip)        MAKE_ZIP=true ;;
        -h|--help)
            sed -n '2,12p' "$0"
            exit 0 ;;
        *) echo "Unknown arg: $arg" >&2; exit 1 ;;
    esac
done

# ---- Output helpers -------------------------------------------------------

if [[ -t 1 ]]; then
    BOLD=$'\033[1m'; GREEN=$'\033[32m'; BLUE=$'\033[34m'; RED=$'\033[31m'; RESET=$'\033[0m'
else
    BOLD=""; GREEN=""; BLUE=""; RED=""; RESET=""
fi
step() { echo; echo "${BLUE}${BOLD}==>${RESET} ${BOLD}$*${RESET}"; }
ok()   { echo "    ${GREEN}OK${RESET}   $*"; }
warn() { echo "    ${RED}WARN${RESET} $*"; }

# ---- Pre-checks -----------------------------------------------------------

step "Pre-checks"
if ! command -v dotnet > /dev/null 2>&1; then
    warn "dotnet not on PATH — install the .NET 8 SDK first."
    exit 1
fi
ok "dotnet $(dotnet --version)"

# ---- Restore + Test -------------------------------------------------------

step "Restore"
dotnet restore "${ROOT_DIR}/SamedisCare.SplSync.sln" --nologo --verbosity minimal

if [[ "$SKIP_TESTS" == "true" ]]; then
    warn "Skipping tests (--skip-tests)"
else
    step "Run tests"
    dotnet test "${ROOT_DIR}/tests/SamedisCare.SplSync.Core.Tests" \
        --configuration "${CONFIGURATION}" \
        --nologo \
        --verbosity minimal
fi

# ---- Publish Tray ---------------------------------------------------------

step "Publish Tray (single-file ${RUNTIME})"
rm -rf "${PUBLISH_DIR}"
dotnet publish "${TRAY_PROJECT}" \
    --configuration "${CONFIGURATION}" \
    --runtime "${RUNTIME}" \
    --self-contained true \
    /p:PublishSingleFile=true \
    /p:IncludeNativeLibrariesForSelfExtract=true \
    --nologo \
    --verbosity minimal

EXE="${PUBLISH_DIR}/SamedisCare.SplSync.Tray.exe"
if [[ ! -f "${EXE}" ]]; then
    warn "Tray.exe not found at ${EXE}"
    exit 1
fi
ok "Built $(basename "${EXE}") ($(du -h "${EXE}" | awk '{print $1}'))"

# ---- Assemble deploy/ -----------------------------------------------------

step "Assemble deploy/"
rm -rf "${DEPLOY_DIR}"
mkdir -p "${DEPLOY_DIR}"

cp "${EXE}" "${DEPLOY_DIR}/"
ok "EXE copied"

if [[ -f "${PUBLISH_DIR}/SamedisCare.SplSync.Tray.pdb" ]]; then
    cp "${PUBLISH_DIR}/SamedisCare.SplSync.Tray.pdb" "${DEPLOY_DIR}/"
    ok "PDB (debug symbols) copied"
fi

cp "${TRAY_PROJECT}/config.yml.example" "${DEPLOY_DIR}/"
ok "config.yml.example copied"

# Deploy-README aus versionierter Vorlage uebernehmen (docs/deploy-README.txt)
DEPLOY_README_SRC="${ROOT_DIR}/docs/deploy-README.txt"
if [[ -f "${DEPLOY_README_SRC}" ]]; then
    cp "${DEPLOY_README_SRC}" "${DEPLOY_DIR}/README.txt"
    ok "README.txt aus docs/deploy-README.txt uebernommen"
else
    warn "docs/deploy-README.txt fehlt - README.txt wird nicht erzeugt"
fi

# ---- Optional: zip --------------------------------------------------------

if [[ "$MAKE_ZIP" == "true" ]]; then
    step "Create deploy.zip"
    ZIP="${ROOT_DIR}/deploy.zip"
    rm -f "${ZIP}"
    (cd "${ROOT_DIR}" && zip -r -q "${ZIP}" "deploy")
    ok "Created $(basename "${ZIP}") ($(du -h "${ZIP}" | awk '{print $1}'))"
fi

# ---- Summary --------------------------------------------------------------

step "Done"
echo "  Deploy folder: ${DEPLOY_DIR}"
echo
ls -lh "${DEPLOY_DIR}" | tail -n +2 | sed 's/^/    /'
echo
ok "Bereit zum Kopieren auf die Windows-VM."
