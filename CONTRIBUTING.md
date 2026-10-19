# Contributing

NemotronDiarizationSharp is an independent, unofficial .NET wrapper with a verified Windows x64 CPU proof of concept. Please open an issue or discussion before starting a substantial change so the scope and upstream assumptions can be reviewed. Native runtime binaries are local development inputs; do not contribute them until redistribution rights are clear.

## Branches and commits

Use `main` as the integration branch and short, descriptive branches such as `docs/overview`, `feat/<topic>`, `fix/<topic>`, `ci/<topic>`, or `poc/<topic>`. Release branches and tags are created only as part of an approved release.

Use concise Conventional Commit subjects, for example `docs: clarify verified runtime support`. Keep each commit focused.

## Pull requests and review

Open a pull request with the motivation, scope, relevant design decisions, and user-visible impact. Link related issues, call out compatibility or licensing implications, and identify any behavior that remains unverified. Changes require maintainer review before merging.

## Validation

From the repository root with the .NET 10 SDK, run the following managed checks:

```powershell
dotnet restore NemotronDiarizationSharp.slnx -m:1
dotnet build NemotronDiarizationSharp.slnx -c Release --no-restore -m:1
dotnet test tests/NemotronDiarizationSharp.Tests/NemotronDiarizationSharp.Tests.csproj -c Release --no-build --no-restore -m:1 --filter "FullyQualifiedName!~WindowsNativeInferenceTests"
dotnet test tests/NemotronDiarizationSharp.Evaluation.Tests/NemotronDiarizationSharp.Evaluation.Tests.csproj -c Release --no-build --no-restore -m:1
dotnet test tests/NemotronDiarizationSharp.Integration.Tests/NemotronDiarizationSharp.Integration.Tests.csproj -c Release --no-build --no-restore -m:1 --filter "FullyQualifiedName!~ExplicitNativeIntegrationTests" -- xUnit.Explicit=off
dotnet test tests/NemotronDiarizationSharp.Integration.Tests/NemotronDiarizationSharp.Integration.Tests.csproj -c Release --no-build --no-restore -m:1 --filter "FullyQualifiedName~PublicApiContractTests" -- xUnit.Explicit=off
```

Native inference tests and explicit native integration tests require separately supplied compatible runtimes, models and audio fixtures. The managed commands exclude those tests; report native results separately. Do not claim Linux or broad GPU support from managed checks.

Runtime package production is a separate operation requiring staged native DLLs and the package-specific provenance and license checks. The `AllRuntimes` aggregation package also requires matching SDK, CPU runtime, and CUDA runtime packages in an approved package source; it is not part of ordinary managed restore/build. See its [packaging guidance](src/NemotronDiarizationSharp.AllRuntimes/README.md). Package restore or creation does not establish redistribution rights.

## Main branch and releases

When hosted on a forge, protect `main`, require review and applicable CI checks, and disallow force pushes. Release tags, NuGet packages, and public release artifacts require explicit maintainer approval. Do not publish credentials, model weights, recordings, build outputs, or package artifacts in pull requests.

## Third-party code and assets

Disclose new dependencies, copied code, native binaries, datasets, and model assets in the pull request. Include provenance and applicable license/notice text. Do not contribute assets unless redistribution rights are clear. A complete native dependency redistribution review is still pending.
