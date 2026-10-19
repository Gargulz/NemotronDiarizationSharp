# All runtimes convenience package

This dependency-only package references the managed SDK and both Windows x64 runtime packages. Their native assets remain in separate application subdirectories so CPU and CUDA files with overlapping names do not overwrite one another.

This project remains in `NemotronDiarizationSharp.slnx`, but its package dependencies are disabled for ordinary solution restore/build. To aggregate packages, first produce the matching SDK, CPU runtime, and CUDA runtime packages through their separate workflows and place them in a local NuGet package source containing those matching artifacts. CPU/CUDA packages require their separately staged native inputs and applicable provenance/license checks; this project does not create placeholder runtime packages.

Restore and pack the aggregation package from that package source:

```powershell
dotnet restore src/NemotronDiarizationSharp.AllRuntimes/NemotronDiarizationSharp.AllRuntimes.csproj --source '<local-package-feed>' -p:EnableAllRuntimesPackageDependencies=true
dotnet pack src/NemotronDiarizationSharp.AllRuntimes/NemotronDiarizationSharp.AllRuntimes.csproj -c Release --no-restore -p:EnableAllRuntimesPackageDependencies=true
```

The opt-in property is required on both commands so restore and packing use the same dependency graph. Packing without it fails with an explanatory error. These commands describe the separate aggregation workflow; they have not been exercised as part of ordinary managed CI. Package creation does not grant redistribution rights; the applicable native-component review remains incomplete.

This package has not been published; its distribution requires satisfying the applicable CPU and CUDA runtime redistribution conditions. Its dependency metadata does not grant rights to bundled native components. The original project-owned SDK source and managed package use MIT; third-party components and model weights remain under their own terms.

See the [distribution status](../../docs/distribution/DISTRIBUTION_STATUS.md), [native provenance](../../docs/distribution/native-provenance.md), and [vendor redistribution evidence](../../docs/distribution/vendor-redistribution.md) for current scope and requirements.
