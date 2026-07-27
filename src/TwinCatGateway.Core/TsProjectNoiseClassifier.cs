using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
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
    private const long MaximumXmlCharacters =
        32L * 1024L * 1024L;
    private const string TcSmProjectElement = "TcSmProject";
    private const string TcSmVersionAttribute = "TcSmVersion";
    private const string TcVersionAttribute = "TcVersion";

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

        try
        {
            XDocument baselineDocument = Parse(baseline);
            XDocument currentDocument = Parse(current);
            XElement? baselineRoot = baselineDocument.Root;
            XElement? currentRoot = currentDocument.Root;
            if (baselineRoot is null || currentRoot is null)
            {
                return Unknown(
                    "The TwinCAT project XML has no root element.");
            }

            if (baselineRoot.Name.NamespaceName.Length != 0
                || currentRoot.Name.NamespaceName.Length != 0
                || baselineRoot.Name.LocalName
                    != TcSmProjectElement
                || currentRoot.Name.LocalName
                    != TcSmProjectElement)
            {
                return Unknown(
                    "The XML root is not a supported TcSmProject.");
            }

            string? baselineTcSmVersion =
                AttributeValue(
                    baselineRoot,
                    TcSmVersionAttribute);
            string? baselineTcVersion =
                AttributeValue(
                    baselineRoot,
                    TcVersionAttribute);
            string? currentTcSmVersion =
                AttributeValue(
                    currentRoot,
                    TcSmVersionAttribute);
            string? currentTcVersion =
                AttributeValue(
                    currentRoot,
                    TcVersionAttribute);
            if (baselineTcSmVersion is null
                || baselineTcVersion is null
                || currentTcSmVersion is null
                || currentTcVersion is null)
            {
                return Unknown(
                    "TwinCAT project version metadata is missing.");
            }

            if (!string.Equals(
                    baselineTcSmVersion,
                    currentTcSmVersion,
                    StringComparison.Ordinal)
                || !string.Equals(
                    baselineTcVersion,
                    currentTcVersion,
                    StringComparison.Ordinal))
            {
                return Unknown(
                    "TwinCAT project version metadata changed, so one "
                    + "pinned schema cannot classify both files.");
            }

            TwinCatSchemaBundle bundle =
                TwinCatSchemaCatalog.GetMvpBundle();
            if (!bundle.SupportsTcSmProject(
                baselineTcSmVersion,
                baselineTcVersion))
            {
                return Unknown(
                    "No pinned TwinCAT project schema supports "
                    + $"TcSmVersion '{baselineTcSmVersion}' and "
                    + $"TcVersion '{baselineTcVersion}'.");
            }

            XmlSchemaSet schemas = bundle.CreateSchemaSet(
                TwinCatSchemaDocumentKind.TcSmProject);
            string? baselineValidationError =
                Validate(baselineDocument, schemas);
            if (baselineValidationError is not null)
            {
                return Unknown(
                    "The baseline TwinCAT project is not valid against "
                    + "the pinned schema: "
                    + baselineValidationError);
            }

            string? currentValidationError =
                Validate(currentDocument, schemas);
            if (currentValidationError is not null)
            {
                return Unknown(
                    "The current TwinCAT project is not valid against "
                    + "the pinned schema: "
                    + currentValidationError);
            }

            byte[] baselineOrdered = CanonicalHash(
                baselineRoot,
                allowSiblingPermutation: false);
            byte[] currentOrdered = CanonicalHash(
                currentRoot,
                allowSiblingPermutation: false);
            if (baselineOrdered.SequenceEqual(currentOrdered))
            {
                return new TsProjectClassificationResult(
                    ProjectChangeClassification.WhitespaceOnly,
                    movedBlocks: 0,
                    contentChanges: 0,
                    "Only insignificant XML formatting changed.");
            }

            byte[] baselinePermutation = CanonicalHash(
                baselineRoot,
                allowSiblingPermutation: true);
            byte[] currentPermutation = CanonicalHash(
                currentRoot,
                allowSiblingPermutation: true);
            if (baselinePermutation.SequenceEqual(
                currentPermutation))
            {
                return new TsProjectClassificationResult(
                    ProjectChangeClassification.ExpectedReorderOnly,
                    CountMovedBlocks(
                        baselineRoot,
                        currentRoot),
                    contentChanges: 0,
                    "Only unchanged XML subtrees were reordered in "
                    + "schema-valid TwinCAT projects.");
            }

            return new TsProjectClassificationResult(
                ProjectChangeClassification.ContentChanged,
                movedBlocks: 0,
                contentChanges: 1,
                "The schema-valid TwinCAT project contains a content "
                + "or structural change other than subtree reordering.");
        }
        catch (XmlException exception)
        {
            return Unknown(
                "The TwinCAT project XML is invalid: "
                + exception.Message);
        }
        catch (XmlSchemaException exception)
        {
            return Unknown(
                "The pinned TwinCAT project schema could not be used: "
                + exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Unknown(
                "The pinned TwinCAT project schema is unavailable: "
                + exception.Message);
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
        return XDocument.Load(
            reader,
            LoadOptions.SetLineInfo);
    }

    private static string? Validate(
        XDocument document,
        XmlSchemaSet schemas)
    {
        string? firstError = null;
        document.Validate(
            schemas,
            (_, args) =>
            {
                firstError ??= args.Message;
            },
            addSchemaInfo: false);
        return firstError;
    }

    private static string? AttributeValue(
        XElement element,
        string localName)
    {
        return element
            .Attributes()
            .FirstOrDefault(attribute =>
                attribute.Name.NamespaceName.Length == 0
                && attribute.Name.LocalName == localName)
            ?.Value;
    }

    private static byte[] CanonicalHash(
        XNode node,
        bool allowSiblingPermutation)
    {
        using MemoryStream canonical = new();
        using (BinaryWriter writer = new(
            canonical,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true),
            leaveOpen: true))
        {
            AppendCanonicalNode(
                writer,
                node,
                allowSiblingPermutation);
        }

        canonical.Position = 0;
        using SHA256 sha256 = SHA256.Create();
        return sha256.ComputeHash(canonical);
    }

    private static void AppendCanonicalNode(
        BinaryWriter writer,
        XNode node,
        bool allowSiblingPermutation)
    {
        if (node is XElement element)
        {
            AppendElement(
                writer,
                element,
                allowSiblingPermutation);
            return;
        }

        if (node is XText text)
        {
            WriteToken(writer, "text");
            WriteToken(writer, text.Value);
            return;
        }

        if (node is XComment comment)
        {
            WriteToken(writer, "comment");
            WriteToken(writer, comment.Value);
            return;
        }

        if (node is XProcessingInstruction instruction)
        {
            WriteToken(writer, "processing-instruction");
            WriteToken(writer, instruction.Target);
            WriteToken(writer, instruction.Data);
            return;
        }

        throw new InvalidOperationException(
            $"Unsupported XML node type '{node.NodeType}'.");
    }

    private static void AppendElement(
        BinaryWriter writer,
        XElement element,
        bool allowSiblingPermutation)
    {
        WriteToken(writer, "element");
        WriteExpandedName(writer, element.Name);

        XAttribute[] attributes = element
            .Attributes()
            .Where(attribute =>
                !attribute.IsNamespaceDeclaration)
            .OrderBy(
                attribute => attribute.Name.NamespaceName,
                StringComparer.Ordinal)
            .ThenBy(
                attribute => attribute.Name.LocalName,
                StringComparer.Ordinal)
            .ToArray();
        writer.Write(attributes.Length);
        foreach (XAttribute attribute in attributes)
        {
            WriteExpandedName(writer, attribute.Name);
            WriteToken(writer, attribute.Value);
        }

        XNode[] nodes = element.Nodes().ToArray();
        byte[][] childHashes = nodes
            .Select(node => CanonicalHash(
                node,
                allowSiblingPermutation))
            .ToArray();
        if (allowSiblingPermutation
            && nodes.All(node => node is XElement))
        {
            Array.Sort(
                childHashes,
                ByteArrayComparer.Instance);
        }

        writer.Write(childHashes.Length);
        foreach (byte[] childHash in childHashes)
        {
            writer.Write(childHash.Length);
            writer.Write(childHash);
        }
    }

    private static void WriteExpandedName(
        BinaryWriter writer,
        XName name)
    {
        WriteToken(writer, name.NamespaceName);
        WriteToken(writer, name.LocalName);
    }

    private static void WriteToken(
        BinaryWriter writer,
        string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static int CountMovedBlocks(
        XElement baseline,
        XElement current)
    {
        XNode[] baselineNodes = baseline.Nodes().ToArray();
        XNode[] currentNodes = current.Nodes().ToArray();
        if (baselineNodes.All(node => node is XElement)
            && currentNodes.All(node => node is XElement))
        {
            return CountMovedElementChildren(
                baselineNodes.Cast<XElement>().ToArray(),
                currentNodes.Cast<XElement>().ToArray());
        }

        int moved = 0;
        int count = Math.Min(
            baselineNodes.Length,
            currentNodes.Length);
        for (int index = 0; index < count; index++)
        {
            if (baselineNodes[index] is XElement baselineChild
                && currentNodes[index] is XElement currentChild)
            {
                moved += CountMovedBlocks(
                    baselineChild,
                    currentChild);
            }
        }

        return moved;
    }

    private static int CountMovedElementChildren(
        IReadOnlyList<XElement> baseline,
        IReadOnlyList<XElement> current)
    {
        Dictionary<string, Queue<int>> currentPositions =
            new(StringComparer.Ordinal);
        for (int index = 0; index < current.Count; index++)
        {
            string hash = HashKey(CanonicalHash(
                current[index],
                allowSiblingPermutation: true));
            if (!currentPositions.TryGetValue(
                hash,
                out Queue<int>? positions))
            {
                positions = new Queue<int>();
                currentPositions.Add(hash, positions);
            }

            positions.Enqueue(index);
        }

        int moved = 0;
        for (int baselineIndex = 0;
             baselineIndex < baseline.Count;
             baselineIndex++)
        {
            string hash = HashKey(CanonicalHash(
                baseline[baselineIndex],
                allowSiblingPermutation: true));
            if (!currentPositions.TryGetValue(
                    hash,
                    out Queue<int>? positions)
                || positions.Count == 0)
            {
                continue;
            }

            int currentIndex = positions.Dequeue();
            if (baselineIndex != currentIndex)
            {
                moved++;
            }

            moved += CountMovedBlocks(
                baseline[baselineIndex],
                current[currentIndex]);
        }

        return moved;
    }

    private static string HashKey(byte[] hash)
    {
        return BitConverter
            .ToString(hash)
            .Replace("-", string.Empty);
    }

    private static TsProjectClassificationResult Unknown(
        string reason)
    {
        return new TsProjectClassificationResult(
            ProjectChangeClassification.Unknown,
            movedBlocks: 0,
            contentChanges: 0,
            reason);
    }

    private sealed class ByteArrayComparer :
        IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int count = Math.Min(left.Length, right.Length);
            for (int index = 0; index < count; index++)
            {
                int comparison = left[index].CompareTo(right[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return left.Length.CompareTo(right.Length);
        }
    }
}
