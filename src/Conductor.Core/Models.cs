namespace Conductor.Core;

public enum Capability
{
    Create,
    Read,
    Write,
    Subscribe,
    Manage
}

public sealed record QueueResource(string Name);
public sealed record TopicResource(string Name);

public sealed record MessageBinding(string MessageType, string? TopicName);
public sealed record EndpointConventionBinding(string MessageType, string QueueName);

public sealed record EndpointTopology(
    string QueueName,
    IReadOnlyCollection<string> SubscribedTopics,
    IReadOnlyCollection<string> ConfiguredConsumers,
    bool PublishFaultsDisabled,
    bool DiscardFaultedMessages,
    bool DiscardSkippedMessages);

public sealed record ServiceTopology(
    string SourceFile,
    bool ReceiveFaultPublishExcluded,
    IReadOnlyCollection<EndpointTopology> Endpoints,
    IReadOnlyCollection<MessageBinding> MessageBindings,
    IReadOnlyCollection<EndpointConventionBinding> EndpointConventions);

public sealed record AnalysisDiagnostic(string SourceFile, string Message);

public sealed record TransportTopology(
    IReadOnlyCollection<ServiceTopology> Services,
    IReadOnlyCollection<QueueResource> Queues,
    IReadOnlyCollection<TopicResource> Topics,
    IReadOnlyCollection<AnalysisDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Count > 0;
}

public sealed record AnalysisInput(string RepoPath, bool Strict = true);

public sealed record EmitOptions(
    string Region = "${region}",
    string AccountId = "${account_id}",
    string Env = "${env}");

public interface ITopologyAnalyzer
{
    TransportTopology Analyze(AnalysisInput input);
}

public interface IPolicyEmitter
{
    string Emit(TransportTopology topology, EmitOptions options);
}

public interface IOutputFormatter
{
    string Format(TransportTopology topology, EmitOptions options);
}
