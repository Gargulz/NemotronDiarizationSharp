# Contributing

NemotronDiarizationSharp is an independent, unofficial .NET wrapper with a verified Windows x64 CPU proof of concept. Please open an issue or discussion before starting a substantial change so the scope and upstream assumptions can be reviewed. Native runtime binaries are local development inputs; do not contribute them until redistribution rights are clear.

## Branches and commits

Use `main` as the integration branch and short, descriptive branches such as `docs/overview`, `feat/<topic>`, `fix/<topic>`, `ci/<topic>`, or `poc/<topic>`. Release branches and tags are created only as part of an approved release.

Use concise Conventional Commit subjects, for example `docs: clarify verified runtime support`. Keep each commit focused.

## Pull requests and review

Open a pull request with the motivation, scope, relevant design decisions, and user-visible impact. Link related issues, call out compatibility or licensing implications, and identify any behavior that remains unverified. Changes require maintainer review before merging.

For code contributions, run the relevant checks and include the exact commands and results. The current verification commands are `dotnet build NemotronDiarizationSharp.sln -c Release` and `dotnet test NemotronDiarizationSharp.sln -c Release`. Do not claim Linux runtime support or other unverified capabilities without evidence from those environments and pinned upstream inputs.

## Main branch and releases

When hosted on a forge, protect `main`, require review and applicable CI checks, and disallow force pushes. Release tags, NuGet packages, and public release artifacts require explicit maintainer approval. Do not publish credentials, model weights, recordings, build outputs, or package artifacts in pull requests.

## Third-party code and assets

Disclose new dependencies, copied code, native binaries, datasets, and model assets in the pull request. Include provenance and applicable license/notice text. Do not contribute assets unless redistribution rights are clear. A complete native dependency redistribution review is still pending.
