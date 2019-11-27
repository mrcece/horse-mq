using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Twino.MQ.Clients;
using Twino.MQ.Options;
using Twino.Protocols.TMQ;

namespace Twino.MQ
{
    /// <summary>
    /// Queue status
    /// </summary>
    public enum QueueStatus
    {
        /// <summary>
        /// Queue messaging is running. Messages are accepted and sent to queueus.
        /// </summary>
        Running,

        /// <summary>
        /// Queue messages are accepted and queued but not pending
        /// </summary>
        Paused,

        /// <summary>
        /// Queue messages are not accepted.
        /// </summary>
        Stopped
    }

    /// <summary>
    /// Channel queue.
    /// Keeps queued messages and subscribed clients.
    /// </summary>
    public class ChannelQueue
    {
        #region Properties

        /// <summary>
        /// Channel of the queue
        /// </summary>
        public Channel Channel { get; }

        /// <summary>
        /// Queue status
        /// </summary>
        public QueueStatus Status { get; private set; }

        /// <summary>
        /// Queue content type
        /// </summary>
        public ushort ContentType { get; }

        /// <summary>
        /// Queue options.
        /// If null, channel default options will be used
        /// </summary>
        public ChannelQueueOptions Options { get; }

        /// <summary>
        /// Queue event handler.
        /// If not null, all methods will be called when events are occured.
        /// If null, server's default event handler will be used.
        /// </summary>
        public IQueueEventHandler EventHandler { get; }

        /// <summary>
        /// Queue messaging handler.
        /// If null, server's default delivery will be used.
        /// </summary>
        public IMessageDeliveryHandler DeliveryHandler { get; }

        /// <summary>
        /// High priority message list
        /// </summary>
        private readonly LinkedList<QueueMessage> _prefentialMessages = new LinkedList<QueueMessage>();

        /// <summary>
        /// Low/Standard priority message list
        /// </summary>
        private readonly LinkedList<QueueMessage> _standardMessages = new LinkedList<QueueMessage>();

        /// <summary>
        /// Standard prefential messages
        /// </summary>
        public IEnumerable<QueueMessage> PrefentialMessages => _prefentialMessages;

        /// <summary>
        /// Standard queued messages
        /// </summary>
        public IEnumerable<QueueMessage> StandardMessages => _standardMessages;

        /// <summary>
        /// Default TMQ Writer class for the queue
        /// </summary>
        private static readonly TmqWriter _writer = new TmqWriter();

        /// <summary>
        /// Time keeper for the queue.
        /// Checks message receiver deadlines and delivery deadlines.
        /// </summary>
        private readonly QueueTimeKeeper _timeKeeper;

        #endregion

        #region Constructors

        internal ChannelQueue(Channel channel,
                              ushort contentType,
                              ChannelQueueOptions options,
                              IQueueEventHandler eventHandler,
                              IMessageDeliveryHandler deliveryHandler)
        {
            Channel = channel;
            ContentType = contentType;
            Options = options;

            EventHandler = eventHandler;
            DeliveryHandler = deliveryHandler;

            _timeKeeper = new QueueTimeKeeper(this, _prefentialMessages, _standardMessages);
            _timeKeeper.Run();
        }

        #endregion

        #region Status Actions

        /// <summary>
        /// Sets status of the queue
        /// </summary>
        public async Task SetStatus(QueueStatus status)
        {
            QueueStatus old = Status;
            if (old == status)
                return;

            if (EventHandler != null)
            {
                bool allowed = await EventHandler.OnStatusChanged(this, old, status);
                if (!allowed)
                    return;
            }

            Status = status;
        }

        #endregion

        #region Messaging Actions

        /// <summary>
        /// Adds a new message into the queue
        /// </summary>
        public async Task AddMessage(QueueMessage message)
        {
            if (EventHandler != null)
                await EventHandler.OnMessageReceived(this, message);

            await Trigger(message);
        }

        /// <summary>
        /// Removes the message from the queue.
        /// Remove operation will be canceled If force is false and message is not sent
        /// </summary>
        public async Task<bool> RemoveMessage(QueueMessage message, bool force = false)
        {
            if (!force && !message.IsSent())
                return false;

            if (message.Message.HighPriority)
                _prefentialMessages.Remove(message);
            else
                _standardMessages.Remove(message);

            if (EventHandler != null)
                await EventHandler.OnMessageRemoved(this, message);

            return true;
        }

        /// <summary>
        /// Checks all pending messages and subscribed receivers.
        /// If they should receive the messages, runs the process. 
        /// Should be called when a new message is added.
        /// </summary>
        internal async Task Trigger(QueueMessage message)
        {
            //if we have an option maximum wait duration for message, set it after message joined to the queue.
            //time keeper will check this value and if message time is up, it will remove message from the queue.
            if (Options.ReceiverWaitMaxDuration > TimeSpan.Zero)
                message.Deadline = DateTime.UtcNow.Add(Options.ReceiverWaitMaxDuration);

            //if message doesn't have message id and "UseMessageId" option is enabled, create new message id for the message
            if (Options.UseMessageId && string.IsNullOrEmpty(message.Message.MessageId))
                message.Message.MessageId = Channel.Server.MessageIdGenerator.Create();

            //if we are hiding client names and message have it, remove client name from the message
            if (Channel.Server.Options.HideClientNames && !string.IsNullOrEmpty(message.Message.Source))
            {
                message.Message.Source = null;
                message.Message.SourceLength = 0;
            }

            //process the message
            QueueMessage held = null;
            try
            {
                //queue the message
                if (Options.MessageQueuing)
                {
                    held = PullMessage(message);
                    await ProcesssMessage(held, true);
                }

                //process like MQTT
                else
                    await ProcesssMessage(message, false);
            }
            catch (Exception ex)
            {
                try
                {
                    await DeliveryHandler.OnException(this, Options.MessageQueuing ? held : message, ex);
                }
                catch //if developer does wrong operation, we should not stop
                {
                }
            }
        }

        /// <summary>
        /// Checks all pending messages and subscribed receivers.
        /// If they should receive the messages, runs the process. 
        /// Should be called when a new client is subscribed to the channel.
        /// </summary>
        internal async Task Trigger(ChannelClient subscribedClient)
        {
            if (!Options.MessageQueuing)
                return;

            if (_prefentialMessages.Count > 0)
                await ProcessPendingMessages(_prefentialMessages);

            if (_standardMessages.Count > 0)
                await ProcessPendingMessages(_standardMessages);
        }

        /// <summary>
        /// Start to process all pending messages.
        /// This method is called after a client is subscribed to the queue.
        /// </summary>
        private async Task ProcessPendingMessages(LinkedList<QueueMessage> list)
        {
            while (true)
            {
                QueueMessage message;
                lock (list)
                {
                    if (list.Count == 0)
                        return;

                    message = list.First.Value;
                    list.RemoveFirst();
                }

                try
                {
                    DeliveryOperation operation = await ProcesssMessage(message, Options.MessageQueuing);
                    if (operation == DeliveryOperation.Keep)
                        return;
                }
                catch (Exception ex)
                {
                    try
                    {
                        await DeliveryHandler.OnException(this, message, ex);
                    }
                    catch //if developer does wrong operation, we should not stop
                    {
                    }
                }
            }
        }

        /// <summary>
        /// Searches receivers of the message and process the send operation
        /// </summary>
        private async Task<DeliveryOperation> ProcesssMessage(QueueMessage message, bool onheld)
        {
            //if we need delivery, we are sending this information to receivers that we require response
            message.Message.ResponseRequired = Options.RequestDelivery;

            MessageDecision decision = await DeliveryHandler.OnSendStarting(this, message);

            //we save is chosen, save the message (if it's not saved before)
            if (decision == MessageDecision.AllowAndSave || decision == MessageDecision.SkipAndSave)
                if (!message.IsSaved)
                    message.IsSaved = await DeliveryHandler.SaveMessage(this, message);

            //if user skips the message, complete operation as skipped
            if (decision == MessageDecision.Skip || decision == MessageDecision.SkipAndSave)
            {
                message.IsSkipped = true;
                DeliveryOperation skipOperation = await DeliveryHandler.OnSendCompleted(this, message);

                await CompleteOperation(message, skipOperation);

                if (Options.MessageQueuing && onheld && skipOperation == DeliveryOperation.Keep)
                    PutMessageBack(message);

                return skipOperation;
            }

            List<ChannelClient> clients = Channel.ClientsClone;
            if (clients.Count == 0)
            {
                await CompleteOperation(message, DeliveryOperation.Keep);

                //if we are queuing, put the message back
                if (Options.MessageQueuing)
                    PutMessageBack(message);

                return DeliveryOperation.Keep;
            }

            //if we need delivery from receiver, it has a deadline.
            DateTime? deadline = null;
            if (Options.RequestDelivery)
                deadline = DateTime.UtcNow.Add(Options.DeliveryWaitMaxDuration);

            //create prepared message data
            byte[] messageData = await _writer.Create(message.Message);

            //to all receivers
            foreach (ChannelClient client in clients)
            {
                //to only online receivers
                if (!client.Client.IsConnected)
                    continue;

                //somehow if code comes here (it should not cuz of last "break" in this foreach, break
                if (!message.Message.FirstAcquirer && Options.SendOnlyFirstAcquirer)
                    break;

                //call before send and check decision
                DeliveryDecision beforeSend = await DeliveryHandler.OnBeforeSend(this, message, client.Client);
                if (beforeSend == DeliveryDecision.Skip)
                    continue;

                //create delivery object
                MessageDelivery delivery = new MessageDelivery(message, client, deadline);
                delivery.FirstAcquirer = message.Message.FirstAcquirer;

                //adds the delivery to time keeper to check timing up
                _timeKeeper.AddDeliveryCheck(delivery);

                //send the message
                await client.Client.SendAsync(messageData);

                //set as sent, if message is sent to it's first acquirer,
                //set message first acquirer false and re-create byte array data of the message
                bool firstAcquirer = message.Message.FirstAcquirer;
                message.AddSend(delivery);

                //do after send operations for per message
                await DeliveryHandler.OnAfterSend(this, delivery, client.Client);

                //if we are sending to only first acquirer, break
                if (Options.SendOnlyFirstAcquirer && firstAcquirer)
                    break;

                if (firstAcquirer && clients.Count > 1)
                    messageData = await _writer.Create(message.Message);
            }

            //after all sending operations completed, calls implementation send completed method and complete the operation
            DeliveryOperation operation = await DeliveryHandler.OnSendCompleted(this, message);

            await CompleteOperation(message, operation);

            if (operation == DeliveryOperation.Keep)
                PutMessageBack(message);

            return operation;
        }

        /// <summary>
        /// Adds the message to the queue and pulls first message from the queue.
        /// Usually first message equals message itself.
        /// But sometimes, previous messages might be pending in the queue.
        /// </summary>
        private QueueMessage PullMessage(QueueMessage message)
        {
            QueueMessage held;
            if (message.Message.HighPriority)
            {
                lock (_prefentialMessages)
                {
                    //we don't need push and pull
                    if (_prefentialMessages.Count == 0)
                        return message;

                    _prefentialMessages.AddLast(message);
                    held = _prefentialMessages.First.Value;
                    _prefentialMessages.RemoveFirst();
                }
            }
            else
            {
                lock (_standardMessages)
                {
                    //we don't need push and pull
                    if (_standardMessages.Count == 0)
                        return message;

                    _standardMessages.AddLast(message);
                    held = _standardMessages.First.Value;
                    _standardMessages.RemoveFirst();
                }
            }

            return held;
        }

        /// <summary>
        /// If there is no available receiver when after a message is helded to send to receivers,
        /// This methods puts the message back.
        /// </summary>
        private void PutMessageBack(QueueMessage message)
        {
            if (!Options.MessageQueuing)
                return;

            if (message.IsFirstQueue)
                message.IsFirstQueue = false;

            if (message.Message.HighPriority)
            {
                lock (_prefentialMessages)
                    _prefentialMessages.AddFirst(message);
            }
            else
            {
                lock (_standardMessages)
                    _standardMessages.AddFirst(message);
            }
        }

        /// <summary>
        /// Process and completes the delivery operation
        /// </summary>
        private async Task CompleteOperation(QueueMessage message, DeliveryOperation operation)
        {
            //keep the message in the queue, do nothing
            if (operation == DeliveryOperation.Keep)
                return;

            //save the message
            if (operation == DeliveryOperation.SaveMessage)
                message.IsSaved = await DeliveryHandler.SaveMessage(this, message);

            //remove the message
            await DeliveryHandler.OnRemove(this, message);
        }

        #endregion
    }
}