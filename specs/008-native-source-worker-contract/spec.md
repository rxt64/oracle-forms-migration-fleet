# 008 - Native Source Worker Contract

## Goal

Run Oracle Forms source probing outside the web process through a hash-pinned, bounded worker protocol.
Until authorized Forms media and native libraries are supplied, the worker must return typed prerequisites
and refuse extraction.

## Requirements

- The worker is a separate executable and accepts only a versioned JSON probe request on standard input.
- The Windows x86 worker is self-contained; the legacy source host does not need a separately installed modern .NET runtime.
- The host stream-copies the executable into a private directory, verifies both hashes in constant time,
  verifies an x86 PE image, and launches only the staged copy.
- Input, image size, output, duration, environment, bundle extraction, and command surface are bounded.
- The host accepts only the worker's exact three-capability blocked-prerequisite manifest.
- The worker reports OS and process architecture but no environment dump, paths, credentials, or native addresses.
- Forms 6i requires Windows, x86, and exact candidate release `6.0.8.22.1`.
- No NDAPI/Oracle dependency is linked until the authorized native tuple is available and experimentally proven.
- Missing bindings remain `BlockedPrerequisite`; a worker integrity/protocol failure is `Rejected`.

## Non-goals

This increment does not open an FMB, connect to Oracle, compile a form, or claim native extraction.