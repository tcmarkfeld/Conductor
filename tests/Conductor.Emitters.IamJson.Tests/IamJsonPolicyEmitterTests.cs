using Conductor.Core;
using Conductor.Emitters.IamJson;

namespace Conductor.Emitters.IamJson.Tests;

public sealed class IamJsonPolicyEmitterTests
{
    [Fact]
    public void Emit_Generates_Deterministic_Minimal_Policy()
    {
        var topology = new TransportTopology(
            Services:
            [
                new ServiceTopology(
                    SourceFile: "/tmp/Program.cs",
                    ReceiveFaultPublishExcluded: true,
                    Endpoints:
                    [
                        new EndpointTopology(
                            QueueName: "queue-a",
                            SubscribedTopics: [],
                            ConfiguredConsumers: [],
                            PublishFaultsDisabled: false,
                            DiscardFaultedMessages: false,
                            DiscardSkippedMessages: true)
                    ],
                    MessageBindings: [],
                    EndpointConventions: [])
            ],
            Queues: [new QueueResource("queue-b"), new QueueResource("queue-a")],
            Topics: [new TopicResource("topic-z"), new TopicResource("topic-a")],
            Diagnostics: []);

        var emitter = new IamJsonPolicyEmitter();
        var json = emitter.Emit(topology, new EmitOptions("${region}", "${account_id}", "${env}"));

        Assert.Contains("arn:aws:sqs:${region}:${account_id}:queue-a", json);
        Assert.Contains("arn:aws:sqs:${region}:${account_id}:queue-b", json);
        Assert.Contains("arn:aws:sqs:${region}:${account_id}:queue-a_error", json);
        Assert.DoesNotContain("arn:aws:sqs:${region}:${account_id}:queue-a_skipped", json);
        Assert.Contains("arn:aws:sns:${region}:${account_id}:topic-a", json);
        Assert.Contains("arn:aws:sns:${region}:${account_id}:topic-z", json);
        Assert.Contains("arn:aws:sns:${region}:${account_id}:MassTransit-Fault*", json);
        Assert.DoesNotContain("arn:aws:sns:${region}:${account_id}:MassTransit-ReceiveFault", json);
        Assert.Contains("sqs:ReceiveMessage", json);
        Assert.Contains("sns:Publish", json);
    }
}
