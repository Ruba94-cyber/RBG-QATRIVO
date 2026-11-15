<p align="center">
  <img src="src/TestLoom.App/Assets/rbg-qatrivo-icon-256.png" width="120" alt="RBG - QATRIVO official application icon" />
</p>

<h1 align="center">RBG - QATRIVO</h1>

<p align="center">
  <strong>From requirements to test execution, anomalies and reports — all in one workspace.</strong><br />
  A Windows desktop test-management tool for software QA, hardware validation, manufacturing and engineering teams.
</p>

<p align="center">
  <a href="https://github.com/Ruba94-cyber/RBG-QATRIVO/releases/latest"><strong>Download latest release</strong></a> ·
  <a href="#quick-start">Quick start</a> ·
  <a href="GUIDE.md">User guide</a> ·
  <a href="GUIDA.md">Guida italiana</a> ·
  <a href="#support-development">Donate</a>
</p>

<p align="center">
  <img alt="Windows desktop" src="https://img.shields.io/badge/Windows-desktop-263238?logo=windows&logoColor=white" />
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4" />
  <img alt="Avalonia UI" src="https://img.shields.io/badge/UI-Avalonia-24786E" />
  <a href="LICENSE"><img alt="PolyForm Shield 1.0.0" src="https://img.shields.io/badge/License-PolyForm_Shield_1.0-4F6573" /></a>
  <img alt="Latest GitHub release" src="https://img.shields.io/github/v/release/Ruba94-cyber/RBG-QATRIVO?label=release" />
</p>

## Why QATRIVO?

When test cases, execution evidence, issues and reporting live in different tools, tracing a result back to a requirement becomes harder than it should be.

**RBG - QATRIVO** brings the testing workflow together: organize what needs testing, execute it, record results and evidence, track anomalies and change requests, then review coverage and export reports. It can be used individually or with a team sharing a workspace folder.

The public application is a general-purpose edition of the author's actively developed team tool. It is generated through a repeatable process that replaces company-specific terminology with neutral wording while retaining a shared codebase.

> [!NOTE]
> QATRIVO has published Windows releases, but the public edition has not yet had the same long-term field use as the original team application. Keep backups of important workspaces and validate updates before rolling them out to a team.

## Features at a glance

| Area | What QATRIVO helps you do |
| --- | --- |
| **Requirements & test cases** | Organize test cases with ordered steps, templates, configurable tools/instruments, and results by product or component. |
| **Test execution** | Record operations and outcomes through a keyboard-friendly editor, with supporting evidence and execution history. |
| **Anomalies & change requests** | Classify findings, link them to tests, manage their lifecycle and recover deleted records from a recovery bin. |
| **Coverage & audit** | Review coverage matrices, dashboards, charts and the history behind test results. |
| **Reporting & Excel** | Export lists, coverage matrices and reports for review and sharing. |
| **Master data** | Maintain products, product families, components and functions; import starting data from spreadsheets. |
| **Shared workspaces** | Work from multiple PCs using a shared folder, with writer coordination, journals and local SQLite indexes. |
| **Backup & archive** | Preserve and archive workspace data. |
| **Languages** | Use the interface in English, Italian or French. |

### One workflow, from plan to evidence

1. **Set up the workspace.** Choose an empty folder and select a starting template (software QA, hardware engineering, manufacturing quality, research laboratory or blank).
2. **Define the scope.** Add your products, components, requirements and test cases, including step-by-step procedures.
3. **Run the tests.** Register each execution, its outcome and supporting evidence.
4. **Track findings.** Connect anomalies or change requests to the affected tests.
5. **Review and report.** Explore audit and coverage views, then export the relevant tables and reports to Excel.

## Download

**[Download RBG - QATRIVO for Windows](https://github.com/Ruba94-cyber/RBG-QATRIVO/releases/latest)**

1. Open the latest published GitHub release and download the **Windows x64 ZIP**.
2. Extract the complete ZIP into a folder of your choice.
3. Run **`RBG - QATRIVO.exe`**; the published application is packaged as a self-contained executable, so a separate .NET runtime is not needed to run it.
4. Choose where QATRIVO should keep your workspace data.

QATRIVO is built on **.NET 10 + Avalonia**. Windows is the tested desktop platform.

**About signing:** the executable uses the author's **self-signed code-signing certificate**. Windows may still display an unknown-publisher or SmartScreen warning. Before trusting a certificate, compare its fingerprint with the independently published information and verify the release-provided checksum. Read **[SIGNING.md](SIGNING.md)** for instructions.

## Quick start

### Working on your own

Create or select an empty local folder when prompted. Choose a suitable starting template, then begin with your products, test cases and executions. Your workspace data remains in the folder you selected.

### Working with a team

Point each PC at **the same workspace folder**, using a network share or a folder synchronized between PCs. QATRIVO coordinates writers with locks and journals; each PC keeps a local SQLite index for faster navigation, while the shared files remain authoritative.

For a reliable multi-PC setup:

- Make sure each user has the appropriate folder permissions and access to the share.
- Use the **same application version** across PCs and keep routine backups.
- Allow file synchronization to finish and avoid interrupting writes.
- Remember that **anyone with access to the shared folder can see its contents**, including evidence, names, notes and audit history.

For a separate test configuration, launch with `--config <folder>`.

## Guides & troubleshooting

- **[User guide (English)](GUIDE.md)** — installation, workspace configuration, multi-PC use and everyday workflows.
- **[Guida utente (Italiano)](GUIDA.md)** — guida all'installazione e all'utilizzo.
- **[Code signing and verification](SIGNING.md)** — certificate and package verification.
- **[Security policy](SECURITY.md)** — storage, privacy and vulnerability reporting.

**Common questions**

| Question | Answer |
| --- | --- |
| **Does it require a server or cloud account?** | No dedicated application server or online account is required. Workspace files can live locally or in a shared folder. |
| **Can several PCs use the same workspace?** | Yes. Use a reliable shared folder and keep the clients on the same version. |
| **Can I start with an existing populated folder?** | QATRIVO does not inject a starter template into a folder that already contains data. |
| **Where is my data?** | In the workspace folder you select; local SQLite indexes support navigation but are not the authoritative copy. |
| **Is the Windows download signed?** | Releases use an author-issued, self-signed certificate. See the verification guide before deciding whether to trust it. |

## Build from source

To build the application on a development machine, install the **.NET 10 SDK**.

```powershell
dotnet build TestLoom.slnx -c Release
dotnet run --project src/TestLoom.App/TestLoom.App.csproj -c Release
```

### Tests

```powershell
dotnet test src/TestLoom.App.Tests/TestLoom.App.Tests.csproj -c Release
dotnet test src/TestLoom.App.Tests.Headless/TestLoom.App.Tests.Headless.csproj -c Release
dotnet test src/TestLoom.App.UITests/TestLoom.App.UITests.csproj -c Release
```

The **FlaUI** suite exercises the real Windows application through UI Automation and requires an interactive Windows desktop session and built application. Passing automated tests does not remove the need to back up or validate important production data.

The main code lives in `src/TestLoom.App/`; the `src/TestLoom.App.MultiPcHost/` helper supports shared-folder testing.

## Privacy & feedback

QATRIVO does **not collect telemetry** or upload workspace content to an application service. It accesses folders through the operating system. If you choose a shared folder, access to the data is governed by that folder and its permissions. See the **[security policy](SECURITY.md)** for details.

Found a bug or have a suggestion? **[Open a GitHub issue](https://github.com/Ruba94-cyber/RBG-QATRIVO/issues)** with the application version, steps to reproduce and a *synthetic or sanitized* example where possible. Do not post confidential test records or customer data.

For security vulnerabilities, use **private vulnerability reporting** instead of a public issue.

## Support development

QATRIVO is independently developed and maintained. If it helps your testing workflow, you can support maintenance, quality assurance and future improvements with an **entirely optional donation**.

**[❤️ Support QATRIVO with a PayPal donation](https://www.paypal.com/donate?business=paolo.ruba.1913%40gmail.com&no_recurring=0&item_name=Support+QATRIVO&currency_code=EUR)**

A donation does **not** grant additional licensing rights or replace a separate commercial agreement when one is needed.

## License & credits

RBG - QATRIVO is **source-available** under the **[PolyForm Shield License 1.0.0](LICENSE)**. The license permits use, modification and sharing for permitted purposes, including internal company use, but **does not permit using it to provide a competing product**. Read the license itself for the controlling terms.

Preserve the `LICENSE` and `NOTICE` files, including the `Required Notice` line, with copies. Attribute the author (for example, “Based on RBG - QATRIVO by Paolo Rubagotti”). The **RBG - QATRIVO** name and its icon are not available for rebranding another product.

For licensing questions or arrangements beyond the existing terms, contact **paolo.ruba.1913@gmail.com**.

Third-party components retain their original licenses. See **[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)**.

---

<p align="center"><sub>Made by Paolo Rubagotti · <a href="https://github.com/Ruba94-cyber/RBG-QATRIVO/releases/latest">Download</a> · <a href="GUIDE.md">Documentation</a> · <a href="https://github.com/Ruba94-cyber/RBG-QATRIVO/issues">Feedback</a> · <a href="#support-development">Support the project</a></sub></p>
