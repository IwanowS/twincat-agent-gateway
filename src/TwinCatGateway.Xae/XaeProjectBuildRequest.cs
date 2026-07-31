using System;
using Microsoft.VisualStudio.Shell.Interop;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Xae;

internal static class XaeProjectBuildRequest
{
    public static uint SelectFlags(BuildAction action)
    {
        VSSOLNBUILDUPDATEFLAGS flags = action switch
        {
            BuildAction.Clean =>
                VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_CLEAN,
            BuildAction.Rebuild =>
                VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_CLEAN
                | VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_BUILD,
            _ => throw new ArgumentOutOfRangeException(
                nameof(action),
                action,
                "Only PLC Clean and Rebuild use the VSSDK project update path."),
        };
        return unchecked((uint)flags);
    }
}
