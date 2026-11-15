# RBG - QATRIVO

**A desktop test-management workspace for teams: requirements, test cases and their executions, anomalies, change
requests, audit and reports, kept in a folder your team shares.**

RBG - QATRIVO is built with .NET 10 and Avalonia (Windows is the tested platform). It is the general-purpose edition of a
test-management application that its author develops for and uses with a team. That application stays the place where
new features are written; RBG - QATRIVO is generated from it by an automatic, repeatable process that replaces the
company-specific vocabulary with neutral words, so the two share the same code and RBG - QATRIVO follows every improvement.

> Status: early public release. The generated edition builds, its test suites run, and the application starts and passes
> the first end-to-end checks that drive the real program through UI Automation. It has not had the long field use of the
> application it comes from. Treat it as a preview and keep backups.

## What it does

- **Requirements and test cases** with ordered steps, configurable instrument/tool fields, templates and results per
  product and component.
- **Executions** recorded per operation with a quick, keyboard-driven editor, evidence and history.
- **Anomalies and change requests** with classification, links to tests and a recovery bin.
- **Audit and coverage matrices**, dashboards and charts, interactive Excel export of lists, matrices and reports.
- **Master data** (products, product families, components, functions) with import from spreadsheets.
- **Shared folder, several users.** The data lives as files in a folder that several PCs open at the same time (a
  network share or a folder kept in sync by a sync client). Writers are coordinated with locks and journals, a local
  SQLite index keeps large archives fast, and conflicting copies are merged instead of lost.
- **Backup and archive** of the workspace data.
- **English, Italian and French** user interface.

## Download and guide

A signed Windows build (single self-contained `RBG - QATRIVO.exe`) is attached to each [release](../../releases). Read the
[user guide](GUIDE.md) ([italiano](GUIDA.md)) and [SIGNING.md](SIGNING.md) for how to verify the signature.

## Build and run

```sh
dotnet build TestLoom.slnx -c Release
dotnet run --project src/TestLoom.App/TestLoom.App.csproj -c Release
```

Requirements: the .NET 10 SDK. The first start asks where the data folder is; use the same folder on every PC to work
together. `--config <folder>` starts the program against an isolated configuration folder (used by the automated tests).

## Tests

```sh
dotnet test src/TestLoom.App.Tests/TestLoom.App.Tests.csproj -c Release
dotnet test src/TestLoom.App.Tests.Headless/TestLoom.App.Tests.Headless.csproj -c Release
dotnet test src/TestLoom.App.UITests/TestLoom.App.UITests.csproj -c Release
```

The UI tests (FlaUI) start the real `RBG - QATRIVO.exe` and drive it through UI Automation on Windows; they need a desktop
session and a built application.

## About the generated edition

The words used in the user interface for products, components and similar concepts are neutral on purpose and can be
adapted. A few areas keep the structure of the original team workflow under generic names, for example the optional
link to an external ticketing system. It is inactive until configured.

On the first start with an empty data folder you can pick a starting point for your kind of testing (software QA,
hardware engineering, manufacturing quality, research laboratory or a blank workspace) and get its first test templates;
nothing is created in a folder that already holds data.

## Project layout

- `src/TestLoom.App/` — the application.
- `src/TestLoom.App.MultiPcHost/` — helper process used to exercise the shared-folder, several-PC behavior.
- `src/TestLoom.App.Tests/`, `src/TestLoom.App.Tests.Headless/`, `src/TestLoom.App.UITests/` — unit, headless and
  real-application tests.
- `TestLoom.slnx` — the solution.

## License

RBG - QATRIVO is source-available under the [PolyForm Shield License 1.0.0](LICENSE). In short: you may use, modify and share
it, including inside a company, but you may not use it to provide a product that competes with RBG - QATRIVO. The license
text is authoritative; this summary is not legal advice. For a commercial license or questions about attribution,
contact paolo.ruba.1913@gmail.com.

Keep the `LICENSE` and `NOTICE` files, including the `Required Notice` line, with any copy of the software, and credit
the author, for example "Based on RBG - QATRIVO by Paolo Rubagotti". The name "RBG - QATRIVO" and its icon are not licensed for
use as the name or branding of another product.

## Third-party software

RBG - QATRIVO uses open-source packages under their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Security and privacy

See [SECURITY.md](SECURITY.md). RBG - QATRIVO sends nothing anywhere. In a shared folder, everyone with access can see what is
stored there: names typed as testers or owners, notes, evidence files and the audit trail.
