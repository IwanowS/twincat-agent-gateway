using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class TsProjectNoiseClassifierTests
{
    [Fact]
    public void ClassifiesSchemaValidProjectReordering()
    {
        XDocument baseline = LoadProject();
        XDocument current = new(baseline);
        ReversePlcProjects(current);

        TsProjectClassificationResult result = Classify(
            baseline,
            current);

        Assert.Equal(
            ProjectChangeClassification.ExpectedReorderOnly,
            result.Classification);
        Assert.Equal(2, result.MovedBlocks);
        Assert.Equal(0, result.ContentChanges);
    }

    [Fact]
    public void ClassifiesLargeSchemaValidProjectReordering()
    {
        XDocument baseline = LoadProject();
        XElement plc = Plc(baseline);
        XElement template = plc.Elements("Project").First();
        XElement[] projects = Enumerable
            .Range(0, 40)
            .Select(index =>
                CreateProjectClone(template, index))
            .ToArray();
        plc.ReplaceNodes(projects);
        XDocument current = new(baseline);
        Plc(current).ReplaceNodes(
            Plc(current)
                .Elements("Project")
                .Reverse()
                .Select(project => new XElement(project)));

        TsProjectClassificationResult result = Classify(
            baseline,
            current);

        Assert.Equal(
            ProjectChangeClassification.ExpectedReorderOnly,
            result.Classification);
        Assert.Equal(40, result.MovedBlocks);
    }

    [Fact]
    public void ClassifiesFormattingOnly()
    {
        XDocument baseline = LoadProject();
        string reformatted = baseline.ToString(
            SaveOptions.DisableFormatting);

        TsProjectClassificationResult result =
            TsProjectNoiseClassifier.Classify(
                Serialize(baseline),
                Encoding.UTF8.GetBytes(reformatted));

        Assert.Equal(
            ProjectChangeClassification.WhitespaceOnly,
            result.Classification);
    }

    [Fact]
    public void DetectsAttributeChangeInsideMovedBlock()
    {
        XDocument baseline = LoadProject();
        XDocument current = new(baseline);
        ReversePlcProjects(current);
        Plc(current)
            .Elements("Project")
            .First()
            .SetAttributeValue("Name", "ChangedName");

        TsProjectClassificationResult result = Classify(
            baseline,
            current);

        Assert.Equal(
            ProjectChangeClassification.ContentChanged,
            result.Classification);
        Assert.True(result.ContentChanges > 0);
    }

    [Fact]
    public void DetectsRemovedBlock()
    {
        XDocument baseline = LoadProject();
        XDocument current = new(baseline);
        Plc(current).Elements("Project").Last().Remove();

        TsProjectClassificationResult result = Classify(
            baseline,
            current);

        Assert.Equal(
            ProjectChangeClassification.ContentChanged,
            result.Classification);
    }

    [Fact]
    public void SupportsDuplicateCanonicalSubtreesAsMultiset()
    {
        XDocument baseline = LoadProject();
        XElement baselinePlc = Plc(baseline);
        XElement first =
            new(baselinePlc.Elements("Project").First());
        XElement second =
            new(baselinePlc.Elements("Project").Last());
        baselinePlc.ReplaceNodes(
            new XElement(first),
            new XElement(first),
            new XElement(second));
        XDocument current = new(baseline);
        Plc(current).ReplaceNodes(
            new XElement(first),
            new XElement(second),
            new XElement(first));

        TsProjectClassificationResult result = Classify(
            baseline,
            current);

        Assert.Equal(
            ProjectChangeClassification.ExpectedReorderOnly,
            result.Classification);
        Assert.Equal(2, result.MovedBlocks);
    }

    [Fact]
    public void DoesNotTreatCrossParentMoveAsReorderOnly()
    {
        XDocument baseline = LoadProject();
        XDocument current = new(baseline);
        XElement moved =
            Plc(current).Elements("Project").Last();
        moved.Remove();
        current.Root!
            .Element("Project")!
            .Element("System")!
            .Add(moved);

        TsProjectClassificationResult result = Classify(
            baseline,
            current);

        Assert.NotEqual(
            ProjectChangeClassification.ExpectedReorderOnly,
            result.Classification);
    }

    [Fact]
    public void ReturnsUnknownForInvalidXml()
    {
        TsProjectClassificationResult result =
            TsProjectNoiseClassifier.Classify(
                Serialize(LoadProject()),
                Encoding.UTF8.GetBytes(
                    "<TcSmProject><Project>"));

        Assert.Equal(
            ProjectChangeClassification.Unknown,
            result.Classification);
    }

    [Fact]
    public void ReturnsUnknownForSchemaInvalidProject()
    {
        XDocument baseline = LoadProject();
        XDocument current = new(baseline);
        current.Root!.Add(new XElement("Unexpected"));

        TsProjectClassificationResult result = Classify(
            baseline,
            current);

        Assert.Equal(
            ProjectChangeClassification.Unknown,
            result.Classification);
    }

    [Fact]
    public void ReturnsUnknownForUnsupportedVersion()
    {
        XDocument baseline = LoadProject();
        XDocument current = new(baseline);
        current.Root!.SetAttributeValue(
            "TcVersion",
            "3.1.4024.18");

        TsProjectClassificationResult result = Classify(
            baseline,
            current);

        Assert.Equal(
            ProjectChangeClassification.Unknown,
            result.Classification);
    }

    [Fact]
    public void RejectsDtdProcessing()
    {
        XDocument baseline = LoadProject();
        string current =
            "<!DOCTYPE TcSmProject "
            + "[<!ENTITY value \"unsafe\">]>"
            + baseline.ToString(SaveOptions.DisableFormatting);

        TsProjectClassificationResult result =
            TsProjectNoiseClassifier.Classify(
                Serialize(baseline),
                Encoding.UTF8.GetBytes(current));

        Assert.Equal(
            ProjectChangeClassification.Unknown,
            result.Classification);
    }

    private static TsProjectClassificationResult Classify(
        XDocument baseline,
        XDocument current)
    {
        return TsProjectNoiseClassifier.Classify(
            Serialize(baseline),
            Serialize(current));
    }

    private static byte[] Serialize(XDocument document)
    {
        return Encoding.UTF8.GetBytes(
            document.ToString());
    }

    private static XDocument LoadProject()
    {
        return XDocument.Load(
            Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "TC3_SimpleProject.tsproj"),
            LoadOptions.PreserveWhitespace);
    }

    private static XElement Plc(XDocument document)
    {
        return document.Root!
            .Element("Project")!
            .Element("Plc")!;
    }

    private static void ReversePlcProjects(
        XDocument document)
    {
        XElement plc = Plc(document);
        plc.ReplaceNodes(
            plc.Elements("Project")
                .Reverse()
                .Select(project => new XElement(project)));
    }

    private static XElement CreateProjectClone(
        XElement template,
        int index)
    {
        XElement project = new(template);
        project.SetAttributeValue(
            "GUID",
            $"{{00000000-0000-0000-0000-{index:000000000000}}}");
        project.SetAttributeValue("Name", $"PlcProject{index}");
        project.SetAttributeValue(
            "PrjFilePath",
            $"PlcProject{index}\\PlcProject{index}.plcproj");
        project.SetAttributeValue(
            "TmcFilePath",
            $"PlcProject{index}\\PlcProject{index}.tmc");
        project.SetAttributeValue(
            "AmsPort",
            851 + index);
        return project;
    }
}
