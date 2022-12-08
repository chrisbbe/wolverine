using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Lamar;
using Wolverine.Runtime.ResponseReply;
using Wolverine.Transports;

namespace Wolverine.Runtime;

public class MessagePublisher : CommandBus, IMessagePublisher
{
    [DefaultConstructor]
    public MessagePublisher(IWolverineRuntime runtime) : base(runtime)
    {
    }

    public MessagePublisher(IWolverineRuntime runtime, string? correlationId) : base(runtime, correlationId)
    {
    }

    public IReadOnlyList<Envelope> PreviewSubscriptions(object message)
    {
        return Runtime.RoutingFor(message.GetType()).RouteForSend(message, null);

    }

    public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        // Cannot trust the T here. Can be "object"
        var outgoing = Runtime.RoutingFor(message.GetType()).RouteForSend(message, options);
        trackEnvelopeCorrelation(outgoing);

        return PersistOrSendAsync(outgoing);
    }

    public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        // TODO -- eliminate this. Only happening for logging at this point. Check same in Send.
        var envelope = new Envelope(message);

        // You can't trust the T here.
        var outgoing = Runtime.RoutingFor(message.GetType()).RouteForPublish(message, options);
        trackEnvelopeCorrelation(outgoing);

        if (outgoing.Any())
        {
            return PersistOrSendAsync(outgoing);
        }

        Runtime.MessageLogger.NoRoutesFor(envelope);
        return ValueTask.CompletedTask;
    }


    public ValueTask SendToTopicAsync(string topicName, object message, DeliveryOptions? options = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var outgoing = Runtime.RoutingFor(message.GetType()).RouteToTopic(message, topicName, options);
        return PersistOrSendAsync(outgoing);
    }

    public ValueTask SendToEndpointAsync(string endpointName, object message, DeliveryOptions? options = null)
    {
        if (endpointName == null)
        {
            throw new ArgumentNullException(nameof(endpointName));
        }

        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var outgoing = Runtime.RoutingFor(message.GetType())
            .RouteToEndpointByName(message, endpointName, options);

        return PersistOrSendAsync(outgoing);
    }

    /// <summary>
    ///     Send to a specific destination rather than running the routing rules
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="destination">The destination to send to</param>
    /// <param name="message"></param>
    public ValueTask SendAsync<T>(Uri destination, T message, DeliveryOptions? options = null)
    {
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var envelope = Runtime.RoutingFor(message.GetType())
            .RouteToDestination(message, destination, options);

        TrackEnvelopeCorrelation(envelope);

        return PersistOrSendAsync(envelope);
    }

    /// <summary>
    ///     Send a message that should be executed at the given time
    /// </summary>
    /// <param name="message"></param>
    /// <param name="time"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    public ValueTask SchedulePublishAsync<T>(T message, DateTimeOffset time, DeliveryOptions? options = null)
    {
        options ??= new DeliveryOptions();
        options.ScheduledTime = time;

        return PublishAsync(message, options);
    }

    /// <summary>
    ///     Send a message that should be executed after the given delay
    /// </summary>
    /// <param name="message"></param>
    /// <param name="delay"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    public ValueTask SchedulePublishAsync<T>(T message, TimeSpan delay, DeliveryOptions? options = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        options ??= new DeliveryOptions();
        options.ScheduleDelay = delay;
        return PublishAsync(message, options);
    }

    public Task<Acknowledgement> SendAndWaitAsync(object message, CancellationToken cancellation = default,
        TimeSpan? timeout = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        return Runtime.RoutingFor(message.GetType()).FindSingleRouteForSending()
            .InvokeAsync<Acknowledgement>(message, this, cancellation, timeout);
    }

    public Task<Acknowledgement> SendAndWaitAsync(Uri destination, object message,
        CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var route = Runtime.RoutingFor(message.GetType());
        return route.RouteForUri(destination).InvokeAsync<Acknowledgement>(message, this, cancellation, timeout);
    }

    public Task<Acknowledgement> SendAndWaitAsync(string endpointName, object message,
        CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var route = Runtime.RoutingFor(message.GetType());
        return route.RouteForEndpoint(endpointName).InvokeAsync<Acknowledgement>(message, this, cancellation, timeout);
    }

    public Task<T> RequestAsync<T>(object message, CancellationToken cancellation = default,
        TimeSpan? timeout = null) where T : class
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        // KEEP THIS IN MESSAGE PUBLISHER
        Runtime.RegisterMessageType(typeof(T));

        return Runtime.RoutingFor(message.GetType()).FindSingleRouteForSending()
            .InvokeAsync<T>(message, this, cancellation, timeout);
    }

    public Task<T> RequestAsync<T>(Uri destination, object message, CancellationToken cancellation = default,
        TimeSpan? timeout = null) where T : class
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        Runtime.RegisterMessageType(typeof(T));
        return Runtime.RoutingFor(message.GetType()).RouteForUri(destination)
            .InvokeAsync<T>(message, this, cancellation, timeout);
    }

    public Task<T> RequestAsync<T>(string endpointName, object message, CancellationToken cancellation = default,
        TimeSpan? timeout = null) where T : class
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        Runtime.RegisterMessageType(typeof(T));
        return Runtime.RoutingFor(message.GetType()).RouteForEndpoint(endpointName)
            .InvokeAsync<T>(message, this, cancellation, timeout);
    }

    private void trackEnvelopeCorrelation(Envelope[] outgoing)
    {
        foreach (var outbound in outgoing) TrackEnvelopeCorrelation(outbound);
    }

    internal virtual void TrackEnvelopeCorrelation(Envelope outbound)
    {
        outbound.Source = Runtime.Advanced.ServiceName;
        outbound.CorrelationId = CorrelationId;
        outbound.ConversationId = outbound.Id; // the message chain originates here
    }

    internal async ValueTask PersistOrSendAsync(params Envelope[] outgoing)
    {
        if (Transaction != null)
        {
            // This filtering is done to only persist envelopes where 
            // the sender is currently latched
            var envelopes = outgoing.Where(isDurable).ToArray();
            foreach (var envelope in envelopes.Where(x => x.Sender is { Latched: true }))
                envelope.OwnerId = TransportConstants.AnyNode;

            await Transaction.PersistAsync(envelopes);

            _outstanding.Fill(outgoing);
        }
        else
        {
            foreach (var outgoingEnvelope in outgoing) await outgoingEnvelope.StoreAndForwardAsync();
        }
    }

    private bool isDurable(Envelope envelope)
    {
        return envelope.Sender?.IsDurable ?? Runtime.Endpoints.GetOrBuildSendingAgent(envelope.Destination!).IsDurable;
    }
}