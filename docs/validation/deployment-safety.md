# Deployment script safety validation

## Safety invariants

- Installation, release, temporary staging, backup, quarantine, and settings
  paths are converted to absolute paths and must be strict children of their
  expected parent before a recursive delete or directory move.
- A protected root, its move source, and every recursively deleted tree are
  rejected if they are a reparse point or contain a reparse point/junction.
- ZIP entries are inspected before extraction. Rooted names, `.`/`..`
  traversal, alternate data stream syntax, duplicate destinations, Unix
  symbolic links, excessive entry counts, and excessive expanded size fail
  closed.
- An install package has exactly one top-level `FoxMouse` directory and must
  contain regular files `FoxMouse.exe`, `FoxMouse.Guard.exe`,
  `FoxMouse.Uninstall.exe`, `FoxMouse.Cleanup.exe`, and
  `Settings\FoxMouse.Settings.exe` before any installed process is stopped or
  any installed directory is moved.
- A user-selected location is interpreted as a parent directory. The canonical
  target owns exactly one `FoxMouse` child, must stay inside the isolated test
  root during automated validation, and must not overlap settings, cache,
  Start Menu, protected, network, device or reparse-point paths. A non-empty
  unrecognized target fails before mutation.
- When `-ExpectedSignerThumbprint` (alias `-CertificateThumbprint`) is supplied,
  the App, Guard, Settings, Uninstall, Cleanup, and outer Setup executables must
  have a valid Authenticode signature from that exact certificate at their
  immutable packaging stage.

## Running-process protocol

Install and uninstall enumerate only process-name candidates, then require both
of the following before acting on one:

1. the process image path exactly equals the expected executable below the
   resolved install root; and
2. the process belongs to the deployment script's current Windows session.

Before requesting shutdown, a separately launched Guard must complete
`--restore-cursor` with exit code zero. The script first requests normal close
for a verified settings window, then requests normal close and posts `WM_QUIT`
to the verified tray application's message-loop threads, and waits up to five
seconds. If a verified process remains, a second,
separate Guard must confirm Show success immediately before `Process.Kill()` is
allowed. A final Guard invocation confirms cursor visibility after termination.
An unverified same-name process is never signalled or killed.

## Transactions

Install copies the validated payload into a unique candidate beside the final
per-user install directory. It then renames the previous install to a unique
backup and renames the candidate into place on the same volume. Shortcut or
launch failure moves the candidate back, restores the prior directory and
shortcut, and restarts a previously running old version. The old backup is
recursively removed only after commit.

Uninstall first renames the validated install and settings directories into
same-volume quarantine paths. Startup registry data and the Start Menu shortcut
are retained in memory/on disk until logical commit and are restored if a
pre-commit operation fails. Quarantine cleanup occurs only after commit; an
unsafe or locked quarantine is retained with a warning rather than followed or
force-deleted.

Release creation publishes into a unique artifact staging root. Build payload,
Setup embedding inputs, and generated Cleanup version metadata stay below that
staging root; the candidate release contains only the two distributable
artifacts and their manifests. The candidate is subjected to the complete
release-artifact and isolated installer-lifecycle gates before activation. If a
release already exists, it is renamed to a backup immediately before the
validated new release is moved into place. Activation failure restores the old
release, and any missing backup or occupied restore destination is reported as
an explicit rollback failure rather than silently skipped.
Earlier version directories are never release-activation targets. Their release
manifest and SHA-256 list are snapshotted before packaging and verified again
after activation, so publishing v0.4.2 cannot silently rewrite or remove the
v0.1.0–v0.4.1 evidence.

## Signing and Setup order

For signed releases the immutable order is:

1. publish and assemble the payload, including the versioned and branded
   constrained Cleanup companion;
2. sign and verify App, Guard, Settings, Uninstall, and Cleanup;
3. create the portable ZIP containing the signed binaries;
4. publish the self-contained .NET Setup with that exact ZIP embedded;
5. extract the embedded package and require byte identity with the portable ZIP;
6. sign and verify the outer `FoxMouse-Setup-x64.exe` last;
7. compute hashes, write the release manifest, and run the candidate gates
   without mutating either
   artifact afterward.

The root maintenance launcher uses a named ready event and verified parent PID
before returning from its GUI handoff. Its optional result JSON and the cleanup
status are written to a same-directory candidate, flushed, and atomically
published, so observers never accept a partial completion record. Windows'
quiet Installed Apps command points to the stable external maintenance host and
returns the real transaction exit code synchronously.

The Cleanup companion accepts only a same-session verified PID, exact companion
path, and either a strict `FoxMouse-Uninstall-Host-<GUID>` temporary directory
or the fixed maintenance-host directory. Stale cleanup accepts exactly one
ordinary helper file, deletes it explicitly, and removes the empty directory
non-recursively. It does not use `MoveFileEx` or register reboot-time deletion.

## Current automated evidence requirement

The v0.4.2 candidate must run the complete lifecycle under a custom parent with
a space in its name. The first install passes `--install-parent`; subsequent
Setup repair deliberately omits it and must rediscover the same root from the
validated Installed Apps record. The gate checks `InstallLocation`, display
icon, maintenance commands, shortcut target and working directory, then runs
cached repair and every uninstall entry point. A sibling sentinel must survive
and the default test installation root must remain absent.

The result is recorded in `docs/validation/v0.4.2-execution-report.md`. The
final run at `artifacts/validation/m5-20260905-021308` passed this complete
custom-parent lifecycle twice (candidate gate and activated-package gate),
including failure-code propagation and cleanup assertions.

## Historical automated evidence

The following v0.4.0 checks were run on 2026-09-04 without touching the
existing installed FoxMouse instance or hiding the real cursor:

```powershell
pwsh -NoProfile -File .\scripts\Accept-Milestone.ps1 `
  -Milestone M5 -Configuration Release
```

Evidence is in `artifacts/validation/m5-20260904-180927`. It includes 349/349
solution tests, 22/22 deployment tests, script-safety checks, Settings/App/Guard
smokes, candidate and activated-package verification, and two successful real
isolated installer lifecycles. The lifecycle executes clean install, Setup
repair, deletion of an installed file, root cached repair with exact SHA-256
restoration, detached root uninstall, synchronous quiet-ARP failure and success,
and explicit settings deletion. It also confirms the repair cache, hash sidecar,
and active install-state hash agree.

The final release left no installer test root, test registry key, staging root,
backup root, temporary maintenance host, or cleanup-worker directory. The
release manifest byte counts and SHA-256 values were independently recomputed.

The Authenticode-positive path still requires a real code-signing certificate
and timestamp-service access in the release environment. Supplying a signing
parameter without a valid matching signature is intentionally a hard failure.
