using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace TwinCatGateway.Core;

public sealed class PlcRuntimeTarget
{
    public PlcRuntimeTarget(
        string name,
        int adsPort)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? $"PLC port {adsPort}"
            : name;
        AdsPort = adsPort;
    }

    public string Name { get; }

    public int AdsPort { get; }
}

public static class TwinCatRuntimeTargetDiscovery
{
    public static IReadOnlyList<PlcRuntimeTarget> Discover(
        string twinCatProjectPath)
    {
        if (string.IsNullOrWhiteSpace(twinCatProjectPath))
        {
            throw new ArgumentException(
                "TwinCAT project path is required.",
                nameof(twinCatProjectPath));
        }

        string fullPath = Path.GetFullPath(
            twinCatProjectPath);
        XDocument document = XDocument.Load(
            fullPath,
            LoadOptions.None);
        Dictionary<int, PlcRuntimeTarget> targets = new();
        foreach (XElement project in document
            .Descendants()
            .Where(element =>
                string.Equals(
                    element.Name.LocalName,
                    "Project",
                    StringComparison.Ordinal)
                && element.Ancestors().Any(ancestor =>
                    string.Equals(
                        ancestor.Name.LocalName,
                        "Plc",
                        StringComparison.Ordinal))))
        {
            string? rawPort =
                (string?)project.Attribute("AmsPort");
            if (!int.TryParse(
                    rawPort,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int port)
                || port <= 0
                || port > ushort.MaxValue)
            {
                continue;
            }

            string? name =
                (string?)project.Attribute("Name");
            if (!targets.ContainsKey(port))
            {
                targets.Add(
                    port,
                    new PlcRuntimeTarget(
                        name ?? string.Empty,
                        port));
            }
        }

        return targets.Values
            .OrderBy(target => target.AdsPort)
            .ToArray();
    }
}
