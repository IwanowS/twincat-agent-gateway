using System.Linq;
using System.Text;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class TsProjectNoiseClassifierTests
{
    [Fact]
    public void ClassifiesKnownBlockReordering()
    {
        TsProjectClassificationResult result = Classify(
            Project(
                """
                <Project GUID="{A}" Name="First"><Value>1</Value></Project>
                <Project GUID="{B}" Name="Second"><Value>2</Value></Project>
                """),
            Project(
                """
                <Project GUID="{B}" Name="Second"><Value>2</Value></Project>
                <Project GUID="{A}" Name="First"><Value>1</Value></Project>
                """));

        Assert.Equal(
            ProjectChangeClassification.ExpectedReorderOnly,
            result.Classification);
        Assert.Equal(2, result.MovedBlocks);
        Assert.Equal(0, result.ContentChanges);
    }

    [Fact]
    public void ClassifiesLargeKnownBlockReordering()
    {
        string[] blocks = CreateBlocks(40);

        TsProjectClassificationResult result = Classify(
            Project(string.Concat(blocks)),
            Project(string.Concat(blocks.Reverse())));

        Assert.Equal(
            ProjectChangeClassification.ExpectedReorderOnly,
            result.Classification);
        Assert.Equal(40, result.MovedBlocks);
    }

    [Fact]
    public void ClassifiesInsignificantFormattingOnly()
    {
        TsProjectClassificationResult result = Classify(
            "<TcSmProject><Project><Plc>"
                + "<Project GUID=\"{A}\" Name=\"First\" />"
                + "</Plc></Project></TcSmProject>",
            """
            <TcSmProject>
              <Project>
                <Plc>
                  <Project Name="First" GUID="{A}" />
                </Plc>
              </Project>
            </TcSmProject>
            """);

        Assert.Equal(
            ProjectChangeClassification.WhitespaceOnly,
            result.Classification);
    }

    [Fact]
    public void DetectsContentChangeInsideMovedBlock()
    {
        TsProjectClassificationResult result = Classify(
            Project(
                """
                <Project GUID="{A}" Name="First"><Value>1</Value></Project>
                <Project GUID="{B}" Name="Second"><Value>2</Value></Project>
                """),
            Project(
                """
                <Project GUID="{B}" Name="Second"><Value>changed</Value></Project>
                <Project GUID="{A}" Name="First"><Value>1</Value></Project>
                """));

        Assert.Equal(
            ProjectChangeClassification.ContentChanged,
            result.Classification);
        Assert.True(result.ContentChanges > 0);
    }

    [Fact]
    public void DetectsAddedOrRemovedKnownBlock()
    {
        TsProjectClassificationResult result = Classify(
            Project(
                """
                <Project GUID="{A}" Name="First" />
                <Project GUID="{B}" Name="Second" />
                """),
            Project(
                """
                <Project GUID="{A}" Name="First" />
                """));

        Assert.Equal(
            ProjectChangeClassification.ContentChanged,
            result.Classification);
    }

    [Fact]
    public void ReturnsUnknownForDuplicateIdentity()
    {
        TsProjectClassificationResult result = Classify(
            Project(
                """
                <Project GUID="{A}" Name="Same"><Value>1</Value></Project>
                <Project GUID="{A}" Name="Same"><Value>2</Value></Project>
                """),
            Project(
                """
                <Project GUID="{A}" Name="Same"><Value>2</Value></Project>
                <Project GUID="{A}" Name="Same"><Value>1</Value></Project>
                """));

        Assert.Equal(
            ProjectChangeClassification.Unknown,
            result.Classification);
    }

    [Fact]
    public void ReturnsUnknownForInvalidXml()
    {
        TsProjectClassificationResult result = Classify(
            Project("<Project GUID=\"{A}\" />"),
            "<TcSmProject><Project>");

        Assert.Equal(
            ProjectChangeClassification.Unknown,
            result.Classification);
    }

    [Fact]
    public void ReturnsUnknownForChangeOutsideKnownContainer()
    {
        TsProjectClassificationResult result = Classify(
            """
            <TcSmProject TcVersion="3.1">
              <Project><System /></Project>
            </TcSmProject>
            """,
            """
            <TcSmProject TcVersion="3.2">
              <Project><System /></Project>
            </TcSmProject>
            """);

        Assert.Equal(
            ProjectChangeClassification.Unknown,
            result.Classification);
    }

    [Fact]
    public void RejectsDtdProcessing()
    {
        TsProjectClassificationResult result = Classify(
            Project("<Project GUID=\"{A}\" />"),
            """
            <!DOCTYPE TcSmProject [<!ENTITY value "unsafe">]>
            <TcSmProject><Project><Plc>&value;</Plc></Project></TcSmProject>
            """);

        Assert.Equal(
            ProjectChangeClassification.Unknown,
            result.Classification);
    }

    private static TsProjectClassificationResult Classify(
        string baseline,
        string current)
    {
        return TsProjectNoiseClassifier.Classify(
            Encoding.UTF8.GetBytes(baseline),
            Encoding.UTF8.GetBytes(current));
    }

    private static string Project(string content)
    {
        return "<TcSmProject><Project><Plc>"
            + content
            + "</Plc></Project></TcSmProject>";
    }

    private static string[] CreateBlocks(int count)
    {
        return Enumerable
            .Range(0, count)
            .Select(index =>
                $"<Project GUID=\"{{{index}}}\" "
                + $"Name=\"Project{index}\" />")
            .ToArray();
    }
}
