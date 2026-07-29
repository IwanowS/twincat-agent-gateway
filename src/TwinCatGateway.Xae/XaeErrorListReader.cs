using System.Collections.Generic;
using EnvDTE;
using EnvDTE80;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Xae;

internal static class XaeErrorListReader
{
    public static IReadOnlyList<BuildDiagnostic> Read(DTE2 dte)
    {
        ToolWindows? toolWindows = null;
        ErrorList? errorList = null;
        ErrorItems? items = null;
        List<BuildDiagnostic> diagnostics = new();
        try
        {
            toolWindows = dte.ToolWindows;
            errorList = toolWindows.ErrorList;
            items = errorList.ErrorItems;
            int count = items.Count;
            for (int index = 1; index <= count; index++)
            {
                ErrorItem? item = null;
                try
                {
                    item = items.Item(index);
                    DiagnosticSeverity severity =
                        MapSeverity(item.ErrorLevel);
                    diagnostics.Add(
                        new BuildDiagnostic
                        {
                            Severity = severity,
                            Source = "xae-error-list",
                            Message = item.Description
                                ?? string.Empty,
                            File = string.IsNullOrWhiteSpace(
                                item.FileName)
                                ? null
                                : item.FileName,
                            Line = item.Line > 0
                                ? item.Line
                                : null,
                            Column = item.Column > 0
                                ? item.Column
                                : null,
                        });
                }
                finally
                {
                    ComObject.Release(item);
                }
            }

            return diagnostics;
        }
        finally
        {
            ComObject.Release(items);
            ComObject.Release(errorList);
            ComObject.Release(toolWindows);
        }
    }

    private static DiagnosticSeverity MapSeverity(
        vsBuildErrorLevel level)
    {
        switch (level)
        {
            case vsBuildErrorLevel.vsBuildErrorLevelHigh:
                return DiagnosticSeverity.Error;
            case vsBuildErrorLevel.vsBuildErrorLevelMedium:
                return DiagnosticSeverity.Warning;
            default:
                return DiagnosticSeverity.Info;
        }
    }
}
