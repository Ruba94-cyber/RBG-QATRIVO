# Contributing

RBG - QATRIVO is generated from a larger application by an automatic process, so changes to the application code are not
made in this repository by hand: they would be overwritten by the next generation. Bug reports, reproduction steps
with synthetic data and suggestions are welcome as issues.

## Local checks

```sh
dotnet build TestLoom.slnx -c Release
dotnet test src/TestLoom.App.Tests/TestLoom.App.Tests.csproj -c Release
```

All test cases, requirements, workspaces and fixtures must be entirely synthetic. Never commit local workspace data,
credentials or private dependency paths, and never include real, confidential or personal data in an issue.

## Contribution terms

RBG - QATRIVO is source-available under the PolyForm Shield License 1.0.0 (see LICENSE and NOTICE). Until a contributor
agreement is published in this repository, code contributions are not accepted. Contact: paolo.ruba.1913@gmail.com.
