using MassTransit;

const string queueEnv = "prod";
var queueName = "deliverable-ready-for-publication-queue-{{env}}".Replace("{{env}}", queueEnv);
var topicName = "deliverable-status-update-topic-{{env}}".Replace("{{env}}", queueEnv);
var hostURI = "amazonsqs://sqs.us-east-2";

builder.Services.AddMassTransit(cfg =>
{
    cfg.UsingAmazonSqs((context, bus) =>
    {
        bus.Message<DeliverableStatusUpdate>(x =>
        {
            x.SetEntityName(topicName);
        });

        EndpointConvention.Map<DeliverableStatusUpdate>(new Uri($"{hostURI}/{queueName}"));

        bus.ReceiveEndpoint(queueName, ec =>
        {
            ec.ConfigureConsumeTopology = false;
            ec.Subscribe(topicName);
        });

        bus.ConfigureEndpoints(context);
    });
});

public class DeliverableStatusUpdate { }
