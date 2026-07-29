using System;
using System.Collections.Generic;
using System.Linq;
using TwinCatGateway.Contracts;
using TwinCatGateway.Core;
using TwinCatGateway.Xae;
using Xunit;

namespace TwinCatGateway.IntegrationTests;

public sealed class XaeWorkspaceFileChangeGuardTests
{
    private const string MainPath =
        @"C:\project\PlcProject\POUs\MAIN.TcPOU";
    private const string ProjectPath =
        @"C:\project\Project.tsproj";

    [Fact]
    public void AcquireProtectsProjectAndOpenDocumentUntilDispose()
    {
        FakeBackend backend = new();
        backend.OpenDocuments.Add(MainPath);

        XaeWorkspaceFileChangeGuard guard =
            XaeWorkspaceFileChangeGuard.Acquire(
                backend,
                CreatePaths());

        Assert.True(guard.IsActive);
        Assert.Equal(
            new[]
            {
                $"project:{MainPath}:True",
                $"project:{ProjectPath}:True",
                "tracking:start",
                $"document:{MainPath}:True",
            },
            backend.Operations);

        guard.Dispose();

        Assert.Equal("tracking:stop", backend.Operations[4]);
        Assert.Contains(
            $"document:{MainPath}:False",
            backend.Operations);
        Assert.Equal(
            $"project:{MainPath}:False",
            backend.Operations[backend.Operations.Count - 1]);
        Assert.True(backend.Disposed);
    }

    [Fact]
    public void DocumentOpenedAfterAcquireIsProtectedImmediately()
    {
        FakeBackend backend = new();
        using XaeWorkspaceFileChangeGuard guard =
            XaeWorkspaceFileChangeGuard.Acquire(
                backend,
                CreatePaths());

        backend.RaiseOpened(MainPath);
        backend.RaiseClosing(MainPath);

        Assert.Contains(
            $"document:{MainPath}:True",
            backend.Operations);
        Assert.Contains(
            $"document:{MainPath}:False",
            backend.Operations);
        Assert.True(guard.IsActive);
    }

    [Fact]
    public void PartialAcquireRollsBackProtectedPaths()
    {
        FakeBackend backend = new()
        {
            FailProjectPath = ProjectPath,
        };

        GatewayOperationException exception =
            Assert.Throws<GatewayOperationException>(
                () => XaeWorkspaceFileChangeGuard.Acquire(
                    backend,
                    CreatePaths()));

        Assert.Equal(
            ErrorCodes.XaeFileChangeGuardFailed,
            exception.Code);
        Assert.Contains(
            $"project:{MainPath}:False",
            backend.Operations);
        Assert.True(backend.Disposed);
    }

    [Fact]
    public void SyncAcknowledgesFileWithoutReleasingSessionGuard()
    {
        FakeBackend backend = new();
        using XaeWorkspaceFileChangeGuard guard =
            XaeWorkspaceFileChangeGuard.Acquire(
                backend,
                CreatePaths());

        guard.SyncProjectFile(MainPath);

        Assert.True(guard.IsProjectFileGuarded(MainPath));
        Assert.Contains($"sync:{MainPath}", backend.Operations);
        Assert.DoesNotContain(
            $"project:{MainPath}:False",
            backend.Operations);
    }

    [Fact]
    public void UpdateAddsNewPathsBeforeRemovingOldPaths()
    {
        const string replacement =
            @"C:\project\PlcProject\POUs\NEXT.TcPOU";
        FakeBackend backend = new();
        using XaeWorkspaceFileChangeGuard guard =
            XaeWorkspaceFileChangeGuard.Acquire(
                backend,
                CreatePaths());
        int baseline = backend.Operations.Count;

        guard.UpdatePaths(
            new[]
            {
                new XaeWorkspaceGuardPath(
                    replacement,
                    protectOpenDocument: true),
                new XaeWorkspaceGuardPath(
                    ProjectPath,
                    protectOpenDocument: false),
            });

        string[] update = backend.Operations
            .Skip(baseline)
            .ToArray();
        Assert.Equal(
            $"project:{replacement}:True",
            update[0]);
        Assert.Equal(
            $"project:{MainPath}:False",
            update[update.Length - 1]);
        Assert.True(guard.IsProjectFileGuarded(replacement));
        Assert.False(guard.IsProjectFileGuarded(MainPath));
    }

    [Theory]
    [InlineData("RDT_PROJ_MK::{A2FE74E1-B743-11D0-AE1A-00A0C90FFFC3}")]
    [InlineData("solution-items")]
    [InlineData("")]
    public void NonFileRunningDocumentMonikerIsIgnored(
        string moniker)
    {
        bool recognized =
            XaeWorkspaceFileChangeBackend.TryNormalizeFileMoniker(
                moniker,
                out string path);

        Assert.False(recognized);
        Assert.Equal(string.Empty, path);
    }

    [Fact]
    public void AbsoluteRunningDocumentMonikerIsNormalized()
    {
        bool recognized =
            XaeWorkspaceFileChangeBackend.TryNormalizeFileMoniker(
                MainPath,
                out string path);

        Assert.True(recognized);
        Assert.Equal(MainPath, path);
    }

    private static XaeWorkspaceGuardPath[] CreatePaths()
    {
        return new[]
        {
            new XaeWorkspaceGuardPath(
                MainPath,
                protectOpenDocument: true),
            new XaeWorkspaceGuardPath(
                ProjectPath,
                protectOpenDocument: false),
        };
    }

    private sealed class FakeBackend :
        IXaeWorkspaceFileChangeBackend
    {
        private Action<string>? _documentClosing;
        private Action<string>? _documentOpened;

        public List<string> OpenDocuments { get; } = new();

        public List<string> Operations { get; } = new();

        public string? FailProjectPath { get; set; }

        public bool Disposed { get; private set; }

        public void StartDocumentTracking(
            Action<string> documentOpened,
            Action<string> documentClosing,
            Action<string?, Exception> trackingFailed)
        {
            _documentOpened = documentOpened;
            _documentClosing = documentClosing;
            Operations.Add("tracking:start");
        }

        public void StopDocumentTracking()
        {
            Operations.Add("tracking:stop");
            _documentOpened = null;
            _documentClosing = null;
        }

        public IReadOnlyList<string> GetOpenDocumentPaths()
        {
            return OpenDocuments.ToArray();
        }

        public void IgnoreProjectFile(string path, bool ignore)
        {
            Operations.Add($"project:{path}:{ignore}");
            if (ignore
                && string.Equals(
                    path,
                    FailProjectPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Project guard failed.");
            }
        }

        public bool IgnoreDocumentFileChanges(
            string path,
            bool ignore)
        {
            Operations.Add($"document:{path}:{ignore}");
            return true;
        }

        public void SyncProjectFile(string path)
        {
            Operations.Add($"sync:{path}");
        }

        public void Dispose()
        {
            Disposed = true;
        }

        public void RaiseOpened(string path)
        {
            _documentOpened!(path);
        }

        public void RaiseClosing(string path)
        {
            _documentClosing!(path);
        }
    }
}
