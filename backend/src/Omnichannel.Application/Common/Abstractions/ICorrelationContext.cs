namespace Omnichannel.Application.Common.Abstractions;

/// <summary>
/// Correlation id of the current operation, written to outbox events and processing tasks so a flow can be
/// traced API → RabbitMQ → worker. In the API it is the request's X-Correlation-Id.
/// </summary>
public interface ICorrelationContext
{
    string CorrelationId { get; }
}
