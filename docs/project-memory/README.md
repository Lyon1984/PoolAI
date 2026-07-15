# Project memory

This directory contains concise, project-maintained context for contributors and Codex. It is intended to be version-controlled after repository initialization. It is not Codex native memory, is not generated from chats, and does not synchronize with `~/.codex/memories/`.

## Read order

1. [`project-context.md`](project-context.md)
2. [`current-state.md`](current-state.md)
3. [`open-items.md`](open-items.md)
4. [`lessons-learned.md`](lessons-learned.md) when the task touches a recorded issue
5. [`../README.md`](../README.md) and the task-relevant authoritative contract

## Authority

These files are a navigation and handoff layer. They are lower priority than every contract listed in `docs/README.md`. When memory conflicts with an authoritative contract or verified code/test evidence, correct the memory in the same change.

## Update protocol

- Record only evidence-backed state, with links to files, tests, issues, ADRs, or commits when available.
- Put unverified assumptions, risks, and blockers in `open-items.md`.
- Update `current-state.md` only when implementation or verification state actually changes.
- Put architecture decisions in `docs/architecture/adr/`; memory links to the ADR instead of copying it.
- Remove resolved items rather than maintaining a chat-style history or session log.
- Never store passwords, tokens, API keys, database credentials, secret paths or values, personal data, chat transcripts, command dumps, or model reasoning.
