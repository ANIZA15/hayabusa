#!/usr/bin/env bash
set -euo pipefail

# =============================================================================
# Hayabusa vs Go (net/http) vs Rust (axum / tokio) Cross-Language Benchmark Runner
#
# - Default (when Docker daemon is active):
#     Runs `docker compose -f docker-compose.benchmarks.yml` with inline Dockerfiles
#     so zero .go or .rs files exist in the repository.
# - Fallback / `--local` mode (e.g. inside a DevContainer or VM without dockerd):
#     Builds and runs ephemeral Go / Rust / Hayabusa servers in `/tmp` via
#     `Hayabusa.Benchmarks --cross-lang` and removes all `/tmp` artifacts on exit.
# =============================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "${REPO_ROOT}"

MODE="auto"
FORWARD_ARGS=()

for arg in "$@"; do
    case "${arg}" in
        --docker)
            MODE="docker"
            ;;
        --local)
            MODE="local"
            ;;
        *)
            FORWARD_ARGS+=("${arg}")
            ;;
    esac
done

can_use_docker() {
    command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1
}

run_docker_benchmarks() {
    echo "[Hayabusa Benchmark] Running cross-language benchmarks via Docker Compose (docker-compose.benchmarks.yml)..."
    trap 'docker compose -f docker-compose.benchmarks.yml down --remove-orphans >/dev/null 2>&1 || true' EXIT
    docker compose -f docker-compose.benchmarks.yml up --build --abort-on-container-exit --exit-code-from benchmark-runner
}

run_local_ephemeral_benchmarks() {
    echo "[Hayabusa Benchmark] Running cross-language benchmarks via ephemeral /tmp harness (zero .go/.rs files in repo)..."
    dotnet build benchmarks/Hayabusa.Benchmarks/Hayabusa.Benchmarks.csproj -c Release -f net9.0 --nologo -v quiet
    dotnet run --project benchmarks/Hayabusa.Benchmarks/Hayabusa.Benchmarks.csproj -c Release -f net9.0 --no-build -- --cross-lang "${FORWARD_ARGS[@]}"
}

if [[ "${MODE}" == "docker" ]]; then
    run_docker_benchmarks
elif [[ "${MODE}" == "local" ]]; then
    run_local_ephemeral_benchmarks
elif can_use_docker; then
    run_docker_benchmarks
else
    run_local_ephemeral_benchmarks
fi
