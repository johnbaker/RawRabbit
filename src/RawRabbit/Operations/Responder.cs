﻿using System;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RawRabbit.Common;
using RawRabbit.Configuration.Respond;
using RawRabbit.Context;
using RawRabbit.Context.Provider;
using RawRabbit.Operations.Contracts;
using RawRabbit.Serialization;
using RawRabbit.Consumer.Contract;

namespace RawRabbit.Operations
{
	public class Responder<TMessageContext> : OperatorBase, IResponder<TMessageContext> where TMessageContext : IMessageContext
	{
		private readonly IConsumerFactory _consumerFactory;
		private readonly IMessageContextProvider<TMessageContext> _contextProvider;

		public Responder(IChannelFactory channelFactory, IConsumerFactory consumerFactory, IMessageSerializer serializer, IMessageContextProvider<TMessageContext> contextProvider)
			: base(channelFactory, serializer)
		{
			_consumerFactory = consumerFactory;
			_contextProvider = contextProvider;
		}

		public Task RespondAsync<TRequest, TResponse>(Func<TRequest, TMessageContext, Task<TResponse>> onMessage, ResponderConfiguration cfg)
		{
			var queueTask = DeclareQueueAsync(cfg.Queue);
			var exchangeTask = DeclareExchangeAsync(cfg.Exchange);

			return Task
				.WhenAll(queueTask, exchangeTask)
				.ContinueWith(t => BindQueue(cfg.Queue, cfg.Exchange, cfg.RoutingKey))
				.ContinueWith(t => ConfigureRespond(onMessage, cfg));
		}

		private void ConfigureRespond<TRequest, TResponse>(Func<TRequest, TMessageContext, Task<TResponse>> onMessage, IConsumerConfiguration cfg)
		{
			var consumer = _consumerFactory.CreateConsumer(cfg);
			consumer.OnMessageAsync = (o, args) =>
			{
				var bodyTask = Task.Run(() => Serializer.Deserialize<TRequest>(args.Body));
				var contextTask = _contextProvider.ExtractContextAsync(args.BasicProperties.Headers[_contextProvider.ContextHeaderName]);
				return Task
					.WhenAll(bodyTask, contextTask)
					.ContinueWith(task =>	onMessage(bodyTask.Result, contextTask.Result)).Unwrap()
					.ContinueWith(payloadTask => SendResponseAsync(payloadTask.Result, args));
			};
		}

		private Task SendResponseAsync<TResponse>(TResponse result, BasicDeliverEventArgs requestPayload)
		{
			var propsTask = CreateReplyPropsAsync(requestPayload);
			var serializeTask = Task.Run(() => Serializer.Serialize(result));

			return Task
				.WhenAll(propsTask, serializeTask)
				.ContinueWith(task =>
				{
					var channel = ChannelFactory.GetChannel();
					channel.BasicPublish(
						exchange: requestPayload.Exchange,
						routingKey: requestPayload.BasicProperties.ReplyTo,
						basicProperties: propsTask.Result,
						body: serializeTask.Result
					);
				});
		}

		private Task<IBasicProperties> CreateReplyPropsAsync(BasicDeliverEventArgs requestPayload)
		{
			return Task.Run(() =>
			{
				var channel = ChannelFactory.GetChannel();
				var replyProps = channel.CreateBasicProperties();
				replyProps.CorrelationId = requestPayload.BasicProperties.CorrelationId;
				return replyProps;
			});
		}
	}
}