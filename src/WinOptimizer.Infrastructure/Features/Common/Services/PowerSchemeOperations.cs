using System;
using System.Runtime.InteropServices;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Native;

namespace WinOptimizer.Infrastructure.Features.Common.Services;

/// <summary>
/// Wraps PowrProf.dll scheme calls behind an injectable interface.
/// </summary>
public class PowerSchemeOperations : IPowerSchemeOperations
{
    public uint GetActiveScheme(out Guid schemeGuid)
    {
        const uint errorInvalidData = 13;
        var result = PowerProf.PowerGetActiveScheme(IntPtr.Zero, out var pointer);
        try
        {
            if (result == PowerProf.ERROR_SUCCESS && pointer == IntPtr.Zero)
                result = errorInvalidData;
            schemeGuid = result == PowerProf.ERROR_SUCCESS ? Marshal.PtrToStructure<Guid>(pointer) : Guid.Empty;
            return result;
        }
        finally
        {
            if (pointer != IntPtr.Zero) PowerProf.LocalFree(pointer);
        }
    }

    public uint WriteValue(Guid schemeGuid, Guid subgroup, Guid setting, bool onAc, uint value) =>
        onAc
            ? PowerProf.PowerWriteACValueIndex(IntPtr.Zero, ref schemeGuid, ref subgroup, ref setting, value)
            : PowerProf.PowerWriteDCValueIndex(IntPtr.Zero, ref schemeGuid, ref subgroup, ref setting, value);

    public uint DeleteScheme(Guid schemeGuid)
    {
        return PowerProf.PowerDeleteScheme(IntPtr.Zero, ref schemeGuid);
    }

    public uint DuplicateScheme(Guid sourceGuid, out Guid destinationGuid)
    {
        var result = PowerProf.PowerDuplicateScheme(IntPtr.Zero, ref sourceGuid, out var destPtr);
        if (result == PowerProf.ERROR_SUCCESS)
        {
            destinationGuid = Marshal.PtrToStructure<Guid>(destPtr);
            PowerProf.LocalFree(destPtr);
        }
        else
        {
            destinationGuid = Guid.Empty;
        }
        return result;
    }

    public uint SetActiveScheme(Guid schemeGuid)
    {
        return PowerProf.PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid);
    }

    public uint WriteFriendlyName(Guid schemeGuid, string name)
    {
        var nameBytes = (uint)(name.Length * 2 + 2);
        return PowerProf.PowerWriteFriendlyName(
            IntPtr.Zero, ref schemeGuid, IntPtr.Zero, IntPtr.Zero, name, nameBytes);
    }

    public uint WriteDescription(Guid schemeGuid, string description)
    {
        var descBytes = (uint)(description.Length * 2 + 2);
        return PowerProf.PowerWriteDescription(
            IntPtr.Zero, ref schemeGuid, IntPtr.Zero, IntPtr.Zero, description, descBytes);
    }
}
