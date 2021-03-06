using System;
using System.Linq;
using System.Threading.Tasks;
using RawRabbit.Context;
using RawRabbit.Extensions.MessageSequence.Configuration.Abstraction;
using RawRabbit.Extensions.MessageSequence.Core.Abstraction;
using RawRabbit.Extensions.MessageSequence.Model;
using RawRabbit.Extensions.MessageSequence.Repository;
using RawRabbit.Logging;

namespace RawRabbit.Extensions.MessageSequence.Configuration
{
	public class MessageSequenceBuilder<TMessageContext>
		: IMessageChainPublisher<TMessageContext>
		, IMessageSequenceBuilder<TMessageContext> where TMessageContext : IMessageContext
	{
		private readonly ILogger _logger = LogManager.GetLogger<MessageSequenceBuilder<TMessageContext>>();
		private readonly IBusClient<TMessageContext> _busClient;
		private readonly IMessageChainTopologyUtil _chainTopology;
		private readonly IMessageChainDispatcher _dispatcher;
		private readonly IMessageSequenceRepository _repository;

		private Func<Task> _publishAsync;
		private Guid _globalMessageId ;

		public MessageSequenceBuilder(IBusClient<TMessageContext> busClient, IMessageChainTopologyUtil chainTopology, IMessageChainDispatcher dispatcher, IMessageSequenceRepository repository)
		{
			_busClient = busClient;
			_chainTopology = chainTopology;
			_dispatcher = dispatcher;
			_repository = repository;
		}

		public IMessageSequenceBuilder<TMessageContext> PublishAsync<TMessage>(TMessage message = default(TMessage), Guid globalMessageId = new Guid()) where TMessage : new()
		{
			_globalMessageId = globalMessageId == Guid.Empty
				? Guid.NewGuid()
				: globalMessageId;
			_logger.LogDebug($"Preparing Message Sequence for '{_globalMessageId}' that starts with {typeof(TMessage).Name}.");
			_publishAsync = () => _busClient.PublishAsync(message, _globalMessageId);
			return this;
		}

		public IMessageSequenceBuilder<TMessageContext> When<TMessage>(Func<TMessage, TMessageContext, Task> func, Action<IStepOptionBuilder> options = null)
		{
			var optionBuilder = new StepOptionBuilder();
			options?.Invoke(optionBuilder);
			_logger.LogDebug($"Registering handler for '{_globalMessageId}' of type '{typeof(TMessage).Name}'. Optional: {optionBuilder.Configuration.Optional}, Aborts: {optionBuilder.Configuration.AbortsExecution}");
			_dispatcher.AddMessageHandler(_globalMessageId, func, optionBuilder.Configuration);
			var bindTask = _chainTopology.BindToExchange<TMessage>(_globalMessageId);
			Task.WaitAll(bindTask);
			return this;
		}

		public MessageSequence<TMessage> Complete<TMessage>()
		{
			_logger.LogDebug($"Message Sequence for '{_globalMessageId}' completes with '{typeof(TMessage).Name}'.");
			var sequenceDef = _repository.GetOrCreate(_globalMessageId);
			
			var messageTcs = new TaskCompletionSource<TMessage>();
			var sequence = new MessageSequence<TMessage>
			{
				Task = messageTcs.Task
			};

			sequenceDef.TaskCompletionSource.Task.ContinueWith(tObj =>
			{
				var final = _repository.Get(_globalMessageId);
				_logger.LogDebug($"Updating Sequence for '{_globalMessageId}'.");
				sequence.Aborted = final.State.Aborted;
				sequence.Completed = final.State.Completed;
				sequence.Skipped = final.State.Skipped;
				foreach (var step in final.StepDefinitions)
				{
					_chainTopology.UnbindFromExchange(step.Type, _globalMessageId);
				}
				messageTcs.TrySetResult((TMessage) tObj.Result);
				_repository.Remove(_globalMessageId);
			});

			Func<TMessage, TMessageContext, Task> func = (message, context) =>
			{
				Task
					.WhenAll(sequenceDef.State.HandlerTasks)
					.ContinueWith(t => sequenceDef.TaskCompletionSource.TrySetResult(message));
				return Task.FromResult(true);
			};

			var bindTask = _chainTopology.BindToExchange<TMessage>(_globalMessageId);
			_dispatcher.AddMessageHandler(_globalMessageId, func);

			Task
				.WhenAll(bindTask)
				.ContinueWith(t => _publishAsync())
				.Unwrap()
				.Wait();

			return sequence;
		}
	}
}
