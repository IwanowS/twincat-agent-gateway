using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TwinCatGateway.Contracts;
using TwinCatGateway.Ipc;
using Xunit;

namespace TwinCatGateway.ContractTests;

public sealed class IpcProtocolTests
{
    [Fact]
    public async Task FrameRoundTripsThroughPartialReads()
    {
        using MemoryStream output = new();
        await IpcFrameProtocol.WriteAsync(
            output,
            "{\"ok\":true}",
            CancellationToken.None);
        output.Position = 0;
        using PartialReadStream input = new(output, maximumRead: 2);

        string payload = await IpcFrameProtocol.ReadAsync(
            input,
            CancellationToken.None);

        Assert.Equal("{\"ok\":true}", payload);
    }

    [Fact]
    public async Task HandlerReturnsVersionMismatchWithoutDispatch()
    {
        bool dispatched = false;
        GatewayProtocolHandler handler = new(
            (request, cancellationToken) =>
            {
                dispatched = true;
                return Task.FromResult(GatewayDispatchResult.Success());
            });
        string request =
            """
            {
              "protocolVersion": 999,
              "requestId": "request-1",
              "method": "health",
              "params": {}
            }
            """;

        string responseJson = await handler.HandleAsync(
            request,
            CancellationToken.None);
        GatewayResponse<EmptyParameters>? response =
            JsonSerializer.Deserialize<GatewayResponse<EmptyParameters>>(
                responseJson,
                GatewayJson.CreateSerializerOptions());

        Assert.NotNull(response);
        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.IpcVersionMismatch, response.Error?.Code);
        Assert.False(dispatched);
    }

    [Fact]
    public async Task HandlerDispatchesTypedParametersAndPreservesRequestId()
    {
        GatewayProtocolHandler handler = new(
            (request, cancellationToken) =>
            {
                BuildParameters parameters =
                    request.DeserializeParameters<BuildParameters>(
                        GatewayJson.CreateSerializerOptions());
                Assert.Equal(
                    "TC3_SimpleProject/PlcProject1/POUs/MAIN.TcPOU",
                    Assert.Single(parameters.ChangedPaths));
                return Task.FromResult(
                    GatewayDispatchResult.Success(
                        new BuildSummary
                        {
                            Ok = true,
                            OperationId = "operation-1",
                            Action = parameters.Action,
                        }));
            });
        string request =
            """
            {
              "protocolVersion": 1,
              "requestId": "request-1",
              "method": "build",
              "params": {
                "profile": "bench",
                "action": "clean",
                "changedPaths": [
                  "TC3_SimpleProject/PlcProject1/POUs/MAIN.TcPOU"
                ]
              },
              "wait": true
            }
            """;

        string responseJson = await handler.HandleAsync(
            request,
            CancellationToken.None);
        GatewayResponse<BuildSummary>? response =
            JsonSerializer.Deserialize<GatewayResponse<BuildSummary>>(
                responseJson,
                GatewayJson.CreateSerializerOptions());

        Assert.NotNull(response);
        Assert.True(response.Ok);
        Assert.Equal("request-1", response.RequestId);
        Assert.Equal(BuildAction.Clean, response.Result?.Action);
    }

    [Fact]
    public async Task InvalidRequestReturnsCompactError()
    {
        GatewayProtocolHandler handler = new(
            (request, cancellationToken) =>
                Task.FromResult(GatewayDispatchResult.Success()));

        string responseJson = await handler.HandleAsync(
            "{ invalid",
            CancellationToken.None);
        GatewayResponse<EmptyParameters>? response =
            JsonSerializer.Deserialize<GatewayResponse<EmptyParameters>>(
                responseJson,
                GatewayJson.CreateSerializerOptions());

        Assert.NotNull(response);
        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.RequestInvalid, response.Error?.Code);
        Assert.DoesNotContain("JsonException", responseJson);
    }

    private sealed class PartialReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _maximumRead;

        public PartialReadStream(Stream inner, int maximumRead)
        {
            _inner = inner;
            _maximumRead = maximumRead;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _inner.Read(buffer, offset, Math.Min(count, _maximumRead));
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return _inner.ReadAsync(
                buffer,
                offset,
                Math.Min(count, _maximumRead),
                cancellationToken);
        }

#if NET8_0_OR_GREATER
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return _inner.ReadAsync(
                buffer.Slice(0, Math.Min(buffer.Length, _maximumRead)),
                cancellationToken);
        }
#endif

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
