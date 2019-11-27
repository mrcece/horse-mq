using System.Threading.Tasks;

namespace Twino.MQ.Channels
{
    /// <summary>
    /// Messaging queue event handler implementation (subs, unsubs, message add/remove, status changes)
    /// </summary>
    public interface IQueueEventHandler
    {
        /// <summary>
        /// Called when a new message is received to the queue.
        /// </summary>
        Task OnMessageReceived(ChannelQueue queue, QueueMessage message);

        /// <summary>
        /// Called when a new message is received, no receiver found and queued.
        /// </summary>
        Task OnMessageQueued(ChannelQueue queue, QueueMessage message);

        /// <summary>
        /// Called when a message removed from the queue
        /// </summary>
        Task OnMessageRemoved(ChannelQueue queue, QueueMessage message);

        /// <summary>
        /// Called when queue status has changed
        /// </summary>
        Task<bool> OnStatusChanged(ChannelQueue queue, QueueStatus from, QueueStatus to);
    }
}