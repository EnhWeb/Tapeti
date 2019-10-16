﻿using RabbitMQ.Client;
using Tapeti.Cmd.Serialization;

namespace Tapeti.Cmd.Commands
{
    public class ImportCommand
    {
        public ConnectionFactory ConnectionFactory { get; set; }
        public IMessageSerializer MessageSerializer { get; set; }

        public bool DirectToQueue { get; set; }


        public int Execute()
        {
            using (var connection = ConnectionFactory.CreateConnection())
            {
                using (var channel = connection.CreateModel())
                {
                    return PublishMessages(channel);
                }
            }
        }


        private int PublishMessages(IModel channel)
        {
            var messageCount = 0;

            foreach (var message in MessageSerializer.Deserialize())
            {
                var exchange = DirectToQueue ? "" : message.Exchange;
                var routingKey = DirectToQueue ? message.Queue : message.RoutingKey;

                channel.BasicPublish(exchange, routingKey, message.Properties, message.Body);
                messageCount++;
            }

            return messageCount;
        }
    }
}
