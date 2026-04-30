using Conductor.Core;

namespace Conductor.Emitters.Terraform;

public sealed class TerraformPolicyEmitter : IPolicyEmitter, IOutputFormatter
{
    private static readonly IReadOnlyDictionary<Capability, string[]> SqsActions = new Dictionary<Capability, string[]>
    {
        [Capability.Create] = ["sqs:CreateQueue", "sqs:GetQueueAttributes", "sqs:GetQueueUrl", "sqs:SetQueueAttributes", "sqs:TagQueue"],
        [Capability.Read] = ["sqs:ChangeMessageVisibility", "sqs:DeleteMessage", "sqs:GetQueueAttributes", "sqs:GetQueueUrl", "sqs:ReceiveMessage"],
        [Capability.Write] = ["sqs:GetQueueAttributes", "sqs:GetQueueUrl", "sqs:SendMessage"],
        [Capability.Manage] = ["sqs:PurgeQueue"]
    };

    private static readonly IReadOnlyDictionary<Capability, string[]> SnsActions = new Dictionary<Capability, string[]>
    {
        [Capability.Create] = ["sns:CreateTopic", "sns:GetTopicAttributes", "sns:SetTopicAttributes", "sns:TagResource"],
        [Capability.Write] = ["sns:Publish", "sns:GetTopicAttributes"],
        [Capability.Subscribe] = ["sns:Subscribe", "sns:Unsubscribe", "sns:GetTopicAttributes"]
    };

    public string Format(TransportTopology topology, EmitOptions options) => Emit(topology, options);

    public string Emit(TransportTopology topology, EmitOptions options)
    {
        var queueNames = BuildQueueNames(topology);
        var topicNames = BuildTopicNames(topology);
        var blocks = new List<string>();

        if (queueNames.Length > 0)
        {
            blocks.Add(BuildDataBlock(
                "conductor_sqs",
                "SqsManageQueues",
                GetSqsActions(),
                queueNames.Select(x => QueueArn(x, options)).ToArray()));
        }

        if (topicNames.Length > 0)
        {
            blocks.Add(BuildDataBlock(
                "conductor_sns",
                "SnsManageTopics",
                GetSnsActions(),
                topicNames.Select(x => TopicArn(x, options)).ToArray()));
        }

        return string.Join(Environment.NewLine + Environment.NewLine, blocks);
    }

    private static string BuildDataBlock(string label, string sid, string[] actions, string[] resources)
    {
        var lines = new List<string>
        {
            $"data \"aws_iam_policy_document\" \"{label}\" {{",
            "  statement {",
            $"    sid    = \"{sid}\"",
            "    effect = \"Allow\"",
            "    actions = ["
        };

        lines.AddRange(actions
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(action => $"      \"{action}\","));

        lines.Add("    ]");
        lines.Add("    resources = [");

        lines.AddRange(resources
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(resource => $"      \"{resource}\","));

        lines.Add("    ]");
        lines.Add("  }");
        lines.Add("}");

        return string.Join(Environment.NewLine, lines);
    }

    private static string QueueArn(string queueName, EmitOptions options)
        => $"arn:aws:sqs:{options.Region}:{options.AccountId}:{queueName}";

    private static string TopicArn(string topicName, EmitOptions options)
        => $"arn:aws:sns:{options.Region}:{options.AccountId}:{topicName}";

    private static string[] GetSqsActions() => SqsActions
        .OrderBy(x => x.Key)
        .SelectMany(x => x.Value)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();

    private static string[] GetSnsActions() => SnsActions
        .OrderBy(x => x.Key)
        .SelectMany(x => x.Value)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(x => x, StringComparer.Ordinal)
        .ToArray();

    private static string[] BuildTopicNames(TransportTopology topology)
    {
        var names = new HashSet<string>(topology.Topics.Select(x => x.Name), StringComparer.Ordinal);

        var hasEndpointFaultPublishes = topology.Services
            .SelectMany(x => x.Endpoints)
            .Any(e => !e.PublishFaultsDisabled);

        if (hasEndpointFaultPublishes)
        {
            names.Add("MassTransit-Fault*");
        }

        var hasReceiveFaultPublish = topology.Services.Any(s => !s.ReceiveFaultPublishExcluded);
        if (hasReceiveFaultPublish)
        {
            names.Add("MassTransit-ReceiveFault");
        }

        return names.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static string[] BuildQueueNames(TransportTopology topology)
    {
        var baseQueueNames = topology.Queues.Select(x => x.Name).ToArray();
        var names = new HashSet<string>(baseQueueNames, StringComparer.Ordinal);

        foreach (var queueName in baseQueueNames)
        {
            names.Add($"{queueName}_error");
            names.Add($"{queueName}_skipped");
        }

        var endpointBehaviorByQueue = topology.Services
            .SelectMany(x => x.Endpoints)
            .GroupBy(e => e.QueueName, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    DiscardFaulted = g.All(x => x.DiscardFaultedMessages),
                    DiscardSkipped = g.All(x => x.DiscardSkippedMessages)
                },
                StringComparer.Ordinal);

        foreach (var queueName in baseQueueNames)
        {
            if (!endpointBehaviorByQueue.TryGetValue(queueName, out var behavior))
            {
                continue;
            }

            if (behavior.DiscardFaulted)
            {
                names.Remove($"{queueName}_error");
            }

            if (behavior.DiscardSkipped)
            {
                names.Remove($"{queueName}_skipped");
            }
        }

        return names.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }
}
