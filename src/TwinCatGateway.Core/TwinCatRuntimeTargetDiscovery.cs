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
        string runtimeId,
        string? project,
        string? instance,
        int adsPort)
    {
        RuntimeId = string.IsNullOrWhiteSpace(runtimeId)
            ? throw new ArgumentException(
                "Runtime id is required.",
                nameof(runtimeId))
            : runtimeId;
        Project = string.IsNullOrWhiteSpace(project)
            ? null
            : project;
        Instance = string.IsNullOrWhiteSpace(instance)
            ? null
            : instance;
        AdsPort = adsPort;
    }

    public string RuntimeId { get; }

    public string? Project { get; }

    public string? Instance { get; }

    public int AdsPort { get; }
}

public static class TwinCatRuntimeTargetDiscovery
{
    public static IReadOnlyList<PlcRuntimeTarget> Discover(
        string twinCatProjectPath,
        string? configuredRuntimeId = null,
        int? configuredRuntimePort = null)
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
                string runtimeId =
                    configuredRuntimePort == port
                    && !string.IsNullOrWhiteSpace(
                        configuredRuntimeId)
                        ? configuredRuntimeId!
                        : $"plc-{port}";
                targets.Add(
                    port,
                    new PlcRuntimeTarget(
                        runtimeId,
                        name,
                        instance: null,
                        adsPort: port));
            }
        }

        return targets.Values
            .OrderBy(target => target.AdsPort)
            .ToArray();
    }
}
