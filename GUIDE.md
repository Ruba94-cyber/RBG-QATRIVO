# RBG - QATRIVO user guide

This guide explains how to install RBG - QATRIVO, set up a shared workspace and use it day to day. An Italian version is in
[GUIDA.md](GUIDA.md).

## 1. Install

1. Download `RBG - QATRIVO-<version>-win-x64.zip` from the [Releases](../../releases) page and unzip it anywhere.
2. Run `RBG - QATRIVO.exe`. It is a single self-contained file: no installer and no .NET runtime are needed.
3. The program is signed with the author's personal certificate (self-signed, subject "Paolo Rubagotti"). Windows may
   show "Unknown publisher" or a SmartScreen warning because the certificate is not issued by a public authority.
   To make your PC recognize the signature, import the public certificate first:
   - verify the SHA-256 fingerprint of `Paolo-Rubagotti-CodeSigning.cer` against the one published in the release notes
     and in the repository (`SIGNING.md`);
   - run `powershell -ExecutionPolicy Bypass -File .\Import-Certificate.ps1` (add `-ForAllUsers` from an elevated window
     to trust it for every user of the PC). Only trust a certificate after verifying the fingerprint.
4. Check the file integrity with the `SHA256SUMS.txt` published next to the download.

## 2. First start and the data folder

On the first start RBG - QATRIVO asks for the **data folder**. All your data is stored there as ordinary files.

- **Working alone:** pick an empty folder on your disk.
- **Working as a team (multi-PC):** pick the *same* folder on every PC: a network share or a folder kept in sync by a
  sync client. Several people can have it open at the same time.

If the chosen folder is empty you can pick a **starting point for your kind of testing** (software QA, hardware
engineering, manufacturing quality, research laboratory, or a blank workspace) and get its first test templates.
Nothing is created in a folder that already holds data.

`--config <folder>` starts the program against an isolated configuration folder (useful for trying things out).

## 3. What you can do

- **Master data:** products, product families, components and functions; import from spreadsheets.
- **Requirements and test cases:** ordered steps, configurable instrument/tool fields, templates, results per product
  and component.
- **Executions:** recorded per operation with a fast, keyboard-driven editor, evidence files and history.
- **Anomalies and change requests:** classification, links to tests, a recovery bin for deleted items.
- **Audit and coverage:** coverage matrices, dashboards and charts.
- **Reports:** interactive Excel export of lists, matrices and reports.
- **Backup and archive** of the workspace data.
- **Languages:** English, Italian and French; change it in the settings.

## 4. Working with several PCs

- Everyone opens the same data folder. Writers are coordinated with locks and journals, so two people editing at the
  same time do not corrupt each other's work.
- A local SQLite index on every PC keeps large archives fast. It is rebuilt from the shared files and never replaces
  them: the files in the folder are the authoritative data.
- If a sync client produces conflicting copies, RBG - QATRIVO merges them instead of losing changes.
- Use a folder that is reliably available. Avoid pulling the cable on a PC while it is saving, and keep regular backups.

## 5. Good practice

- Make a backup before updating RBG - QATRIVO, and update every PC that shares the folder to the same version.
- Everyone with access to the folder can read what is stored there: tester and owner names, notes, evidence files and
  the audit trail.
- This is an early public release: treat it as a preview and keep backups.

## 6. Troubleshooting

- *Windows warns about an unknown publisher:* see step 3 of the installation.
- *The program cannot open the folder:* check that you have write permission and that the share is reachable.
- *A second PC does not see new data yet:* wait for the sync client to finish, then reopen the page.
- *Something looks wrong:* close the program, make a copy of the data folder and open an issue on GitHub with the
  version number and what you did.

## 7. License and credits

PolyForm Shield 1.0.0; see [LICENSE](LICENSE) and [NOTICE](NOTICE). Based on RBG - QATRIVO by Paolo Rubagotti.
