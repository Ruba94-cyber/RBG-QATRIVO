# Security policy

RBG - QATRIVO stores its data as files in a folder the user chooses (on the PC or shared between PCs) and keeps a local
SQLite index of them. It has no network code of its own (a shared folder is reached through the operating system) and
does not collect telemetry.

## Reporting a vulnerability

Please do not open a public issue for a security problem. Use the private vulnerability reporting of this repository
(Security tab) or write privately to paolo.ruba.1913@gmail.com, and include the version, the steps to reproduce and, if
possible, a synthetic workspace that shows the problem. Never attach real or confidential data.

## Scope

In scope: corruption or loss of workspace data, reading or writing outside the chosen folder, unsafe handling of files
(links, backups, locks, sync copies), and spreadsheet or CSV export injection. Out of scope: access control on a shared
folder (use the folder's own permissions; everyone who can write there is trusted) and hostile writers on that folder.
