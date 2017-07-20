﻿namespace NServiceBus.Transports.SQS
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Amazon.S3;
    using Amazon.S3.Model;
    using Amazon.SQS;
    using Amazon.SQS.Model;
    using AmazonSQS;
    using Extensibility;
    using Logging;
    using Newtonsoft.Json;
    using Transport;

    class MessagePump : IPushMessages
    {
        public MessagePump(ConnectionConfiguration configuration, IAmazonS3 s3Client, IAmazonSQS sqsClient, QueueUrlCache queueUrlCache)
        {
            this.configuration = configuration;
            this.s3Client = s3Client;
            this.sqsClient = sqsClient;
            this.queueUrlCache = queueUrlCache;
        }

        public async Task Init(Func<MessageContext, Task> onMessage, Func<ErrorContext, Task<ErrorHandleResult>> onError, CriticalError criticalError, PushSettings settings)
        {
            _queueUrl = queueUrlCache.GetQueueUrl(QueueNameHelper.GetSqsQueueName(settings.InputQueue, configuration));

            if (settings.PurgeOnStartup)
            {
                // SQS only allows purging a queue once every 60 seconds or so.
                // If you try to purge a queue twice in relatively quick succession,
                // PurgeQueueInProgressException will be thrown.
                // This will happen if you are trying to start an endpoint twice or more
                // in that time.
                try
                {
                    await sqsClient.PurgeQueueAsync(_queueUrl).ConfigureAwait(false);
                }
                catch (PurgeQueueInProgressException ex)
                {
                    Logger.Warn("Multiple queue purges within 60 seconds are not permitted by SQS.", ex);
                }
                catch (Exception ex)
                {
                    Logger.Error("Exception thrown from PurgeQueue.", ex);
                    throw;
                }
            }

            _onMessage = onMessage;
            _onError = onError;
        }

        public void Start(PushRuntimeSettings limitations)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _concurrencyLevel = limitations.MaxConcurrency;
            _maxConcurrencySempahore = new SemaphoreSlim(_concurrencyLevel);
            _consumerTasks = new List<Task>();

            for (var i = 0; i < _concurrencyLevel; i++)
            {
                _consumerTasks.Add(ConsumeMessages());
            }
        }

        public async Task Stop()
        {
            _cancellationTokenSource?.Cancel();

            try
            {
                await Task.WhenAll(_consumerTasks.ToArray()).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Silently swallow OperationCanceledException
                // These are expected to be thrown when _cancellationTokenSource
                // is Cancel()ed above.
            }

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }

        async Task ConsumeMessages()
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    var receiveResult = await sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
                        {
                            MaxNumberOfMessages = 10,
                            QueueUrl = _queueUrl,
                            WaitTimeSeconds = 20,
                            AttributeNames = new List<String>
                            {
                                "SentTimestamp"
                            }
                        },
                        _cancellationTokenSource.Token).ConfigureAwait(false);

                    var tasks = receiveResult.Messages.Select(async message =>
                    {
                        IncomingMessage incomingMessage = null;
                        TransportMessage transportMessage = null;
                        var transportTransaction = new TransportTransaction();
                        var contextBag = new ContextBag();

                        var messageProcessedOk = false;
                        var messageExpired = false;
                        var isPoisonMessage = false;
                        var errorHandled = false;

                        try
                        {
                            transportMessage = JsonConvert.DeserializeObject<TransportMessage>(message.Body);

                            incomingMessage = await transportMessage.ToIncomingMessage(s3Client,
                                configuration,
                                _cancellationTokenSource.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            // Can't deserialize. This is a poison message
                            Logger.Warn($"Deleting poison message with SQS Message Id {message.MessageId} due to exception {ex}");
                            isPoisonMessage = true;
                        }

                        if (incomingMessage == null || transportMessage == null)
                        {
                            Logger.Warn($"Deleting poison message with SQS Message Id {message.MessageId}");
                            isPoisonMessage = true;
                        }

                        if (isPoisonMessage)
                        {
                            await DeleteMessage(sqsClient,
                                s3Client,
                                message,
                                transportMessage,
                                incomingMessage).ConfigureAwait(false);
                        }
                        else
                        {
                            // Check that the message hasn't expired
                            string rawTtbr;
                            if (incomingMessage.Headers.TryGetValue(TransportHeaders.TimeToBeReceived, out rawTtbr))
                            {
                                incomingMessage.Headers.Remove(TransportHeaders.TimeToBeReceived);

                                var timeToBeReceived = TimeSpan.Parse(rawTtbr);

                                if (timeToBeReceived != TimeSpan.MaxValue)
                                {
                                    var sentDateTime = message.GetSentDateTime();
                                    var utcNow = DateTime.UtcNow;
                                    if (sentDateTime + timeToBeReceived <= utcNow)
                                    {
                                        // Message has expired.
                                        Logger.Warn($"Discarding expired message with Id {incomingMessage.MessageId}");
                                        messageExpired = true;
                                    }
                                }
                            }


                            if (!messageExpired)
                            {
                                var immediateProcessingAttempts = 0;

                                while (!errorHandled && !messageProcessedOk)
                                {
                                    try
                                    {
                                        await _maxConcurrencySempahore
                                            .WaitAsync(_cancellationTokenSource.Token)
                                            .ConfigureAwait(false);

                                        using (var messageContextCancellationTokenSource =
                                            new CancellationTokenSource())
                                        {
                                            var messageContext = new MessageContext(
                                                incomingMessage.MessageId,
                                                incomingMessage.Headers,
                                                incomingMessage.Body,
                                                transportTransaction,
                                                messageContextCancellationTokenSource,
                                                contextBag);

                                            await _onMessage(messageContext)
                                                .ConfigureAwait(false);

                                            messageProcessedOk = !messageContextCancellationTokenSource.IsCancellationRequested;
                                        }
                                    }
                                    catch (Exception ex)
                                        when (!(ex is OperationCanceledException && _cancellationTokenSource.IsCancellationRequested))
                                    {
                                        immediateProcessingAttempts++;
                                        var errorHandlerResult = ErrorHandleResult.RetryRequired;

                                        try
                                        {
                                            errorHandlerResult = await _onError(new ErrorContext(ex,
                                                incomingMessage.Headers,
                                                incomingMessage.MessageId,
                                                incomingMessage.Body,
                                                transportTransaction,
                                                immediateProcessingAttempts)).ConfigureAwait(false);
                                        }
                                        catch (Exception onErrorEx)
                                        {
                                            Logger.Error("Exception thrown from error handler", onErrorEx);
                                        }
                                        errorHandled = errorHandlerResult == ErrorHandleResult.Handled;
                                    }
                                    finally
                                    {
                                        _maxConcurrencySempahore.Release();
                                    }
                                }
                            }

                            // Always delete the message from the queue.
                            // If processing failed, the _onError handler will have moved the message
                            // to a retry queue.
                            await DeleteMessage(sqsClient, s3Client, message, transportMessage, incomingMessage).ConfigureAwait(false);
                        }
                    });

                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (Exception ex)
                    when (!(ex is OperationCanceledException && _cancellationTokenSource.IsCancellationRequested))
                {
                    Logger.Error("Exception thrown when consuming messages", ex);
                }
            } // while
        }

        async Task DeleteMessage(IAmazonSQS sqs,
            IAmazonS3 s3,
            Message message,
            TransportMessage transportMessage,
            IncomingMessage incomingMessage)
        {
            await sqs.DeleteMessageAsync(_queueUrl, message.ReceiptHandle, _cancellationTokenSource.Token).ConfigureAwait(false);

            if (transportMessage != null)
            {
                if (!String.IsNullOrEmpty(transportMessage.S3BodyKey))
                {
                    try
                    {
                        await s3.DeleteObjectAsync(
                            new DeleteObjectRequest
                            {
                                BucketName = configuration.S3BucketForLargeMessages,
                                Key = configuration.S3KeyPrefix + incomingMessage.MessageId
                            },
                            _cancellationTokenSource.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // If deleting the message body from S3 fails, we don't
                        // want the exception to make its way through to the _endProcessMessage below,
                        // as the message has been successfully processed and deleted from the SQS queue
                        // and effectively doesn't exist anymore.
                        // It doesn't really matter, as S3 is configured to delete message body data
                        // automatically after a certain period of time.
                        Logger.Warn("Couldn't delete message body from S3. Message body data will be aged out by the S3 lifecycle policy when the TTL expires.", ex);
                    }
                }
            }
            else
            {
                Logger.Warn("Couldn't delete message body from S3 because the TransportMessage was null. Message body data will be aged out by the S3 lifecycle policy when the TTL expires.");
            }
        }

        CancellationTokenSource _cancellationTokenSource;
        List<Task> _consumerTasks;
        Func<ErrorContext, Task<ErrorHandleResult>> _onError;
        Func<MessageContext, Task> _onMessage;
        SemaphoreSlim _maxConcurrencySempahore;
        string _queueUrl;
        int _concurrencyLevel;
        ConnectionConfiguration configuration;
        IAmazonS3 s3Client;
        IAmazonSQS sqsClient;
        QueueUrlCache queueUrlCache;

        static ILog Logger = LogManager.GetLogger(typeof(MessagePump));
    }
}