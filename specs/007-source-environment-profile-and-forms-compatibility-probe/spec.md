# 007 - Source Environment Profile and Forms Compatibility Probe

## Goal

Let an authenticated project declare a server-owned Oracle source environment and receive a truthful,
machine-readable compatibility result before any native Oracle library is loaded or Azure lab is provisioned.

## Requirements

- Source profiles are immutable, versioned, project/tenant owned, and hash bound.
- A caller supplies only labels, expected releases, a server path alias, schema allowlist, and secret-reference names.
- Rooted paths, traversal, connection strings, credentials, and caller-supplied observations are rejected.
- The web process never loads Forms native libraries or starts a native helper.
- A missing Forms installation, Open API libraries, Oracle client connection, worker architecture, or operator export is
  represented by typed `BlockedPrerequisite` capability rows.
- Probe observations create a new profile version; they never rewrite declared expectations or history.
- The GUI exposes `Choose source -> Check connection` and shows exact blockers/remediation identifiers.

## Non-goals

This increment does not open an FMB, invoke Forms Builder/Runtime/Compiler, connect to Oracle, install licensed
media, or claim native extraction. It changes no migration phase identity or approval binding.