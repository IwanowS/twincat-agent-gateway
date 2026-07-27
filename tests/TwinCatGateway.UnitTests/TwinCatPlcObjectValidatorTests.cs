using System;
using System.IO;
using System.Text;
using System.Xml.Linq;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class TwinCatPlcObjectValidatorTests
{
    [Fact]
    public void ValidatesPinnedFixture()
    {
        using FileStream content = File.OpenRead(FixturePath());

        TwinCatPlcObjectValidationResult result =
            TwinCatPlcObjectValidator.Validate(content);

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
    }

    [Fact]
    public void RejectsSchemaInvalidPlcObject()
    {
        XDocument document = XDocument.Load(FixturePath());
        document.Root!.Add(new XElement("Unexpected"));
        using MemoryStream content = Serialize(document);

        TwinCatPlcObjectValidationResult result =
            TwinCatPlcObjectValidator.Validate(content);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void RejectsUnsupportedPlcObjectVersion()
    {
        XDocument document = XDocument.Load(FixturePath());
        document.Root!.SetAttributeValue(
            "Version",
            "1.1.0.2");
        using MemoryStream content = Serialize(document);

        TwinCatPlcObjectValidationResult result =
            TwinCatPlcObjectValidator.Validate(content);

        Assert.False(result.IsValid);
        Assert.Contains("No pinned", result.Error);
    }

    [Fact]
    public void RejectsDtdProcessing()
    {
        string xml =
            "<!DOCTYPE TcPlcObject "
            + "[<!ENTITY value \"unsafe\">]>"
            + "<TcPlcObject Version=\"1.1.0.1\">"
            + "<POU Name=\"&value;\" "
            + "Id=\"{00000000-0000-0000-0000-000000000000}\" />"
            + "</TcPlcObject>";
        using MemoryStream content = new(
            Encoding.UTF8.GetBytes(xml));

        TwinCatPlcObjectValidationResult result =
            TwinCatPlcObjectValidator.Validate(content);

        Assert.False(result.IsValid);
        Assert.Contains("invalid", result.Error);
    }

    private static string FixturePath()
    {
        return Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "MAIN.TcPOU");
    }

    private static MemoryStream Serialize(
        XDocument document)
    {
        return new MemoryStream(
            Encoding.UTF8.GetBytes(document.ToString()));
    }
}
