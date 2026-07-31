using System;
using System.Collections.Generic;

namespace TwinCatGateway.Contracts;

public sealed class ProfileCapabilitiesSnapshot
{
    public string Profile { get; set; } = string.Empty;

    public List<CapabilityState> Capabilities { get; set; } = new();

    public DateTimeOffset ObservedAtUtc { get; set; }
}

public sealed class GatewayDiagnosticsSnapshot
{
    public GatewayComponent Component { get; set; } = GatewayComponent.Gateway;

    public OperationEventPage Events { get; set; } = new();
}

public sealed class XaeDiagnosticsSnapshot
{
    public string Profile { get; set; } = string.Empty;

    public GatewayComponent Component { get; set; } = GatewayComponent.Xae;

    public XaeSessionSnapshot State { get; set; } = new();

    public List<DteInstanceInfo> DteInstances { get; set; } = new();

    public ComDiagnostics Com { get; set; } = new();

    public OperationEventPage Events { get; set; } = new();
}

public sealed class TargetDiagnosticsSnapshot
{
    public string Profile { get; set; } = string.Empty;

    public GatewayComponent Component { get; set; } = GatewayComponent.Target;

    public TargetSystemObservation Target { get; set; } = new();

    public XaeTwinCatSystemObservation? XaeObserved { get; set; }

    public StateObservationDivergence? Divergence { get; set; }

    public OperationEventPage Events { get; set; } = new();
}

public sealed class PlcDiagnosticsSnapshot
{
    public string Profile { get; set; } = string.Empty;

    public string RuntimeId { get; set; } = string.Empty;

    public GatewayComponent Component { get; set; } = GatewayComponent.Plc;

    public PlcRuntimeObservation Runtime { get; set; } = new();

    public OperationEventPage Events { get; set; } = new();
}
