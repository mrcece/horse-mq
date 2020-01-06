using System;
using System.Linq;
using System.Threading.Tasks;
using Twino.Client.TMQ;
using Twino.MQ.Clients;
using Twino.MQ.Helpers;
using Twino.MQ.Options;
using Twino.MQ.Queues;
using Twino.MQ.Security;
using Twino.Protocols.TMQ;

namespace Twino.MQ.Network
{
    internal class ServerMessageHandler : INetworkMessageHandler
    {
        #region Fields

        /// <summary>
        /// Messaging Queue Server
        /// </summary>
        private readonly MqServer _server;

        public ServerMessageHandler(MqServer server)
        {
            _server = server;
        }

        #endregion


        public async Task Handle(MqClient client, TmqMessage message)
        {
            switch (message.ContentType)
            {
                //join to a channel
                case KnownContentTypes.Join:
                    await JoinChannel(client, message);
                    break;

                //leave from a channel
                case KnownContentTypes.Leave:
                    await LeaveChannel(client, message);
                    break;

                //create only channel
                case KnownContentTypes.CreateChannel:
                    await CreateChannel(client, message, false);
                    break;

                //get channel information
                case KnownContentTypes.ChannelInformation:
                    await GetChannelInformation(client, message);
                    break;

                //remove channel and queues in it
                case KnownContentTypes.RemoveChannel:
                    await RemoveChannel(client, message);
                    break;

                //creates new queue
                case KnownContentTypes.CreateQueue:
                    await CreateQueue(client, message);
                    break;

                //remove only queue
                case KnownContentTypes.RemoveQueue:
                    await RemoveQueue(client, message);
                    break;

                //update queue
                case KnownContentTypes.UpdateQueue:
                    await UpdateQueue(client, message);
                    break;

                //get queue information
                case KnownContentTypes.QueueInformation:
                    await GetQueueInformation(client, message);
                    break;
            }
        }

        #region Channel

        /// <summary>
        /// Finds and joins to channel and sends response
        /// </summary>
        private async Task JoinChannel(MqClient client, TmqMessage message)
        {
            Channel channel = _server.FindChannel(message.Target);

            //if auto creation active, try to create channel
            if (channel == null && _server.Options.AutoChannelCreation)
                channel = _server.CreateChannel(message.Target);

            if (channel == null)
            {
                if (message.ResponseRequired)
                    await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.NotFound));

                return;
            }

            ChannelClient found = channel.FindClient(client.UniqueId);
            if (found != null)
            {
                if (message.ResponseRequired)
                    await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.Ok));

                return;
            }

            bool grant = await channel.AddClient(client);
            if (message.ResponseRequired)
                await client.SendAsync(MessageBuilder.ResponseStatus(message, grant ? KnownContentTypes.Ok : KnownContentTypes.Unauthorized));
        }

        /// <summary>
        /// Leaves from the channel and sends response
        /// </summary>
        private async Task LeaveChannel(MqClient client, TmqMessage message)
        {
            Channel channel = _server.FindChannel(message.Target);
            if (channel == null)
            {
                if (message.ResponseRequired)
                    await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.NotFound));

                return;
            }

            bool success = await channel.RemoveClient(client);

            if (message.ResponseRequired)
                await client.SendAsync(MessageBuilder.ResponseStatus(message, success ? KnownContentTypes.Ok : KnownContentTypes.NotFound));
        }

        /// <summary>
        /// Creates new channel
        /// </summary>
        private async Task<Channel> CreateChannel(MqClient client, TmqMessage message, bool createForQueue)
        {
            Channel channel = _server.FindChannel(message.Target);
            if (channel != null)
                return channel;

            //check create channel access
            if (_server.Authorization != null)
            {
                bool grant = await _server.Authorization.CanCreateChannel(client, _server, message.Target);
                if (!grant)
                {
                    if (message.ResponseRequired)
                        await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.Unauthorized));

                    return null;
                }
            }

            if (!createForQueue && message.Length > 0 && message.Content != null && message.Content.Length > 0)
            {
                NetworkOptionsBuilder builder = new NetworkOptionsBuilder();
                builder.Load(message.ToString());

                ChannelOptions options = ChannelOptions.CloneFrom(_server.Options);
                builder.ApplyToChannel(options);

                IChannelEventHandler eventHandler = _server.DefaultChannelEventHandler;
                if (!string.IsNullOrEmpty(builder.ChannelEventHandler))
                {
                    IChannelEventHandler e = _server.Registry.GetChannelEvent(builder.ChannelEventHandler);
                    if (e != null)
                        eventHandler = e;
                }

                IChannelAuthenticator authenticator = _server.DefaultChannelAuthenticator;
                if (!string.IsNullOrEmpty(builder.ChannelAuthenticator))
                {
                    IChannelAuthenticator e = _server.Registry.GetChannelAuthenticator(builder.ChannelAuthenticator);
                    if (e != null)
                        authenticator = e;
                }

                IMessageDeliveryHandler deliveryHandler = _server.DefaultDeliveryHandler;
                if (!string.IsNullOrEmpty(builder.MessageDeliveryHandler))
                {
                    IMessageDeliveryHandler e = _server.Registry.GetMessageDelivery(builder.MessageDeliveryHandler);
                    if (e != null)
                        deliveryHandler = e;
                }

                Channel ch = _server.CreateChannel(message.Target, authenticator, eventHandler, deliveryHandler, options);

                if (ch != null && message.ResponseRequired)
                    await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.Ok));
            }

            Channel c = _server.CreateChannel(message.Target);

            if (!createForQueue && c != null && message.ResponseRequired)
                await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.Ok));

            return c;
        }

        /// <summary>
        /// Removes a channel with it's queues
        /// </summary>
        private async Task RemoveChannel(MqClient client, TmqMessage message)
        {
            Channel channel = _server.FindChannel(message.Target);
            if (channel == null)
            {
                await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.NotFound));
                return;
            }

            //check remove channel access
            if (_server.Authorization != null)
            {
                bool grant = await _server.Authorization.CanRemoveChannel(client, _server, channel);
                if (!grant)
                {
                    if (message.ResponseRequired)
                        await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.Unauthorized));

                    return;
                }
            }

            await _server.RemoveChannel(channel);
        }

        /// <summary>
        /// Finds the channel and sends the information
        /// </summary>
        private async Task GetChannelInformation(MqClient client, TmqMessage message)
        {
            Channel channel = _server.FindChannel(message.Target);
            if (channel == null)
            {
                await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.NotFound));
                return;
            }

            //authenticate for channel
            if (_server.DefaultChannelAuthenticator != null)
            {
                bool grant = await _server.DefaultChannelAuthenticator.Authenticate(channel, client);
                if (!grant)
                {
                    await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.Unauthorized));
                    return;
                }
            }

            ChannelInformation information = new ChannelInformation
                                             {
                                                 Name = channel.Name,
                                                 Queues = channel.QueuesClone.Select(x => x.Id).ToArray(),
                                                 AllowMultipleQueues = channel.Options.AllowMultipleQueues,
                                                 AllowedQueues = channel.Options.AllowedQueues,
                                                 SendOnlyFirstAcquirer = channel.Options.SendOnlyFirstAcquirer,
                                                 RequestAcknowledge = channel.Options.RequestAcknowledge,
                                                 AcknowledgeTimeout = Convert.ToInt32(channel.Options.AcknowledgeTimeout.TotalMilliseconds),
                                                 MessageTimeout = Convert.ToInt32(channel.Options.MessageTimeout),
                                                 WaitForAcknowledge = channel.Options.WaitForAcknowledge,
                                                 HideClientNames = channel.Options.HideClientNames
                                             };

            TmqMessage response = message.CreateResponse();
            message.ContentType = KnownContentTypes.ChannelInformation;
            await response.SetJsonContent(information);
            await client.SendAsync(response);
        }

        #endregion

        #region Queue

        /// <summary>
        /// Creates new queue and sends response
        /// </summary>
        private async Task CreateQueue(MqClient client, TmqMessage message)
        {
            ushort? contentType;
            NetworkOptionsBuilder builder = null;
            if (message.Length == 2)
            {
                byte[] bytes = new byte[2];
                await message.Content.ReadAsync(bytes);
                contentType = BitConverter.ToUInt16(bytes);
            }
            else
            {
                builder = new NetworkOptionsBuilder();
                builder.Load(message.ToString());
                contentType = builder.Id;
            }

            Channel channel = await CreateChannel(client, message, true);
            ChannelQueue queue = channel.FindQueue(contentType.Value);

            //if queue exists, we can't create. return duplicate response.
            if (queue != null)
            {
                if (message.ResponseRequired)
                    await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.Duplicate));

                return;
            }

            //check authority if client can create queue
            if (_server.Authorization != null)
            {
                bool grant = await _server.Authorization.CanCreateQueue(client, channel, contentType.Value, builder);
                if (!grant)
                {
                    if (message.ResponseRequired)
                        await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.Unauthorized));

                    return;
                }
            }

            //creates new queue
            ChannelQueueOptions options = ChannelQueueOptions.CloneFrom(channel.Options);
            IMessageDeliveryHandler delivery = channel.DeliveryHandler;
            if (builder != null)
            {
                builder.ApplyToQueue(options);
                if (!string.IsNullOrEmpty(builder.MessageDeliveryHandler))
                {
                    IMessageDeliveryHandler found = _server.Registry.GetMessageDelivery(builder.MessageDeliveryHandler);
                    if (found != null)
                        delivery = found;
                }
            }

            queue = await channel.CreateQueue(contentType.Value, options, delivery);

            //if creation successful, sends response
            if (queue != null && message.ResponseRequired)
                await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.Ok));
        }

        /// <summary>
        /// Removes a queue from a channel
        /// </summary>
        private async Task RemoveQueue(MqClient client, TmqMessage message)
        {
            Channel channel = _server.FindChannel(message.Target);
            if (channel == null)
            {
                await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.NotFound));
                return;
            }

            byte[] bytes = new byte[2];
            await message.Content.ReadAsync(bytes);
            ushort contentType = BitConverter.ToUInt16(bytes);

            ChannelQueue queue = channel.FindQueue(contentType);
            if (queue == null)
            {
                await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.NotFound));
                return;
            }

            //check authority if client can create queue
            if (_server.Authorization != null)
            {
                bool grant = await _server.Authorization.CanRemoveQueue(client, queue);
                if (!grant)
                {
                    if (message.ResponseRequired)
                        await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.Unauthorized));

                    return;
                }
            }

            await channel.RemoveQueue(queue);

            if (message.ResponseRequired)
                await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.Ok));
        }

        /// <summary>
        /// Creates new queue and sends response
        /// </summary>
        private async Task UpdateQueue(MqClient client, TmqMessage message)
        {
            NetworkOptionsBuilder builder = new NetworkOptionsBuilder();
            builder.Load(message.ToString());

            Channel channel = _server.FindChannel(message.Target);
            if (channel == null)
            {
                if (message.ResponseRequired)
                    await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.NotFound));

                return;
            }

            ChannelQueue queue = channel.FindQueue(builder.Id);
            if (queue == null)
            {
                if (message.ResponseRequired)
                    await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.NotFound));

                return;
            }

            //check authority if client can create queue
            if (_server.Authorization != null)
            {
                bool grant = await _server.Authorization.CanUpdateQueueOptions(client, channel, queue, builder);
                if (!grant)
                {
                    if (message.ResponseRequired)
                        await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.Unauthorized));

                    return;
                }
            }

            builder.ApplyToQueue(queue.Options);
            if (builder.Status.HasValue)
                await queue.SetStatus(builder.Status.Value);

            //if creation successful, sends response
            if (message.ResponseRequired)
                await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.Ok));
        }

        /// <summary>
        /// Finds the queue and sends the information
        /// </summary>
        private async Task GetQueueInformation(MqClient client, TmqMessage message)
        {
            Channel channel = _server.FindChannel(message.Target);
            if (channel == null)
            {
                await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.NotFound));
                return;
            }

            //authenticate for channel
            if (_server.DefaultChannelAuthenticator != null)
            {
                bool grant = await _server.DefaultChannelAuthenticator.Authenticate(channel, client);
                if (!grant)
                {
                    await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.Unauthorized));
                    return;
                }
            }

            byte[] bytes = new byte[2];
            await message.Content.ReadAsync(bytes);
            ushort id = BitConverter.ToUInt16(bytes);
            ChannelQueue queue = channel.FindQueue(id);
            if (queue == null)
            {
                await client.SendAsync(MessageBuilder.ResponseStatus(message, KnownContentTypes.NotFound));
                return;
            }

            QueueInformation information = new QueueInformation
                                           {
                                               Channel = channel.Name,
                                               Id = id,
                                               Status = queue.Status.ToString().ToLower(),
                                               HighPriorityMessages = queue.HighPriorityLinkedList.Count,
                                               RegularMessages = queue.RegularLinkedList.Count,
                                               SendOnlyFirstAcquirer = channel.Options.SendOnlyFirstAcquirer,
                                               RequestAcknowledge = channel.Options.RequestAcknowledge,
                                               AcknowledgeTimeout = Convert.ToInt32(channel.Options.AcknowledgeTimeout.TotalMilliseconds),
                                               MessageTimeout = Convert.ToInt32(channel.Options.MessageTimeout),
                                               WaitForAcknowledge = channel.Options.WaitForAcknowledge,
                                               HideClientNames = channel.Options.HideClientNames
                                           };

            TmqMessage response = message.CreateResponse();
            message.ContentType = KnownContentTypes.ChannelInformation;
            await response.SetJsonContent(information);
            await client.SendAsync(response);
        }

        #endregion
    }
}