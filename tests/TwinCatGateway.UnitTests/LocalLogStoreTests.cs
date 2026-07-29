using System;
using System.IO;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class LocalLogStoreTests
{
    [Fact]
    public void ResourceRoundTripUsesCompactUriAndBoundedRead()
    {
        using TemporaryDirectory temporary = new();
        LocalLogStore store = new(temporary.Path);

        ResourceReference reference = store.WriteText(
            "operation-1",
            ResourceKind.BuildLog,
            "0123456789");
        ResourceContent resource = store.Read(reference.Uri, maximumCharacters: 5);

        Assert.Equal("twincat-log://operation-1/build", reference.Uri);
        Assert.Equal("01234", resource.Content);
        Assert.True(resource.Truncated);
        Assert.Equal(5, resource.NextOffset);
        Assert.Equal("text/plain", resource.ContentType);

        ResourceContent remainder = store.Read(
            reference.Uri,
            maximumCharacters: 5,
            offset: resource.NextOffset!.Value);
        Assert.Equal("56789", remainder.Content);
        Assert.False(remainder.Truncated);
        Assert.Null(remainder.NextOffset);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("operation/child")]
    [InlineData("operation:child")]
    [InlineData("")]
    public void OperationIdCannotEscapeLogRoot(string operationId)
    {
        using TemporaryDirectory temporary = new();
        LocalLogStore store = new(temporary.Path);

        Assert.Throws<ArgumentException>(
            () => store.WriteText(operationId, ResourceKind.BuildLog, "content"));
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("twincat-log://operation-1/unknown")]
    [InlineData("twincat-test://../xunit")]
    public void UnsupportedResourceUriIsRejected(string uri)
    {
        using TemporaryDirectory temporary = new();
        LocalLogStore store = new(temporary.Path);

        Assert.Throws<ArgumentException>(() => store.Read(uri));
    }

    [Fact]
    public void PruneRemovesOnlyExpiredOperationDirectories()
    {
        using TemporaryDirectory temporary = new();
        LocalLogStore store = new(temporary.Path);
        store.WriteText("expired-operation", ResourceKind.BuildLog, "old");
        store.WriteText("recent-operation", ResourceKind.BuildLog, "new");
        string unrelated = System.IO.Path.Combine(temporary.Path, "not.an.operation");
        Directory.CreateDirectory(unrelated);

        Directory.SetLastWriteTimeUtc(
            System.IO.Path.Combine(temporary.Path, "expired-operation"),
            DateTime.UtcNow.AddDays(-30));
        Directory.SetLastWriteTimeUtc(
            System.IO.Path.Combine(temporary.Path, "recent-operation"),
            DateTime.UtcNow);
        Directory.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-30));

        int removed = store.Prune(DateTimeOffset.UtcNow.AddDays(-14));

        Assert.Equal(1, removed);
        Assert.False(
            Directory.Exists(
                System.IO.Path.Combine(temporary.Path, "expired-operation")));
        Assert.True(
            Directory.Exists(
                System.IO.Path.Combine(temporary.Path, "recent-operation")));
        Assert.True(Directory.Exists(unrelated));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "TwinCatGatewayTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
