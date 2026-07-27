using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Schema;

namespace TwinCatGateway.Core;

internal enum TwinCatSchemaDocumentKind
{
    TcSmProject,
    TcPlcObject,
}

internal sealed class TwinCatSchemaBundle
{
    private const long MaximumSchemaCharacters =
        32L * 1024L * 1024L;
    private readonly IReadOnlyDictionary<string, byte[]> _files;
    private readonly SchemaDocumentDescriptor _tcSmProject;
    private readonly SchemaDocumentDescriptor _tcPlcObject;

    public TwinCatSchemaBundle(
        string twinCatXaeVersion,
        SchemaDocumentDescriptor tcSmProject,
        SchemaDocumentDescriptor tcPlcObject,
        IReadOnlyDictionary<string, byte[]> files)
    {
        TwinCatXaeVersion = twinCatXaeVersion;
        _tcSmProject = tcSmProject;
        _tcPlcObject = tcPlcObject;
        _files = files;
    }

    public string TwinCatXaeVersion { get; }

    public bool SupportsTcSmProject(
        string tcSmVersion,
        string tcVersion)
    {
        return _tcSmProject.Versions.Contains(
                tcSmVersion,
                StringComparer.Ordinal)
            && _tcSmProject.ProductVersions.Contains(
                tcVersion,
                StringComparer.Ordinal);
    }

    public bool SupportsTcPlcObject(string version)
    {
        return _tcPlcObject.Versions.Contains(
            version,
            StringComparer.Ordinal);
    }

    public XmlSchemaSet CreateSchemaSet(
        TwinCatSchemaDocumentKind kind)
    {
        SchemaDocumentDescriptor descriptor =
            kind == TwinCatSchemaDocumentKind.TcSmProject
                ? _tcSmProject
                : _tcPlcObject;
        BundleXmlResolver resolver = new(_files);
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = MaximumSchemaCharacters,
            XmlResolver = resolver,
        };
        Uri rootUri =
            BundleXmlResolver.CreateUri(descriptor.RootSchema);
        using MemoryStream stream = new(
            _files[descriptor.RootSchema],
            writable: false);
        using XmlReader reader = XmlReader.Create(
            stream,
            settings,
            rootUri.AbsoluteUri);
        XmlSchemaSet schemas = new()
        {
            XmlResolver = resolver,
        };
        schemas.Add(targetNamespace: null, reader);
        schemas.Compile();
        return schemas;
    }
}

internal sealed class SchemaDocumentDescriptor
{
    public SchemaDocumentDescriptor(
        string rootSchema,
        string rootElement,
        IReadOnlyList<string> versions,
        IReadOnlyList<string> productVersions)
    {
        RootSchema = rootSchema;
        RootElement = rootElement;
        Versions = versions;
        ProductVersions = productVersions;
    }

    public string RootSchema { get; }

    public string RootElement { get; }

    public IReadOnlyList<string> Versions { get; }

    public IReadOnlyList<string> ProductVersions { get; }
}

internal static class TwinCatSchemaCatalog
{
    public const string MvpTwinCatXaeVersion = "3.1.4024.17";

    private const string ResourcePrefix =
        "TwinCatGateway.Schemas.3.1.4024.17.";
    private const string ManifestResource =
        ResourcePrefix + "manifest.json";
    private static readonly JsonSerializerOptions ManifestJsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = true,
        };
    private static readonly Lazy<TwinCatSchemaBundle> MvpBundle =
        new(LoadMvpBundle);

    public static TwinCatSchemaBundle GetMvpBundle()
    {
        return MvpBundle.Value;
    }

    private static TwinCatSchemaBundle LoadMvpBundle()
    {
        Assembly assembly = typeof(TwinCatSchemaCatalog).Assembly;
        byte[] manifestContent = ReadResource(
            assembly,
            ManifestResource);
        SchemaManifest manifest =
            JsonSerializer.Deserialize<SchemaManifest>(
                manifestContent,
                ManifestJsonOptions)
            ?? throw new InvalidOperationException(
                "The embedded TwinCAT schema manifest is empty.");
        if (!string.Equals(
            manifest.TwinCatXaeVersion,
            MvpTwinCatXaeVersion,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The embedded TwinCAT schema manifest has an "
                + "unexpected XAE version.");
        }

        Dictionary<string, byte[]> files =
            new(StringComparer.Ordinal);
        foreach (SchemaFileManifest file in manifest.Files)
        {
            string path = NormalizeManifestPath(file.Path);
            byte[] content = ReadResource(
                assembly,
                ResourcePrefix
                    + path.Replace('/', '.'));
            string actualHash = ComputeSha256(content);
            if (!string.Equals(
                actualHash,
                file.Sha256,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Embedded TwinCAT schema hash mismatch: '{path}'.");
            }

            files.Add(path, content);
        }

        SupportedDocumentsManifest documents =
            manifest.SupportedDocuments
            ?? throw new InvalidOperationException(
                "The TwinCAT schema manifest has no document descriptors.");
        return new TwinCatSchemaBundle(
            manifest.TwinCatXaeVersion,
            CreateTcSmProjectDescriptor(documents.TcSmProject),
            CreateTcPlcObjectDescriptor(documents.TcPlcObject),
            files);
    }

    private static SchemaDocumentDescriptor
        CreateTcSmProjectDescriptor(
            TcSmProjectManifest? document)
    {
        if (document is null)
        {
            throw new InvalidOperationException(
                "The TwinCAT schema manifest has no "
                + "TcSmProject descriptor.");
        }

        return new SchemaDocumentDescriptor(
            NormalizeManifestPath(document.RootSchema),
            document.RootElement,
            document.TcSmVersions,
            document.TcVersions);
    }

    private static SchemaDocumentDescriptor
        CreateTcPlcObjectDescriptor(
            TcPlcObjectManifest? document)
    {
        if (document is null)
        {
            throw new InvalidOperationException(
                "The TwinCAT schema manifest has no "
                + "TcPlcObject descriptor.");
        }

        return new SchemaDocumentDescriptor(
            NormalizeManifestPath(document.RootSchema),
            document.RootElement,
            document.Versions,
            Array.Empty<string>());
    }

    private static byte[] ReadResource(
        Assembly assembly,
        string resourceName)
    {
        using Stream stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded TwinCAT schema resource was not found: "
                + $"'{resourceName}'.");
        using MemoryStream content = new();
        stream.CopyTo(content);
        return content.ToArray();
    }

    private static string NormalizeManifestPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                "A TwinCAT schema manifest path is empty.");
        }

        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Split('/').Any(segment =>
                string.Equals(
                    segment,
                    "..",
                    StringComparison.Ordinal)
                || segment.Length == 0))
        {
            throw new InvalidOperationException(
                $"TwinCAT schema manifest path is unsafe: '{path}'.");
        }

        return normalized;
    }

    private static string ComputeSha256(byte[] content)
    {
        using SHA256 algorithm = SHA256.Create();
        return BitConverter
            .ToString(algorithm.ComputeHash(content))
            .Replace("-", string.Empty)
            .ToLowerInvariant();
    }

    private sealed class SchemaManifest
    {
        public string TwinCatXaeVersion { get; set; } = string.Empty;

        public SupportedDocumentsManifest? SupportedDocuments
        {
            get;
            set;
        }

        public List<SchemaFileManifest> Files { get; set; } = new();
    }

    private sealed class SupportedDocumentsManifest
    {
        public TcSmProjectManifest? TcSmProject { get; set; }

        public TcPlcObjectManifest? TcPlcObject { get; set; }
    }

    private sealed class TcSmProjectManifest
    {
        public string RootSchema { get; set; } = string.Empty;

        public string RootElement { get; set; } = string.Empty;

        public List<string> TcSmVersions { get; set; } = new();

        public List<string> TcVersions { get; set; } = new();
    }

    private sealed class TcPlcObjectManifest
    {
        public string RootSchema { get; set; } = string.Empty;

        public string RootElement { get; set; } = string.Empty;

        public List<string> Versions { get; set; } = new();
    }

    private sealed class SchemaFileManifest
    {
        public string Path { get; set; } = string.Empty;

        public string Sha256 { get; set; } = string.Empty;
    }
}

internal sealed class BundleXmlResolver : XmlResolver
{
    private const string Scheme = "twincat-schema";
    private readonly IReadOnlyDictionary<string, byte[]> _files;

    public BundleXmlResolver(
        IReadOnlyDictionary<string, byte[]> files)
    {
        _files = files;
    }

    public override ICredentials? Credentials
    {
        set
        {
        }
    }

    public static Uri CreateUri(string path)
    {
        return new Uri(
            $"{Scheme}://bundle/{path}",
            UriKind.Absolute);
    }

    public override object GetEntity(
        Uri absoluteUri,
        string? role,
        Type? ofObjectToReturn)
    {
        if (!string.Equals(
                absoluteUri.Scheme,
                Scheme,
                StringComparison.Ordinal)
            || !string.Equals(
                absoluteUri.Host,
                "bundle",
                StringComparison.Ordinal)
            || (ofObjectToReturn is not null
                && ofObjectToReturn != typeof(Stream)))
        {
            throw new XmlException(
                $"TwinCAT schema URI is not allowed: "
                + $"'{absoluteUri}'.");
        }

        string path = Uri.UnescapeDataString(
                absoluteUri.AbsolutePath)
            .TrimStart('/');
        if (!_files.TryGetValue(path, out byte[]? content))
        {
            throw new XmlException(
                $"TwinCAT schema dependency is not bundled: "
                + $"'{path}'.");
        }

        return new MemoryStream(content, writable: false);
    }
}
