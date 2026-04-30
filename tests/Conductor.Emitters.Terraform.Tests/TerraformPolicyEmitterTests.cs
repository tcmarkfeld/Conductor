using Conductor.Core;

namespace Conductor.Emitters.Terraform.Tests;

public sealed class TerraformPolicyEmitterTests
{
    [Fact]
    public void Emit_Generates_Deterministic_Hcl_With_Sqs_And_Sns_Documents()
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

        var emitter = new TerraformPolicyEmitter();
        var hcl = emitter.Emit(topology, new EmitOptions("${region}", "${account_id}", "${env}"));

        Assert.Contains("data \"aws_iam_policy_document\" \"conductor_sqs\" {", hcl);
        Assert.Contains("data \"aws_iam_policy_document\" \"conductor_sns\" {", hcl);
        Assert.Contains("arn:aws:sqs:${region}:${account_id}:queue-a", hcl);
        Assert.Contains("arn:aws:sqs:${region}:${account_id}:queue-b", hcl);
        Assert.Contains("arn:aws:sqs:${region}:${account_id}:queue-a_error", hcl);
        Assert.DoesNotContain("arn:aws:sqs:${region}:${account_id}:queue-a_skipped", hcl);
        Assert.Contains("arn:aws:sns:${region}:${account_id}:topic-a", hcl);
        Assert.Contains("arn:aws:sns:${region}:${account_id}:topic-z", hcl);
        Assert.Contains("arn:aws:sns:${region}:${account_id}:MassTransit-Fault*", hcl);
        Assert.DoesNotContain("arn:aws:sns:${region}:${account_id}:MassTransit-ReceiveFault", hcl);
        Assert.Contains("\"sqs:ReceiveMessage\",", hcl);
        Assert.Contains("\"sns:Publish\",", hcl);
    }

    [Fact]
    public void Emit_Includes_ReceiveFault_Topic_When_Not_Excluded()
    {
        var topology = new TransportTopology(
            Services:
            [
                new ServiceTopology(
                    SourceFile: "/tmp/Program.cs",
                    ReceiveFaultPublishExcluded: false,
                    Endpoints:
                    [
                        new EndpointTopology(
                            QueueName: "queue-a",
                            SubscribedTopics: [],
                            ConfiguredConsumers: [],
                            PublishFaultsDisabled: true,
                            DiscardFaultedMessages: false,
                            DiscardSkippedMessages: false)
                    ],
                    MessageBindings: [],
                    EndpointConventions: [])
            ],
            Queues: [new QueueResource("queue-a")],
            Topics: [],
            Diagnostics: []);

        var emitter = new TerraformPolicyEmitter();
        var hcl = emitter.Emit(topology, new EmitOptions());

        Assert.Contains("arn:aws:sns:${region}:${account_id}:MassTransit-ReceiveFault", hcl);
        Assert.DoesNotContain("arn:aws:sns:${region}:${account_id}:MassTransit-Fault*", hcl);
    }

    [Fact]
    public void Emit_Empty_Topology_Returns_Empty_Output()
    {
        var topology = new TransportTopology(
            Services: [],
            Queues: [],
            Topics: [],
            Diagnostics: []);

        var emitter = new TerraformPolicyEmitter();
        var hcl = emitter.Emit(topology, new EmitOptions());

        Assert.Equal(string.Empty, hcl);
    }
}
