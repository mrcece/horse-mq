﻿namespace Horse.Messaging.Server.Queues;

/// <summary>
/// The strategy when message queue limit is exceed and a new message received.
/// </summary>
public enum MessageLimitExceededStrategy
{
    /// <summary>
    /// Rejects new message and sends negative acknowledge to producer
    /// </summary>
    RejectNewMessage,

    /// <summary>
    /// Deletes oldest message from the queue and adds new message
    /// </summary>
    DeleteOldestMessage
}