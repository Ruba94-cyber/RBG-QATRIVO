# TestLoom

**Standalone desktop test-management application — early technical preview.**

TestLoom helps teams create local test workspaces, define cases, and record
execution results. It is built with .NET 9 and Avalonia and has no dependency
on any private companion application or private repository.

## What works in this first independent edition

- A first-run selector: pick the language (English or Italian) and one of five presets (Software QA, Hardware,
  Manufacturing, Laboratory, Blank), name the workspace and optionally start with example content. The preset sets the
  vocabulary and the first test templates; nothing is written until you confirm.
- Editable test templates: sections of named fields, optionally required before a run can be recorded; template fields
  are filled in on each test.
- Priority, owner and tags on every test; filter by priority, tag or plan; duplicate a test.
- Test plans (campaigns / protocols): group tests and follow executed, passed, failed and blocked counts per plan.
- Add and edit test cases; archive and restore cases without deleting history.
- Add, edit, remove and reorder structured test steps with action and expected result.
- Track defects/nonconformities with a stable ID and optional test-case link; edit their title, description, severity and lifecycle status.
- Create and edit requirements without changing stable requirement IDs; link or unlink them on existing tests, and monitor traceability coverage.
- Record Passed, Failed, or Blocked, or run the test step by step: the overall result is derived from the steps
  (any failed step fails the run, any blocked step blocks it, it passes only when every step passed).
- Each run can record tester, environment, version/build and duration, and carry evidence files (copied into the
  workspace, SHA-256 checked, included in backups).
- Review recent execution results, notes and evidence integrity.
- Import requirements and tests from CSV (comma or semicolon separated; all-or-nothing, duplicates skipped).
- Export a requirement-by-test traceability matrix to CSV.
- An audit trail of changes (who, what, when), capped in size, shown in its own tab.
- English and Italian user interface, switchable at any time.
- Export test cases, ordered steps, requirements/coverage and defects to CSV using a native save dialog, with quoting and spreadsheet-formula safeguards.
- Paste test steps (action and expected result) straight from a spreadsheet; the paste is atomic, so an invalid row rejects everything.
- Search test cases by text (title, description, steps, linked requirement) and filter by latest outcome.
- Live dashboard numbers: pass rate, execution progress, requirement coverage and open defects by severity.
- Export a self-contained, printable HTML report (no scripts, no external resources).
- Back up the workspace to a ZIP archive that can be restored into an empty folder only.
- Persist data locally as JSON in a dedicated `TestLoom` user profile.
- Preserve a previous JSON version as `workspace.json.bak` when updating.
- Recover explicitly from a validated `.bak` if the primary JSON becomes corrupted, preserving the damaged bytes in `workspace.json.corrupt-*`.
- Reload the workspace from disk after a stale-write conflict, or after an external repair of an unreadable workspace, without overwriting external changes.
- Refuse recovery when a workspace declares an unsupported, missing or ambiguous schema version, or is valid JSON without a versioned workspace object; installing a compatible version takes priority over rolling back data.
- Refuse to overwrite workspace changes made by another session until reloaded; a local exclusive writer lock also coordinates cooperating TestLoom processes.
- Reject linked local workspace, primary JSON, backup and lock-file paths; these paths must be regular local directories/files.
- Validate unique requirement and defect IDs, test-step identifiers and references to existing tests/requirements.

**Not yet supported:** advanced requirements, rich reports, multiple workspaces, role management, multi-user collaboration,
network shares, distributed locking, or feature parity with mature test-management suites.
Backup recovery is an explicit action, not automatic. This version is not production-ready. It intentionally supports local, single-user
workspaces only: the local writer lock and conflict checks reduce accidental lost updates but do not
provide distributed coordination across multiple users or network shares.

## Requirements

- .NET 9 SDK to build.
- Windows, macOS or Linux desktop supported by Avalonia (platform validation
  has not been completed yet).

## Build and start

```sh
dotnet build TestLoom.slnx -c Release
dotnet run --project src/TestLoom.Desktop/TestLoom.Desktop.csproj -c Release
```

Run the self-contained test suite and core smoke verification:

```sh
dotnet test src/TestLoom.Core.Tests/TestLoom.Core.Tests.csproj -c Release
dotnet run --project src/TestLoom.Core.Smoke/TestLoom.Core.Smoke.csproj -c Release
dotnet test src/TestLoom.Desktop.Tests/TestLoom.Desktop.Tests.csproj -c Release
```

Data is stored in the operating system's LocalApplicationData folder under
`TestLoom/workspace.json`, not beside the executable. The first-run workflow
never discovers or imports another application's data.

## Layout

- `src/TestLoom.Core/` — independent models, presets, field utilities and storage.
- `src/TestLoom.Desktop/` — independent Avalonia desktop application.
- `src/TestLoom.Core.Tests/` — independent xUnit suite for the reusable core.
- `src/TestLoom.Desktop.Tests/` — headless UI tests of the desktop shell.
- `src/TestLoom.Core.Smoke/` — dependency-free smoke verification.
- `TestLoom.slnx` — self-contained solution.

## Project status

This document describes an **unapproved source candidate**. Availability of
source in a staging directory does not authorize redistribution or public
publication. Contributor attribution, ownership, dependency/license review,
build and UI validation, and a release license must be settled before release.

## Origin

TestLoom is a generalized adaptation of a more specific private application that its author develops and uses with
his team. It keeps the general ideas (test cases with steps, requirements and traceability, defects, execution history,
local and recoverable storage) and leaves out everything specific to that context. The two applications are maintained
separately; their data files are not interchangeable unless an import is explicitly documented.

## License

TestLoom is source-available under the [PolyForm Shield License 1.0.0](LICENSE). In short: you may use, modify and
share it, including inside a company, but you may not use it to provide a product that competes with TestLoom.
The license text is authoritative; this summary is not legal advice. For a commercial license or questions about
attribution, contact paolo.ruba.1913@gmail.com.

## Credits and attribution

Keep the `LICENSE` and `NOTICE` files, including the `Required Notice` line, with any copy of the software, and credit
the author when you use or redistribute it, for example: "Based on TestLoom by Paolo Rubagotti". The About window of the
application shows the same credit.

## Name and icon

The name "TestLoom" and its icon are not licensed for use as the name or branding of another product.

## Third-party software

TestLoom uses open-source packages under their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Security

See [SECURITY.md](SECURITY.md).
