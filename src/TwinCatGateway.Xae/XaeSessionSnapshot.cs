using System.Collections.Generic;
using TwinCatGateway.Contracts;

namespace TwinCatGateway.Xae;

public sealed class XaeSessionSnapshot
{
    public bool Connected { get; set; }

    public DteInstanceInfo? SelectedInstance { get; set; }

    public bool SysManagerAvailable { get; set; }

    public bool LaunchedByGateway { get; set; }

    public IReadOnlyList<DteInstanceInfo> DiscoveredInstances { get; set; } =
        new List<DteInstanceInfo>();
}
