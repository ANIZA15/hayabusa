#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CANONICAL_SKILLS_DIR="${ROOT_DIR}/.cursor/skills"
TARGET_SKILL_DIRS=(
    "${ROOT_DIR}/.claude/skills"
    "${ROOT_DIR}/.agents/skills"
    "${ROOT_DIR}/.codex/skills"
)

CANONICAL_AGENT_MD="${ROOT_DIR}/AGENTS.md"
TARGET_AGENT_MDS=(
    "${ROOT_DIR}/agent.md"
    "${ROOT_DIR}/CLAUDE.md"
)

MODE="${1:-sync}"

if [[ "${MODE}" == "--check" ]]; then
    echo "[check] Verifying agent instruction files (AGENTS.md, agent.md, CLAUDE.md)..."
    for target_md in "${TARGET_AGENT_MDS[@]}"; do
        if [[ ! -f "${target_md}" ]]; then
            echo "ERROR: Missing ${target_md}" >&2
            exit 1
        fi
        if ! diff -u "${CANONICAL_AGENT_MD}" "${target_md}" >/dev/null; then
            echo "ERROR: ${target_md} is out of sync with ${CANONICAL_AGENT_MD}. Run ./scripts/sync-agent-skills.sh" >&2
            exit 1
        fi
    done

    echo "[check] Verifying skills across .cursor/skills, .claude/skills, .agents/skills, and .codex/skills..."
    for target_dir in "${TARGET_SKILL_DIRS[@]}"; do
        if [[ ! -d "${target_dir}" ]]; then
            echo "ERROR: Missing skills directory ${target_dir}" >&2
            exit 1
        fi
        if ! diff -ru "${CANONICAL_SKILLS_DIR}" "${target_dir}"; then
            echo "ERROR: ${target_dir} is out of sync with ${CANONICAL_SKILLS_DIR}. Run ./scripts/sync-agent-skills.sh" >&2
            exit 1
        fi
    done

    echo "[ok] All agent files and skills for Cursor, Claude Code, and Codex are 100% synchronized."
    exit 0
fi

echo "[sync] Syncing AGENTS.md -> agent.md, CLAUDE.md..."
for target_md in "${TARGET_AGENT_MDS[@]}"; do
    cp "${CANONICAL_AGENT_MD}" "${target_md}"
done

echo "[sync] Syncing .cursor/skills -> .claude/skills, .agents/skills, .codex/skills..."
for target_dir in "${TARGET_SKILL_DIRS[@]}"; do
    rm -rf "${target_dir}"
    mkdir -p "${target_dir}"
    cp -R "${CANONICAL_SKILLS_DIR}/." "${target_dir}/"
done

echo "[ok] Synchronized all 11 skills and agent instructions across Cursor, Claude Code, and Codex."
