namespace Conductor.Core.Tests;

public sealed class CoreModelTests
{
    [Fact]
    public void TransportTopology_HasErrors_Reflects_Diagnostics()
    {
        var topology = new TransportTopology(
            Services: [],
            Queues: [],
            Topics: [],
            Diagnostics: [new AnalysisDiagnostic("/tmp/Program.cs", "bad")]);

        Assert.True(topology.HasErrors);
    }
}
