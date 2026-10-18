namespace NemotronDiarizationSharp.Native;

internal sealed class NativeRuntimeSelectionState
{
    private readonly object _gate = new();
    private SelectedNativeRuntime? _selected;

    internal DiarizationBackend? SelectedBackend
    {
        get { lock (_gate) return _selected?.Backend; }
    }

    internal SelectedNativeRuntime EnsureSelected(DiarizationBackend requested,
        int? requestedDeviceIndex, Func<SelectedNativeRuntime> select)
    {
        lock (_gate)
        {
            SelectedNativeRuntime candidate = _selected ?? select();
            if (_selected is not null && requested != DiarizationBackend.Auto && requested != _selected.Backend)
                throw new InvalidOperationException(
                    $"The process native backend is already fixed to {_selected.Backend}; it cannot be switched to {requested}. Start a new process to use another backend.");

            if (candidate.Backend == DiarizationBackend.Cpu && requestedDeviceIndex.HasValue)
                throw new InvalidOperationException("GpuDeviceIndex requires CUDA, but the process selected the CPU backend.");

            _selected ??= candidate;
            return _selected;
        }
    }
}
