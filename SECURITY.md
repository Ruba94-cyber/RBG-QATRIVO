# Security policy

TestLoom is an early technical preview that stores its data in local files on the user's computer. It has no network
features and does not collect telemetry.

## Reporting a vulnerability

Please do not open a public issue for a security problem. Use the private vulnerability reporting of this repository
(Security tab) or contact the author privately, and include the version, the steps to reproduce and, if possible,
a synthetic workspace file that shows the problem. Never attach real or confidential data.

## Scope

In scope: corruption or loss of workspace data, reading or writing outside the workspace folder, unsafe handling of
files (links, backups, locks) and CSV export injection. Out of scope: multi-user or network-share scenarios, which are
not supported in this version.
