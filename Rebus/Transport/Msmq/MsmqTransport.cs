﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Messaging;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Rebus.Bus;
using Rebus.Extensions;
using Rebus.Logging;
using Rebus.Messages;
using Message = System.Messaging.Message;

namespace Rebus.Transport.Msmq
{
    /// <summary>
    /// Implementation of <see cref="ITransport"/> that uses MSMQ to do its thing
    /// </summary>
    public class MsmqTransport : ITransport, IInitializable, IDisposable
    {
        static ILog _log;

        static MsmqTransport()
        {
            RebusLoggerFactory.Changed += f => _log = f.GetCurrentClassLogger();
        }

        const string CurrentTransactionKey = "msmqtransport-messagequeuetransaction";
        const string CurrentOutgoingQueuesKey = "msmqtransport-outgoing-messagequeues";
        readonly ExtensionSerializer _extensionSerializer = new ExtensionSerializer();
        readonly string _inputQueueName;

        volatile MessageQueue _inputQueue;

        /// <summary>
        /// Constructs the transport with the specified input queue address
        /// </summary>
        public MsmqTransport(string inputQueueAddress)
        {
            if (inputQueueAddress == null) throw new ArgumentNullException("inputQueueAddress");

            _inputQueueName = MakeGloballyAddressable(inputQueueAddress);
        }

        ~MsmqTransport()
        {
            Dispose(false);
        }

        static string MakeGloballyAddressable(string inputQueueName)
        {
            return inputQueueName.Contains("@")
                ? inputQueueName
                : string.Format("{0}@{1}", inputQueueName, Environment.MachineName);
        }

        public void Initialize()
        {
            _log.Info("Initializing MSMQ transport - input queue: '{0}'", _inputQueueName);

            GetInputQueue();
        }

        public void CreateQueue(string address)
        {
            if (!MsmqUtil.IsLocal(address)) return;

            var inputQueuePath = MsmqUtil.GetPath(address);

            MsmqUtil.EnsureQueueExists(inputQueuePath);
        }

        /// <summary>
        /// Deletes all messages in the input queue
        /// </summary>
        public void PurgeInputQueue()
        {
            if (!MsmqUtil.QueueExists(_inputQueueName))
            {
                _log.Info("Purging {0} (but the queue doesn't exist...)", _inputQueueName);
                return;
            }

            _log.Info("Purging {0}", _inputQueueName);

            MsmqUtil.PurgeQueue(_inputQueueName);
        }

        public async Task Send(string destinationAddress, TransportMessage message, ITransactionContext context)
        {
            if (destinationAddress == null) throw new ArgumentNullException("destinationAddress");
            if (message == null) throw new ArgumentNullException("message");
            if (context == null) throw new ArgumentNullException("context");

            var logicalMessage = CreateMsmqMessage(message);

            var messageQueueTransaction = context.GetOrAdd(CurrentTransactionKey, () =>
            {
                var messageQueueTransaction1 = new MessageQueueTransaction();
                messageQueueTransaction1.Begin();

                context.OnCommitted(async () => messageQueueTransaction1.Commit());

                return messageQueueTransaction1;
            });

            var sendQueues = context.GetOrAdd(CurrentOutgoingQueuesKey, () =>
            {
                var messageQueues = new ConcurrentDictionary<string, MessageQueue>(StringComparer.InvariantCultureIgnoreCase);

                context.OnDisposed(() =>
                {
                    foreach (var messageQueue in messageQueues.Values)
                    {
                        messageQueue.Dispose();
                    }
                });

                return messageQueues;
            });

            var sendQueue = sendQueues.GetOrAdd(MsmqUtil.GetPath(destinationAddress), _ =>
            {
                var messageQueue = new MessageQueue(MsmqUtil.GetPath(destinationAddress), QueueAccessMode.Send);

                return messageQueue;
            });

            sendQueue.Send(logicalMessage, messageQueueTransaction);
        }

        Message CreateMsmqMessage(TransportMessage message)
        {
            var headers = message.Headers;

            var expressDelivery = headers.ContainsKey(Headers.Express);

            var hasTimeout = headers.ContainsKey(Headers.TimeToBeReceived);

            var msmqMessage = new Message
            {
                Extension = _extensionSerializer.Serialize(headers),
                BodyStream = new MemoryStream(message.Body),
                UseJournalQueue = false,
                Recoverable = !expressDelivery,
                UseDeadLetterQueue = !(expressDelivery || hasTimeout)
            };

            if (hasTimeout)
            {
                var timeToBeReceivedStr = headers[Headers.TimeToBeReceived];
                msmqMessage.TimeToBeReceived = TimeSpan.Parse(timeToBeReceivedStr);
            }

            return msmqMessage;
        }

        public async Task<TransportMessage> Receive(ITransactionContext context)
        {
            if (context == null) throw new ArgumentNullException("context");

            var queue = GetInputQueue();

            if (context.Items.ContainsKey(CurrentTransactionKey))
            {
                throw new InvalidOperationException("Tried to receive with an already existing MSMQ queue transaction - while that is possible, it's an indication that something is wrong!");
            }

            var messageQueueTransaction = new MessageQueueTransaction();
            messageQueueTransaction.Begin();

            context.OnCommitted(async () => messageQueueTransaction.Commit());
            context.OnDisposed(() => messageQueueTransaction.Dispose());

            context.Items[CurrentTransactionKey] = messageQueueTransaction;

            try
            {
                var message = queue.Receive(TimeSpan.FromSeconds(1), messageQueueTransaction);
                if (message == null)
                {
                    messageQueueTransaction.Abort();
                    return null;
                }

                var headers = _extensionSerializer.Deserialize(message.Extension, message.Id);
                var body = new byte[message.BodyStream.Length];

                await message.BodyStream.ReadAsync(body, 0, body.Length);

                return new TransportMessage(headers, body);
            }
            catch (MessageQueueException exception)
            {
                if (exception.MessageQueueErrorCode == MessageQueueErrorCode.IOTimeout)
                {
                    return null;
                }

                if (exception.MessageQueueErrorCode == MessageQueueErrorCode.InvalidHandle)
                {
                    _log.Info("Queue handle for '{0}' was invalid - will try to reinitialize the queue", _inputQueueName);
                    ReinitializeInputQueue();
                }

                if (exception.MessageQueueErrorCode == MessageQueueErrorCode.QueueDeleted)
                {
                    _log.Warn("Queue '{0}' was deleted - will not receive any more messages", _inputQueueName);
                    return null;
                }

                throw new IOException(
                    string.Format("Could not receive next message from MSMQ queue '{0}'", _inputQueueName),
                    exception);
            }
        }

        public string Address
        {
            get { return _inputQueueName; }
        }

        void ReinitializeInputQueue()
        {
            if (_inputQueue != null)
            {
                try
                {
                    _inputQueue.Close();
                    _inputQueue.Dispose();
                }
                catch (Exception exception)
                {
                    _log.Warn("An error occurred when closing/disposing the queue handle for '{0}': {1}", _inputQueueName, exception);
                }
                finally
                {
                    _inputQueue = null;
                }
            }

            GetInputQueue();

            _log.Info("Input queue handle successfully reinitialized");
        }

        MessageQueue GetInputQueue()
        {
            if (_inputQueue != null) return _inputQueue;

            lock (this)
            {
                if (_inputQueue != null) return _inputQueue;

                var inputQueuePath = MsmqUtil.GetPath(_inputQueueName);

                MsmqUtil.EnsureQueueExists(inputQueuePath);

                MsmqUtil.EnsureMessageQueueIsTransactional(inputQueuePath);

                _inputQueue = new MessageQueue(inputQueuePath, QueueAccessMode.SendAndReceive)
                {
                    MessageReadPropertyFilter = new MessagePropertyFilter
                    {
                        Id = true,
                        Extension = true,
                        Body = true,
                    }
                };
            }

            return _inputQueue;
        }

        class ExtensionSerializer
        {
            static readonly Encoding DefaultEncoding = Encoding.UTF8;

            public byte[] Serialize(Dictionary<string, string> headers)
            {
                var jsonString = JsonConvert.SerializeObject(headers);

                return DefaultEncoding.GetBytes(jsonString);
            }

            public Dictionary<string, string> Deserialize(byte[] bytes, string msmqMessageId)
            {
                var jsonString = DefaultEncoding.GetString(bytes);

                try
                {
                    return JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonString);
                }
                catch (Exception exception)
                {
                    throw new SerializationException(string.Format("Could not deserialize MSMQ extension for message with physical message ID {0} - expected valid JSON text, got '{1}'", 
                        msmqMessageId, jsonString), exception);
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Disposes the input queue instance
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_inputQueue != null)
            {
                _inputQueue.Dispose();
                _inputQueue = null;
            }
        }
    }
}