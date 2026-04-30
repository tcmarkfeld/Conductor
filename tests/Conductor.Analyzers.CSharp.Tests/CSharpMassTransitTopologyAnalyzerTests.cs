using Conductor.Analyzers.CSharp;
using Conductor.Core;

namespace Conductor.Analyzers.CSharp.Tests;

public sealed class CSharpMassTransitTopologyAnalyzerTests
{
    [Fact]
    public void Analyze_Extracts_Queues_Topics_And_Bindings()
    {
        var repoPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "SampleMassTransitApp"));

        var analyzer = new CSharpMassTransitTopologyAnalyzer();
        var topology = analyzer.Analyze(new AnalysisInput(repoPath, Strict: true));

        Assert.False(topology.HasErrors);
        Assert.Contains(topology.Queues, q => q.Name == "deliverable-ready-for-publication-queue-prod");
        Assert.Contains(topology.Topics, t => t.Name == "deliverable-status-update-topic-prod");

        var service = Assert.Single(topology.Services);
        Assert.False(service.ReceiveFaultPublishExcluded);
        var endpoint = Assert.Single(service.Endpoints);
        Assert.Equal("deliverable-ready-for-publication-queue-prod", endpoint.QueueName);
        Assert.Contains("deliverable-status-update-topic-prod", endpoint.SubscribedTopics);
        Assert.False(endpoint.PublishFaultsDisabled);
        Assert.False(endpoint.DiscardFaultedMessages);
        Assert.False(endpoint.DiscardSkippedMessages);

        var convention = Assert.Single(service.EndpointConventions);
        Assert.Equal("deliverable-ready-for-publication-queue-prod", convention.QueueName);
    }

    [Fact]
    public void Analyze_Strict_Mode_Adds_Diagnostic_For_Unresolved_Name()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "conductor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Program.cs"), """
using MassTransit;

builder.Services.AddMassTransit(cfg =>
{
    cfg.UsingAmazonSqs((context, bus) =>
    {
        bus.ReceiveEndpoint(GetQueueName(), ec =>
        {
            ec.Subscribe(GetTopicName());
        });
    });
});
""");

            var analyzer = new CSharpMassTransitTopologyAnalyzer();
            var topology = analyzer.Analyze(new AnalysisInput(tempDir, Strict: true));

            Assert.True(topology.HasErrors);
            Assert.NotEmpty(topology.Diagnostics);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Analyze_Detects_DiscardFaulted_And_DiscardSkipped()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "conductor-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Program.cs"), """
using MassTransit;

builder.Services.AddMassTransit(cfg =>
{
    cfg.UsingAmazonSqs((context, bus) =>
    {
        bus.Publish<ReceiveFault>(x => x.Exclude = true);
        const string queue = "my-queue";
        bus.ReceiveEndpoint(queue, ec =>
        {
            ec.PublishFaults = false;
            ec.DiscardFaultedMessages();
            ec.DiscardSkippedMessages();
        });
    });
});
""");

            var analyzer = new CSharpMassTransitTopologyAnalyzer();
            var topology = analyzer.Analyze(new AnalysisInput(tempDir, Strict: true));

            var service = Assert.Single(topology.Services);
            Assert.True(service.ReceiveFaultPublishExcluded);
            var endpoint = Assert.Single(service.Endpoints);
            Assert.True(endpoint.PublishFaultsDisabled);
            Assert.True(endpoint.DiscardFaultedMessages);
            Assert.True(endpoint.DiscardSkippedMessages);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
