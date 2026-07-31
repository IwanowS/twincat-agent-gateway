using System;
using System.IO;
using System.Linq;
using TwinCatGateway.Core;
using Xunit;

namespace TwinCatGateway.UnitTests;

public sealed class TwinCatRuntimeTargetDiscoveryTests
{
    [Fact]
    public void DiscoversOnlyPlcProjectPortsInStableOrder()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"runtime-targets-{Guid.NewGuid():N}.tsproj");
        try
        {
            File.WriteAllText(
                path,
                """
                <TcSmProject>
                  <Project>
                    <System>
                      <Tasks>
                        <Task Name="Task" AmsPort="350" />
                      </Tasks>
                    </System>
                    <Plc>
                      <Project Name="Second" AmsPort="852" />
                      <Project Name="First" AmsPort="851" />
                      <Project Name="Duplicate" AmsPort="851" />
                      <Project Name="Invalid" AmsPort="0" />
                    </Plc>
                  </Project>
                </TcSmProject>
                """);

            PlcRuntimeTarget[] targets =
                TwinCatRuntimeTargetDiscovery
                    .Discover(path)
                    .ToArray();

            Assert.Collection(
                targets,
                first =>
                {
                    Assert.Equal("plc-851", first.RuntimeId);
                    Assert.Equal("First", first.Project);
                    Assert.Null(first.Instance);
                    Assert.Equal(851, first.AdsPort);
                },
                second =>
                {
                    Assert.Equal("plc-852", second.RuntimeId);
                    Assert.Equal("Second", second.Project);
                    Assert.Equal(852, second.AdsPort);
                });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void UsesConfiguredRuntimeIdOnlyForMatchingPort()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"runtime-targets-{Guid.NewGuid():N}.tsproj");
        try
        {
            File.WriteAllText(
                path,
                """
                <TcSmProject>
                  <Project>
                    <Plc>
                      <Project Name="First" AmsPort="851" />
                      <Project Name="Second" AmsPort="852" />
                    </Plc>
                  </Project>
                </TcSmProject>
                """);

            PlcRuntimeTarget[] targets =
                TwinCatRuntimeTargetDiscovery
                    .Discover(
                        path,
                        "verification",
                        852)
                    .ToArray();

            Assert.Equal("plc-851", targets[0].RuntimeId);
            Assert.Equal("verification", targets[1].RuntimeId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
