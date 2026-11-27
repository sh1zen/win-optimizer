using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace WinOptimizer.Infrastructure.Features.Benchmark;

/// <summary>Owns one reference to a graphics interface acquired through the native ABI.</summary>
internal sealed class GraphicsComObject : SafeHandleZeroOrMinusOneIsInvalid
{
    internal GraphicsComObject(IntPtr instance) : base(ownsHandle: true) => SetHandle(instance);

    internal T Method<T>(int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(handle), slot * IntPtr.Size));

    protected override bool ReleaseHandle()
    {
        Marshal.Release(handle);
        return true;
    }
}
