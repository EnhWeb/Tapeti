﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tapeti.Config;

namespace Tapeti.Connection
{
    /// <inheritdoc />
    internal class TapetiSubscriber : ISubscriber
    {
        private readonly Func<ITapetiClient> clientFactory;
        private readonly ITapetiConfig config;
        private bool consuming;

        private CancellationTokenSource initializeCancellationTokenSource;


        /// <inheritdoc />
        public TapetiSubscriber(Func<ITapetiClient> clientFactory, ITapetiConfig config)
        {
            this.clientFactory = clientFactory;
            this.config = config;
        }


        public void Dispose()
        {
        }



        /// <summary>
        /// Applies the configured bindings and declares the queues in RabbitMQ. For internal use only.
        /// </summary>
        /// <returns></returns>
        public async Task ApplyBindings()
        {
            initializeCancellationTokenSource = new CancellationTokenSource();
            await ApplyBindings(initializeCancellationTokenSource.Token);
        }


        /// <summary>
        /// Called after the connection is lost. For internal use only.
        /// Guaranteed to be called from within the taskQueue thread.
        /// </summary>
        public void Disconnect()
        {
            initializeCancellationTokenSource?.Cancel();
            initializeCancellationTokenSource = null;
        }


        /// <summary>
        /// Called after the connection is lost and regained. Reapplies the bindings and if Resume
        /// has already been called, restarts the consumers. For internal use only.
        /// Guaranteed to be called from within the taskQueue thread.
        /// </summary>
        public void Reconnect()
        {
            CancellationToken cancellationToken;

            initializeCancellationTokenSource?.Cancel();
            initializeCancellationTokenSource = new CancellationTokenSource();

            cancellationToken = initializeCancellationTokenSource.Token;

            // ReSharper disable once MethodSupportsCancellation
            Task.Run(async () =>
            {
                await ApplyBindings(cancellationToken);

                if (consuming && !cancellationToken.IsCancellationRequested)
                    await ConsumeQueues(cancellationToken);
            });
        }


        /// <inheritdoc />
        public async Task Resume()
        {
            if (consuming)
                return;

            consuming = true;
            initializeCancellationTokenSource = new CancellationTokenSource();

            await ConsumeQueues(initializeCancellationTokenSource.Token);
        }



        private async Task ApplyBindings(CancellationToken cancellationToken)
        {
            var routingKeyStrategy = config.DependencyResolver.Resolve<IRoutingKeyStrategy>();
            var exchangeStrategy = config.DependencyResolver.Resolve<IExchangeStrategy>();

            CustomBindingTarget bindingTarget;

            if (config.Features.DeclareDurableQueues)
                bindingTarget = new DeclareDurableQueuesBindingTarget(clientFactory, routingKeyStrategy, exchangeStrategy, cancellationToken);
            else if (config.Features.VerifyDurableQueues)
                bindingTarget = new PassiveDurableQueuesBindingTarget(clientFactory, routingKeyStrategy, exchangeStrategy, cancellationToken);
            else
                bindingTarget = new NoVerifyBindingTarget(clientFactory, routingKeyStrategy, exchangeStrategy, cancellationToken);

            await Task.WhenAll(config.Bindings.Select(binding => binding.Apply(bindingTarget)));
            await bindingTarget.Apply();
        }


        private async Task ConsumeQueues(CancellationToken cancellationToken)
        {
            var queues = config.Bindings.GroupBy(binding => binding.QueueName);

            await Task.WhenAll(queues.Select(async group =>
            {
                var queueName = group.Key;
                var consumer = new TapetiConsumer(config, queueName, group);

                await clientFactory().Consume(cancellationToken, queueName, consumer);
            }));
        }


        private abstract class CustomBindingTarget : IBindingTarget
        {
            protected readonly Func<ITapetiClient> ClientFactory;
            protected readonly IRoutingKeyStrategy RoutingKeyStrategy;
            protected readonly IExchangeStrategy ExchangeStrategy;
            protected readonly CancellationToken CancellationToken;

            private struct DynamicQueueInfo
            {
                public string QueueName;
                public List<Type> MessageClasses;
            }

            private readonly Dictionary<string, List<DynamicQueueInfo>> dynamicQueues = new Dictionary<string, List<DynamicQueueInfo>>();


            protected CustomBindingTarget(Func<ITapetiClient> clientFactory, IRoutingKeyStrategy routingKeyStrategy, IExchangeStrategy exchangeStrategy, CancellationToken cancellationToken)
            {
                ClientFactory = clientFactory;
                RoutingKeyStrategy = routingKeyStrategy;
                ExchangeStrategy = exchangeStrategy;
                CancellationToken = cancellationToken;
            }


            public virtual Task Apply()
            {
                return Task.CompletedTask;
            }


            public abstract Task BindDurable(Type messageClass, string queueName);
            public abstract Task BindDurableDirect(string queueName);
            public abstract Task BindDurableObsolete(string queueName);


            public async Task<string> BindDynamic(Type messageClass, string queuePrefix = null)
            {
                var result = await DeclareDynamicQueue(messageClass, queuePrefix);
                if (!result.IsNewMessageClass) 
                    return result.QueueName;

                var routingKey = RoutingKeyStrategy.GetRoutingKey(messageClass);
                var exchange = ExchangeStrategy.GetExchange(messageClass);

                await ClientFactory().DynamicQueueBind(CancellationToken, result.QueueName, new QueueBinding(exchange, routingKey));

                return result.QueueName;
            }


            public async Task<string> BindDynamicDirect(Type messageClass, string queuePrefix = null)
            {
                var result = await DeclareDynamicQueue(messageClass, queuePrefix);
                return result.QueueName;
            }


            public async Task<string> BindDynamicDirect(string queuePrefix = null)
            {
                // If we don't know the routing key, always create a new queue to ensure there is no overlap.
                // Keep it out of the dynamicQueues dictionary, so it can't be re-used later on either.
                return await ClientFactory().DynamicQueueDeclare(CancellationToken, queuePrefix);
            }


            private struct DeclareDynamicQueueResult
            {
                public string QueueName;
                public bool IsNewMessageClass;
            }

            private async Task<DeclareDynamicQueueResult> DeclareDynamicQueue(Type messageClass, string queuePrefix)
            {
                // Group by prefix
                var key = queuePrefix ?? "";
                if (!dynamicQueues.TryGetValue(key, out var prefixQueues))
                {
                    prefixQueues = new List<DynamicQueueInfo>();
                    dynamicQueues.Add(key, prefixQueues);
                }

                // Ensure routing keys are unique per dynamic queue, so that a requeue
                // will not cause the side-effect of calling another handler again as well.
                foreach (var existingQueueInfo in prefixQueues)
                {
                    // ReSharper disable once InvertIf
                    if (!existingQueueInfo.MessageClasses.Contains(messageClass))
                    {
                        // Allow this routing key in the existing dynamic queue
                        var result = new DeclareDynamicQueueResult
                        {
                            QueueName = existingQueueInfo.QueueName,
                            IsNewMessageClass = !existingQueueInfo.MessageClasses.Contains(messageClass)
                        };

                        if (result.IsNewMessageClass)
                            existingQueueInfo.MessageClasses.Add(messageClass);

                        return result;
                    }
                }

                // Declare a new queue
                var queueName = await ClientFactory().DynamicQueueDeclare(CancellationToken, queuePrefix);
                var queueInfo = new DynamicQueueInfo
                {
                    QueueName = queueName,
                    MessageClasses = new List<Type> { messageClass }
                };

                prefixQueues.Add(queueInfo);

                return new DeclareDynamicQueueResult
                {
                    QueueName = queueName,
                    IsNewMessageClass = true
                };
            }
        }


        private class DeclareDurableQueuesBindingTarget : CustomBindingTarget
        {
            private readonly Dictionary<string, List<Type>> durableQueues = new Dictionary<string, List<Type>>();
            private readonly HashSet<string> obsoleteDurableQueues = new HashSet<string>();


            public DeclareDurableQueuesBindingTarget(Func<ITapetiClient> clientFactory, IRoutingKeyStrategy routingKeyStrategy, IExchangeStrategy exchangeStrategy, CancellationToken cancellationToken) : base(clientFactory, routingKeyStrategy, exchangeStrategy, cancellationToken)
            {
            }


            public override Task BindDurable(Type messageClass, string queueName)
            {
                // Collect the message classes per queue so we can determine afterwards
                // if any of the bindings currently set on the durable queue are no
                // longer valid and should be removed.
                if (!durableQueues.TryGetValue(queueName, out var messageClasses))
                {
                    durableQueues.Add(queueName, new List<Type>
                    {
                        messageClass
                    });
                }
                else if (!messageClasses.Contains(messageClass))
                    messageClasses.Add(messageClass);

                return Task.CompletedTask;
            }


            public override Task BindDurableDirect(string queueName)
            {
                if (!durableQueues.ContainsKey(queueName))
                    durableQueues.Add(queueName, new List<Type>());

                return Task.CompletedTask;
            }


            public override Task BindDurableObsolete(string queueName)
            {
                obsoleteDurableQueues.Add(queueName);
                return Task.CompletedTask;
            }


            public override async Task Apply()
            {
                var client = ClientFactory();
                await DeclareQueues(client);
                await DeleteObsoleteQueues(client);
            }


            private async Task DeclareQueues(ITapetiClient client)
            {
                await Task.WhenAll(durableQueues.Select(async queue =>
                {
                    var bindings = queue.Value.Select(messageClass =>
                    {
                        var exchange = ExchangeStrategy.GetExchange(messageClass);
                        var routingKey = RoutingKeyStrategy.GetRoutingKey(messageClass);

                        return new QueueBinding(exchange, routingKey);
                    });

                    await client.DurableQueueDeclare(CancellationToken, queue.Key, bindings);
                }));
            }


            private async Task DeleteObsoleteQueues(ITapetiClient client)
            {
                await Task.WhenAll(obsoleteDurableQueues.Except(durableQueues.Keys).Select(async queue =>
                {
                    await client.DurableQueueDelete(CancellationToken, queue);
                }));
            }
        }


        private class PassiveDurableQueuesBindingTarget : CustomBindingTarget
        {
            private readonly List<string> durableQueues = new List<string>();


            public PassiveDurableQueuesBindingTarget(Func<ITapetiClient> clientFactory, IRoutingKeyStrategy routingKeyStrategy, IExchangeStrategy exchangeStrategy, CancellationToken cancellationToken) : base(clientFactory, routingKeyStrategy, exchangeStrategy, cancellationToken)
            {
            }


            public override async Task BindDurable(Type messageClass, string queueName)
            {
                await VerifyDurableQueue(queueName);
            }

            public override async Task BindDurableDirect(string queueName)
            {
                await VerifyDurableQueue(queueName);
            }

            public override Task BindDurableObsolete(string queueName)
            {
                return Task.CompletedTask;
            }


            private async Task VerifyDurableQueue(string queueName)
            {
                if (!durableQueues.Contains(queueName))
                {
                    await ClientFactory().DurableQueueVerify(CancellationToken, queueName);
                    durableQueues.Add(queueName);
                }
            }
        }


        private class NoVerifyBindingTarget : CustomBindingTarget
        {
            public NoVerifyBindingTarget(Func<ITapetiClient> clientFactory, IRoutingKeyStrategy routingKeyStrategy, IExchangeStrategy exchangeStrategy, CancellationToken cancellationToken) : base(clientFactory, routingKeyStrategy, exchangeStrategy, cancellationToken)
            {
            }


            public override Task BindDurable(Type messageClass, string queueName)
            {
                return Task.CompletedTask;
            }

            public override Task BindDurableDirect(string queueName)
            {
                return Task.CompletedTask;
            }

            public override Task BindDurableObsolete(string queueName)
            {
                return Task.CompletedTask;
            }
        }
    }
}
