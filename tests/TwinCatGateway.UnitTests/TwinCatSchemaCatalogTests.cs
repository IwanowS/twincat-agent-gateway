using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Schema;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class TwinCatSchemaCatalogTests
{
    [Fact]
    public void MvpBundleSupportsPinnedDocumentVersions()
    {
        TwinCatSchemaBundle bundle =
            TwinCatSchemaCatalog.GetMvpBundle();

        Assert.Equal("3.1.4024.17", bundle.TwinCatXaeVersion);
        Assert.True(bundle.SupportsTcSmProject(
            "1.0",
            "3.1.4024.17"));
        Assert.True(bundle.SupportsTcPlcObject("1.1.0.1"));
        Assert.False(bundle.SupportsTcSmProject(
            "1.0",
            "3.1.4024.18"));
        Assert.False(bundle.SupportsTcPlcObject("1.1.0.2"));
    }

    [Theory]
    [InlineData(
        "TC3_SimpleProject.tsproj",
        "TcSmProject")]
    [InlineData(
        "MAIN.TcPOU",
        "TcPlcObject")]
    public void PinnedFixtureValidatesAgainstBundledSchema(
        string fileName,
        string kindName)
    {
        TwinCatSchemaBundle bundle =
            TwinCatSchemaCatalog.GetMvpBundle();
        TwinCatSchemaDocumentKind kind =
            kindName == "TcSmProject"
                ? TwinCatSchemaDocumentKind.TcSmProject
                : TwinCatSchemaDocumentKind.TcPlcObject;
        XmlSchemaSet schemas = bundle.CreateSchemaSet(kind);
        List<string> errors = new();
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            Schemas = schemas,
            ValidationType = ValidationType.Schema,
        };
        settings.ValidationEventHandler += (_, args) =>
            errors.Add(args.Message);

        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            fileName);
        using XmlReader reader = XmlReader.Create(path, settings);
        while (reader.Read())
        {
        }

        Assert.Empty(errors);
    }
}
