# Contributing

The initial application is a local-first technical preview. Before accepting
external contributions, establish the project license, contribution terms, and
a reproducible release process.

## Local checks

```sh
dotnet build TestLoom.slnx -c Release
dotnet test src/TestLoom.Core.Tests/TestLoom.Core.Tests.csproj -c Release
dotnet run --project src/TestLoom.Core.Smoke/TestLoom.Core.Smoke.csproj -c Release
```

Keep general-purpose code in `TestLoom.Core`. All test cases, requirements,
workspaces and integration tests must use entirely synthetic examples.
Never commit local workspace data, credentials or private dependency paths.
 The desktop layer should not
require other products, remote private services, private fixtures, or a
shared development checkout. Any new sample data must be entirely synthetic.

## Contribution terms

TestLoom is source-available under the PolyForm Shield License 1.0.0 (see LICENSE and NOTICE). Until a contributor
agreement is published in this repository, code contributions are not accepted; issues, bug reports with synthetic data
and suggestions are welcome. Contact: paolo.ruba.1913@gmail.com. Do not submit code or data you do not have the right to share, and never include real,
confidential or personal data in an issue or a pull request.
