# CUDA runtime package

This package carries the staged NVIDIA NeMo-Speech.cpp v0.2.0 CUDA runtime for Windows x64 under `runtimes/nemotron/cuda/win-x64/native`. Its build-transitive target preserves this directory in application build and publish output so it can coexist with the CPU runtime package.

CUDA requires a compatible NVIDIA GPU and driver. `nvidia-smi` visibility is only a pre-load device check; the application must still verify model creation and inference on its host. GGUF model files are external and are not included.

This native runtime package has not been published. Third-party binary redistribution and provenance review remain incomplete; the SDK license does not grant permission to redistribute these binaries.

The package includes upstream reference license/notice files, the Darts-clone
notice, and the CUDA 12.9.1 EULA under `licenses/`. These are separate
component terms, not one license for the package. The shared CPU notice set
includes a historical CPU-specific `RUNTIME_COMPONENTS.md`; it does not
describe CUDA binary composition. See the [distribution status](../../docs/distribution/DISTRIBUTION_STATUS.md),
[native provenance](../../docs/distribution/native-provenance.md), and
[vendor redistribution evidence](../../docs/distribution/vendor-redistribution.md)
for the NVIDIA and Microsoft conditions and incomplete static-link provenance.
