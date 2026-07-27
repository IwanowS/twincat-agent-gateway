using System.Text;
using TwinCatGateway.Contracts;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeProjectFileChangeLeaseTests
{
    [Fact]
    public void CleanNeverClassifiesAFileChangeAsGeneratedNoise()
    {
        XaeProjectFileChangeResult result =
            XaeProjectFileChangeLease.ClassifyChangedProject(
                BuildAction.Clean,
                @"C:\project\Project.tsproj",
                Encoding.UTF8.GetBytes("<baseline />"),
                Encoding.UTF8.GetBytes("<current />"));

        Assert.Equal(
            ProjectChangeClassification.Unknown,
            result.Classification);
        Assert.Equal(0, result.MovedBlocks);
        Assert.Equal(0, result.ContentChanges);
    }
}
