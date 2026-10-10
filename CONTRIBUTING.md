# Contributing

NemotronSharp is in the planning stage. Please open an issue or discussion before starting a substantial change so the scope and upstream assumptions can be reviewed.

## Branches and commits

Use `main` as the integration branch and short, descriptive branches such as `docs/overview`, `feat/<topic>`, `fix/<topic>`, `ci/<topic>`, or `poc/<topic>`. Release branches and tags are created only as part of an approved release.

Use concise commit subjects in the form `type: summary`, for example `docs: clarify planned runtime support`. Keep each commit focused.

## Pull requests and review

Open a pull request with the motivation, scope, relevant design decisions, and user-visible impact. Link related issues, call out compatibility or licensing implications, and identify any behavior that remains unverified. Changes require maintainer review before merging.

For code contributions, run the checks documented by the project once they exist and include the exact commands and results. Do not claim Windows/Linux runtime support, native ABI compatibility, or successful inference without evidence from those environments and pinned upstream inputs.

## Main branch and releases

When hosted on a forge, protect `main`, require review and applicable CI checks, and disallow force pushes. Release tags, NuGet packages, and public release artifacts require explicit maintainer approval. Do not publish credentials, model weights, recordings, build outputs, or package artifacts in pull requests.

## Third-party code and assets

Disclose new dependencies, copied code, native binaries, datasets, and model assets in the pull request. Include provenance and applicable license/notice text. Do not contribute assets unless redistribution rights are clear. A complete native dependency license inventory is still pending.
