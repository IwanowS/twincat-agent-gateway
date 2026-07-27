using System;
using System.Runtime.InteropServices;

namespace TwinCatGateway.Xae;

internal static class ComObject
{
    public static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }
}
