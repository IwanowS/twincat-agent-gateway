using System;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Core;

public interface IGatewayEventSink
{
    long Record(
        GatewayEvent gatewayEvent,
        DateTimeOffset occurredAtUtc);
}
