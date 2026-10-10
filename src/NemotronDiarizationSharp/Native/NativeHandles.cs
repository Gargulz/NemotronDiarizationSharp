using Microsoft.Win32.SafeHandles;

namespace NemotronDiarizationSharp.Native;

internal sealed class ModelHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal ModelHandle(nint handle) : base(ownsHandle: true) => SetHandle(handle);

    protected override bool ReleaseHandle()
    {
        try
        {
            NativeMethods.DiarDestroy(handle);
            return true;
        }
        catch
        {
            return false;
        }
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
        bool streamClosed = false;
        try
        {
            NativeMethods.DiarStreamClose(handle);
            streamClosed = true;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (streamClosed)
                Interlocked.Exchange(ref _modelOwner, null)?.DangerousRelease();
            // Если native stream не закрылся, удерживаем модель, чтобы не нарушить её lifetime.
        }
    }
}
