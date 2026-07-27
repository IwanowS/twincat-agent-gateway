using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public sealed class TsProjectClassificationResult
{
    internal TsProjectClassificationResult(
        ProjectChangeClassification classification,
        int movedBlocks,
        int contentChanges,
        string reason)
    {
        Classification = classification;
        MovedBlocks = movedBlocks;
        ContentChanges = contentChanges;
        Reason = reason;
    }

    public ProjectChangeClassification Classification { get; }

    public int MovedBlocks { get; }

    public int ContentChanges { get; }

    public string Reason { get; }
}

public static class TsProjectNoiseClassifier
{
    private const long MaximumXmlCharacters = 32L * 1024L * 1024L;

    private static readonly HashSet<string> ReorderableContainers =
        new(StringComparer.Ordinal)
        {
            "Contexts",
            "Plc",
            "TaskPouOids",
            "Tasks",
        };

    private static readonly string[] IdentityAttributes =
    {
        "GUID",
        "Id",
        "Name",
        "OTCID",
        "ObjectId",
        "Path",
        "PrjFilePath",
    };

    private static readonly string[] IdentityElements =
    {
        "Name",
        "Id",
        "OTCID",
    };

    public static TsProjectClassificationResult Classify(
        byte[] baseline,
        byte[] current)
    {
        if (baseline is null)
        {
            throw new ArgumentNullException(nameof(baseline));
        }

        if (current is null)
        {
            throw new ArgumentNullException(nameof(current));
        }

        XDocument baselineDocument;
        XDocument currentDocument;
        try
        {
            baselineDocument = Parse(baseline);
            currentDocument = Parse(current);
        }
        catch (XmlException exception)
        {
            return Unknown(
                "The project XML is invalid: " + exception.Message);
        }

        XElement? baselineRoot = baselineDocument.Root;
        XElement? currentRoot = currentDocument.Root;
        if (baselineRoot is null || currentRoot is null)
        {
            return Unknown("The project XML has no root element.");
        }

        string baselineOrdered = Canonicalize(
            baselineRoot,
            reorderKnownContainers: false);
        string currentOrdered = Canonicalize(
            currentRoot,
            reorderKnownContainers: false);
        if (string.Equals(
            baselineOrdered,
            currentOrdered,
            StringComparison.Ordinal))
        {
            return new TsProjectClassificationResult(
                ProjectChangeClassification.WhitespaceOnly,
                movedBlocks: 0,
                contentChanges: 0,
                "Only insignificant XML formatting changed.");
        }

        try
        {
            string baselineReordered = Canonicalize(
                baselineRoot,
                reorderKnownContainers: true);
            string currentReordered = Canonicalize(
                currentRoot,
                reorderKnownContainers: true);
            if (string.Equals(
                baselineReordered,
                currentReordered,
                StringComparison.Ordinal))
            {
                return new TsProjectClassificationResult(
                    ProjectChangeClassification.ExpectedReorderOnly,
                    CountMovedBlocks(
                        baselineRoot,
                        currentRoot),
                    contentChanges: 0,
                    "Only known TwinCAT project blocks were reordered.");
            }

            string baselineBoundary =
                CanonicalizeKnownContainerBoundaries(baselineRoot);
            string currentBoundary =
                CanonicalizeKnownContainerBoundaries(currentRoot);
            if (!string.Equals(
                baselineBoundary,
                currentBoundary,
                StringComparison.Ordinal))
            {
                return Unknown(
                    "The change is outside a recognized reorderable "
                    + "TwinCAT project container.");
            }

            return new TsProjectClassificationResult(
                ProjectChangeClassification.ContentChanged,
                movedBlocks: 0,
                CountContentChanges(
                    baselineRoot,
                    currentRoot),
                "Content changed inside a recognized TwinCAT "
                + "project container.");
        }
        catch (AmbiguousProjectStructureException exception)
        {
            return Unknown(exception.Message);
        }
    }

    private static XDocument Parse(byte[] content)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = MaximumXmlCharacters,
            XmlResolver = null,
        };
        using MemoryStream stream = new(content, writable: false);
        using XmlReader reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static string Canonicalize(
        XElement element,
        bool reorderKnownContainers)
    {
        StringBuilder text = new();
        AppendCanonicalElement(
            text,
            element,
            reorderKnownContainers);
        return text.ToString();
    }

    private static void AppendCanonicalElement(
        StringBuilder text,
        XElement element,
        bool reorderKnownContainers)
    {
        AppendToken(text, "E");
        AppendToken(text, element.Name.NamespaceName);
        AppendToken(text, element.Name.LocalName);
        foreach (XAttribute attribute in element.Attributes()
            .OrderBy(
                item => item.Name.NamespaceName,
                StringComparer.Ordinal)
            .ThenBy(
                item => item.Name.LocalName,
                StringComparer.Ordinal))
        {
            AppendToken(text, "A");
            AppendToken(text, attribute.Name.NamespaceName);
            AppendToken(text, attribute.Name.LocalName);
            AppendToken(text, attribute.Value);
        }

        XNode[] nodes = OrderChildNodes(
            element,
            reorderKnownContainers);
        foreach (XNode node in nodes)
        {
            AppendCanonicalNode(
                text,
                node,
                reorderKnownContainers);
        }

        AppendToken(text, "/E");
    }

    private static XNode[] OrderChildNodes(
        XElement element,
        bool reorderKnownContainers)
    {
        XNode[] nodes = element.Nodes().ToArray();
        if (!reorderKnownContainers
            || !ReorderableContainers.Contains(
                element.Name.LocalName)
            || nodes.Length < 2)
        {
            return nodes;
        }

        if (nodes.Any(node => node is not XElement))
        {
            throw new AmbiguousProjectStructureException(
                $"The recognized container '{element.Name.LocalName}' "
                + "contains non-element content.");
        }

        List<KeyValuePair<string, XNode>> identified = nodes
            .Cast<XElement>()
            .Select(child =>
                new KeyValuePair<string, XNode>(
                    GetIdentity(child),
                    child))
            .ToList();
        EnsureUniqueIdentities(
            element.Name.LocalName,
            identified.Select(item => item.Key));
        return identified
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => item.Value)
            .ToArray();
    }

    private static void AppendCanonicalNode(
        StringBuilder text,
        XNode node,
        bool reorderKnownContainers)
    {
        switch (node)
        {
            case XElement child:
                AppendCanonicalElement(
                    text,
                    child,
                    reorderKnownContainers);
                break;
            case XCData value:
                AppendToken(text, "C");
                AppendToken(text, value.Value);
                break;
            case XText value:
                AppendToken(text, "T");
                AppendToken(text, value.Value);
                break;
            case XComment value:
                AppendToken(text, "M");
                AppendToken(text, value.Value);
                break;
            case XProcessingInstruction value:
                AppendToken(text, "P");
                AppendToken(text, value.Target);
                AppendToken(text, value.Data);
                break;
            default:
                throw new AmbiguousProjectStructureException(
                    $"Unsupported XML node '{node.NodeType}'.");
        }
    }

    private static string CanonicalizeKnownContainerBoundaries(
        XElement root)
    {
        StringBuilder text = new();
        AppendBoundaryElement(text, root);
        return text.ToString();
    }

    private static void AppendBoundaryElement(
        StringBuilder text,
        XElement element)
    {
        AppendToken(text, "E");
        AppendToken(text, element.Name.NamespaceName);
        AppendToken(text, element.Name.LocalName);
        foreach (XAttribute attribute in element.Attributes()
            .OrderBy(
                item => item.Name.NamespaceName,
                StringComparer.Ordinal)
            .ThenBy(
                item => item.Name.LocalName,
                StringComparer.Ordinal))
        {
            AppendToken(text, "A");
            AppendToken(text, attribute.Name.NamespaceName);
            AppendToken(text, attribute.Name.LocalName);
            AppendToken(text, attribute.Value);
        }

        if (ReorderableContainers.Contains(element.Name.LocalName))
        {
            AppendToken(text, "KNOWN-CONTENT");
        }
        else
        {
            foreach (XNode node in element.Nodes())
            {
                if (node is XElement child)
                {
                    AppendBoundaryElement(text, child);
                }
                else
                {
                    AppendCanonicalNode(
                        text,
                        node,
                        reorderKnownContainers: false);
                }
            }
        }

        AppendToken(text, "/E");
    }

    private static int CountMovedBlocks(
        XElement baseline,
        XElement current)
    {
        if (ReorderableContainers.Contains(
            baseline.Name.LocalName))
        {
            IReadOnlyList<XElement> baselineChildren =
                baseline.Elements().ToArray();
            IReadOnlyList<XElement> currentChildren =
                current.Elements().ToArray();
            string[] baselineIdentities =
                GetUniqueIdentities(baseline);
            string[] currentIdentities =
                GetUniqueIdentities(current);
            Dictionary<string, XElement> currentByIdentity =
                currentChildren
                    .Select((child, index) =>
                        new
                        {
                            Identity = currentIdentities[index],
                            Child = child,
                        })
                    .ToDictionary(
                        item => item.Identity,
                        item => item.Child,
                        StringComparer.Ordinal);
            int moved = baselineIdentities
                .Where((identity, index) =>
                    !string.Equals(
                        identity,
                        currentIdentities[index],
                        StringComparison.Ordinal))
                .Count();
            for (int index = 0;
                index < baselineChildren.Count;
                index++)
            {
                moved += CountMovedBlocks(
                    baselineChildren[index],
                    currentByIdentity[
                        baselineIdentities[index]]);
            }

            return moved;
        }

        XElement[] baselineElements =
            baseline.Elements().ToArray();
        XElement[] currentElements =
            current.Elements().ToArray();
        int count = Math.Min(
            baselineElements.Length,
            currentElements.Length);
        int total = 0;
        for (int index = 0; index < count; index++)
        {
            total += CountMovedBlocks(
                baselineElements[index],
                currentElements[index]);
        }

        return total;
    }

    private static int CountContentChanges(
        XElement baseline,
        XElement current)
    {
        if (string.Equals(
            Canonicalize(
                baseline,
                reorderKnownContainers: true),
            Canonicalize(
                current,
                reorderKnownContainers: true),
            StringComparison.Ordinal))
        {
            return 0;
        }

        if (ReorderableContainers.Contains(
            baseline.Name.LocalName))
        {
            Dictionary<string, XElement> baselineChildren =
                CreateIdentityMap(baseline);
            Dictionary<string, XElement> currentChildren =
                CreateIdentityMap(current);
            int changes = baselineChildren.Keys
                .Union(
                    currentChildren.Keys,
                    StringComparer.Ordinal)
                .Count(identity =>
                    !baselineChildren.TryGetValue(
                        identity,
                        out XElement? baselineChild)
                    || !currentChildren.TryGetValue(
                        identity,
                        out XElement? currentChild)
                    || !string.Equals(
                        Canonicalize(
                            baselineChild,
                            reorderKnownContainers: true),
                        Canonicalize(
                            currentChild,
                            reorderKnownContainers: true),
                        StringComparison.Ordinal));
            return Math.Max(1, changes);
        }

        XElement[] baselineElements =
            baseline.Elements().ToArray();
        XElement[] currentElements =
            current.Elements().ToArray();
        int count = Math.Min(
            baselineElements.Length,
            currentElements.Length);
        int total = 0;
        for (int index = 0; index < count; index++)
        {
            total += CountContentChanges(
                baselineElements[index],
                currentElements[index]);
        }

        return Math.Max(1, total);
    }

    private static Dictionary<string, XElement> CreateIdentityMap(
        XElement container)
    {
        IReadOnlyList<XElement> children =
            container.Elements().ToArray();
        string[] identities =
            GetUniqueIdentities(container);
        return children
            .Select((child, index) =>
                new
                {
                    Identity = identities[index],
                    Child = child,
                })
            .ToDictionary(
                item => item.Identity,
                item => item.Child,
                StringComparer.Ordinal);
    }

    private static string[] GetUniqueIdentities(
        XElement container)
    {
        string[] identities = container
            .Elements()
            .Select(GetIdentity)
            .ToArray();
        EnsureUniqueIdentities(
            container.Name.LocalName,
            identities);
        return identities;
    }

    private static string GetIdentity(XElement element)
    {
        List<string> parts = new()
        {
            element.Name.NamespaceName,
            element.Name.LocalName,
        };
        foreach (string attributeName in IdentityAttributes)
        {
            XAttribute? attribute = element.Attributes()
                .FirstOrDefault(item =>
                    string.Equals(
                        item.Name.LocalName,
                        attributeName,
                        StringComparison.Ordinal));
            if (attribute is not null)
            {
                parts.Add(attributeName);
                parts.Add(attribute.Value);
            }
        }

        if (parts.Count == 2)
        {
            XElement? identityElement = element.Elements()
                .FirstOrDefault(child =>
                    IdentityElements.Contains(
                        child.Name.LocalName,
                        StringComparer.Ordinal));
            if (identityElement is not null
                && !identityElement.HasElements)
            {
                parts.Add(identityElement.Name.LocalName);
                parts.Add(identityElement.Value);
            }
        }

        if (parts.Count == 2)
        {
            throw new AmbiguousProjectStructureException(
                $"The block '{element.Name.LocalName}' in a recognized "
                + "reorderable container has no stable identity.");
        }

        StringBuilder identity = new();
        foreach (string part in parts)
        {
            AppendToken(identity, part);
        }

        return identity.ToString();
    }

    private static void EnsureUniqueIdentities(
        string containerName,
        IEnumerable<string> identities)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string identity in identities)
        {
            if (!seen.Add(identity))
            {
                throw new AmbiguousProjectStructureException(
                    $"The recognized container '{containerName}' "
                    + "contains duplicate block identities.");
            }
        }
    }

    private static void AppendToken(
        StringBuilder target,
        string value)
    {
        target.Append(value.Length);
        target.Append(':');
        target.Append(value);
    }

    private static TsProjectClassificationResult Unknown(string reason)
    {
        return new TsProjectClassificationResult(
            ProjectChangeClassification.Unknown,
            movedBlocks: 0,
            contentChanges: 0,
            reason);
    }

    private sealed class AmbiguousProjectStructureException : Exception
    {
        public AmbiguousProjectStructureException(string message)
            : base(message)
        {
        }
    }
}
