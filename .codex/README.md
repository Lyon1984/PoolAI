# Codex project support

Repository-wide instructions live in `/AGENTS.md`. Stable project-maintained context lives in `/docs/project-memory/` and is explicitly loaded through those instructions.

Do not create or commit `.codex/memories/` here. Codex native memories are generated local state stored under `~/.codex/memories/` by default and controlled through `/memories` or the user's Codex settings. Required team rules and project facts must remain in `AGENTS.md` or checked-in documentation.

This directory may later contain trusted project-scoped Codex configuration, but it must not contain credentials, generated transcripts, private memory exports, or machine-specific state.
