using Microsoft.Win32.SafeHandles;

namespace NemotronDiarizationSharp.Native;

internal sealed class ModelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal ModelHandle(nint handle) : base(ownsHandle: true) => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        NativeMethods.DiarDestroy(handle);
        return true;
    }
}

internal sealed class StreamHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private ModelHandle? _modelOwner;

    internal StreamHandle(nint handle, ModelHandle modelOwner) : base(ownsHandle: true)
    {
        // OpenStream передаёт уже удерживаемую ссылку на модель.
        _modelOwner = modelOwner;
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.DiarStreamClose(handle);
        Interlocked.Exchange(ref _modelOwner, null)?.DangerousRelease();
        return true;
    }
}
