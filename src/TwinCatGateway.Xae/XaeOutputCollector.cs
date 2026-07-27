using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;

namespace TwinCatGateway.Xae;

public sealed class XaeOutputDelta
{
    internal XaeOutputDelta(
        string paneName,
        string paneGuid,
        string text)
    {
        PaneName = paneName;
        PaneGuid = paneGuid;
        Text = text;
    }

    public string PaneName { get; }

    public string PaneGuid { get; }

    public string Text { get; }
}

internal sealed class XaeOutputSnapshot
{
    internal XaeOutputSnapshot(
        IDictionary<string, int> offsets)
    {
        Offsets = new Dictionary<string, int>(
            offsets,
            StringComparer.OrdinalIgnoreCase);
    }

    internal IReadOnlyDictionary<string, int> Offsets { get; }
}

internal static class XaeOutputCollector
{
    public static XaeOutputSnapshot Capture(DTE2 dte)
    {
        Dictionary<string, int> offsets =
            new(StringComparer.OrdinalIgnoreCase);
        VisitPanes(
            dte,
            (pane, document, endPoint) =>
            {
                offsets[GetPaneKey(pane)] =
                    endPoint.AbsoluteCharOffset;
            });
        return new XaeOutputSnapshot(offsets);
    }

    public static IReadOnlyList<XaeOutputDelta> ReadDelta(
        DTE2 dte,
        XaeOutputSnapshot snapshot)
    {
        List<XaeOutputDelta> output = new();
        VisitPanes(
            dte,
            (pane, document, endPoint) =>
            {
                string key = GetPaneKey(pane);
                int endOffset = endPoint.AbsoluteCharOffset;
                int startOffset =
                    snapshot.Offsets.TryGetValue(
                        key,
                        out int capturedOffset)
                        && capturedOffset <= endOffset
                            ? capturedOffset
                            : 1;
                if (startOffset == endOffset)
                {
                    return;
                }

                TextPoint? startPoint = null;
                EditPoint? editPoint = null;
                try
                {
                    startPoint = document.StartPoint;
                    editPoint = startPoint.CreateEditPoint();
                    editPoint.MoveToAbsoluteOffset(startOffset);
                    string text = editPoint.GetText(endPoint);
                    if (!string.IsNullOrEmpty(text))
                    {
                        output.Add(
                            new XaeOutputDelta(
                                pane.Name ?? string.Empty,
                                pane.Guid ?? string.Empty,
                                text));
                    }
                }
                finally
                {
                    ComObject.Release(editPoint);
                    ComObject.Release(startPoint);
                }
            });
        return output
            .OrderBy(
                pane => pane.PaneName,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                pane => pane.PaneGuid,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void VisitPanes(
        DTE2 dte,
        Action<OutputWindowPane, TextDocument, TextPoint> visitor)
    {
        ToolWindows? toolWindows = null;
        OutputWindow? outputWindow = null;
        OutputWindowPanes? panes = null;
        try
        {
            toolWindows = dte.ToolWindows;
            outputWindow = toolWindows.OutputWindow;
            panes = outputWindow.OutputWindowPanes;
            int count = panes.Count;
            for (int index = 1; index <= count; index++)
            {
                OutputWindowPane? pane = null;
                TextDocument? document = null;
                TextPoint? endPoint = null;
                try
                {
                    pane = panes.Item(index);
                    document = pane.TextDocument;
                    endPoint = document.EndPoint;
                    visitor(pane, document, endPoint);
                }
                catch (COMException exception)
                {
                    Trace.TraceWarning(
                        "XAE Output pane {0} could not be read: {1}",
                        index,
                        exception);
                }
                finally
                {
                    ComObject.Release(endPoint);
                    ComObject.Release(document);
                    ComObject.Release(pane);
                }
            }
        }
        finally
        {
            ComObject.Release(panes);
            ComObject.Release(outputWindow);
            ComObject.Release(toolWindows);
        }
    }

    private static string GetPaneKey(OutputWindowPane pane)
    {
        string guid = pane.Guid ?? string.Empty;
        return string.IsNullOrWhiteSpace(guid)
            ? "name:" + (pane.Name ?? string.Empty)
            : "guid:" + guid;
    }
}
