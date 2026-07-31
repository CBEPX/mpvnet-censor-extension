# ADR-003: Session-scoped schedules without a database

**Status:** Accepted
**Date:** 31 July 2026
**Historical note:** replaces pre-repository storage and library drafts

## Context

The application is intended primarily for one-time playback of many different films. A film is unlikely to be opened repeatedly. The operator needs a convenient way to select a text file with timecodes and apply it to the film that is currently open in mpv.net.

A persistent catalog would introduce SQLite, fingerprinting, schema migrations, recovery, indexing, backup logic and a larger UI without providing proportionate value for the expected workflow.

## Decision

The extension will not maintain a movie database or persistent film-to-schedule bindings.

A schedule can be activated in two ways:

1. manual selection through the extension menu, file picker or drag-and-drop into the extension tool window;
2. automatic discovery of exactly one sidecar named `<media-basename>.censor.txt`, `<media-basename>.censor.srt` or `<media-basename>.censor.vtt`.

If more than one sidecar exists, the extension must not choose silently. It presents them in TXT → SRT → VTT order and waits for the user.

A manually selected schedule belongs only to the current `media_session_id`. It is removed when the current media ends or another media file starts.

The extension persists only global settings, the last schedule directory, UI state and logs.

## Consequences

### Positive

- no database dependency;
- no migrations;
- no background indexing;
- no media fingerprint computation;
- simpler UI;
- fewer race conditions and recovery paths;
- faster implementation and review;
- schedule remains an ordinary portable text file;
- exact sidecar naming still provides a zero-click workflow when useful.

### Negative

- a manually selected schedule must be selected again if the film is reopened;
- renaming or moving a film does not preserve an implicit association unless the sidecar is moved and renamed with it;
- the extension cannot search a central catalog of schedules;
- wrong-version protection is limited to optional duration metadata and explicit user confirmation.

## Guardrails

- a schedule selected for media A must never be applied to media B;
- all asynchronous results are checked against `media_session_id`;
- sidecar lookup uses exact basename only;
- no fuzzy title matching;
- optional `media-duration-ms` mismatch blocks automatic sidecar application and requires confirmation for manual application;
- the extension must not silently create a hidden catalog.

## Revisit criteria

This decision may be revisited only if real usage demonstrates frequent rewatching, a need for a shared central schedule repository, or an operator burden that cannot be solved by sidecar naming and remembering the last directory.
