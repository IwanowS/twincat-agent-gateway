using System;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace TwinCatGateway.Core;

public sealed class TwinCatPlcObjectValidationResult
{
    internal TwinCatPlcObjectValidationResult(
        bool isValid,
        string? error)
    {
        IsValid = isValid;
        Error = error;
    }

    public bool IsValid { get; }

    public string? Error { get; }
}

public static class TwinCatPlcObjectValidator
{
    private const long MaximumXmlCharacters =
        32L * 1024L * 1024L;

    public static TwinCatPlcObjectValidationResult Validate(
        Stream content)
    {
        if (content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        try
        {
            XDocument document = Parse(content);
            XElement? root = document.Root;
            if (root is null
                || root.Name.NamespaceName.Length != 0
                || root.Name.LocalName != "TcPlcObject")
            {
                return Invalid(
                    "The XML root is not TcPlcObject.");
            }

            string? version = root
                .Attributes()
                .FirstOrDefault(attribute =>
                    attribute.Name.NamespaceName.Length == 0
                    && attribute.Name.LocalName == "Version")
                ?.Value;
            if (version is null)
            {
                return Invalid(
                    "TcPlcObject Version is missing.");
            }

            TwinCatSchemaBundle bundle =
                TwinCatSchemaCatalog.GetMvpBundle();
            if (!bundle.SupportsTcPlcObject(version))
            {
                return Invalid(
                    "No pinned TwinCAT PLC object schema supports "
                    + $"Version '{version}'.");
            }

            XmlSchemaSet schemas = bundle.CreateSchemaSet(
                TwinCatSchemaDocumentKind.TcPlcObject);
            string? firstError = null;
            document.Validate(
                schemas,
                (_, args) =>
                {
                    firstError ??= args.Message;
                },
                addSchemaInfo: false);
            return firstError is null
                ? new TwinCatPlcObjectValidationResult(
                    isValid: true,
                    error: null)
                : Invalid(firstError);
        }
        catch (XmlException exception)
        {
            return Invalid(
                "The PLC object XML is invalid: "
                + exception.Message);
        }
        catch (XmlSchemaException exception)
        {
            return Invalid(
                "The pinned PLC object schema could not be used: "
                + exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Invalid(
                "The pinned PLC object schema is unavailable: "
                + exception.Message);
        }
    }

    private static XDocument Parse(Stream content)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = MaximumXmlCharacters,
            XmlResolver = null,
        };
        using XmlReader reader = XmlReader.Create(
            content,
            settings);
        return XDocument.Load(
            reader,
            LoadOptions.SetLineInfo);
    }

    private static TwinCatPlcObjectValidationResult Invalid(
        string error)
    {
        return new TwinCatPlcObjectValidationResult(
            isValid: false,
            error);
    }
}
