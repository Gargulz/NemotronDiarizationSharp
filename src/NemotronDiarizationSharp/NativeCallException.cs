using NemotronDiarizationSharp.Native;

namespace NemotronDiarizationSharp;

internal sealed class NativeCallException : InvalidOperationException
{
    private NativeCallException(NativeStatus status, string message) : base($"NeMo-Speech.cpp call failed ({status}): {message}") { }

    internal static NativeCallException Create(NativeStatus status)
    {
        string error = NativeMethods.GetLastError();
        return new NativeCallException(status, error);
    }
}
