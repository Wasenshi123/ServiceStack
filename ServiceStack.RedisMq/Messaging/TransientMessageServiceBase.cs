#if !SL5 
using ServiceStack.Text;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ServiceStack.Messaging
{
    public abstract class TransientMessageServiceBase
        : IMessageService, IMessageHandlerDisposer
    {
        private bool isRunning;
        public const int DefaultRetryCount = 2; //Will be a total of 3 attempts

        public int RetryCount { get; set; }
        public TimeSpan? RequestTimeOut { get; protected set; }

        public int PoolSize { get; protected set; } //use later

        public abstract IMessageFactory MessageFactory { get; }

        protected TransientMessageServiceBase()
            : this(DefaultRetryCount, null)
        {
        }

        protected TransientMessageServiceBase(int retryAttempts, TimeSpan? requestTimeOut)
        {
            this.RetryCount = retryAttempts;
            this.RequestTimeOut = requestTimeOut;
        }

        private readonly Dictionary<Type, IMessageHandlerFactory> handlerMap
            = new Dictionary<Type, IMessageHandlerFactory>();

        private IMessageHandler[] messageHandlers;

        public void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn)
        {
            RegisterHandler(processMessageFn, null, noOfThreads: 1);
        }

        public void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn, int noOfThreads)
        {
            RegisterHandler(processMessageFn, null, noOfThreads: 1);
        }

        public void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn,
            Action<IMessageHandler, IMessage<T>, Exception> processExceptionEx)
        {
            RegisterHandler(processMessageFn, processExceptionEx, noOfThreads: 1);
        }

        public void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn, Action<IMessageHandler, IMessage<T>, Exception> processExceptionEx, int noOfThreads)
        {
            if (handlerMap.ContainsKey(typeof(T)))
            {
                throw new ArgumentException("Message handler has already been registered for type: " + typeof(T).GetOperationName());
            }

            handlerMap[typeof(T)] = CreateMessageHandlerFactory(processMessageFn, processExceptionEx);
        }

        public IMessageHandlerStats GetStats()
        {
            var total = new MessageHandlerStats("All Handlers");
            messageHandlers.Each(x => total.Add(x.GetStats()));
            return total;
        }

        public List<Type> RegisteredTypes => handlerMap.Keys.ToList();

        public string GetStatus()
        {
            return isRunning ? "Started" : "Stopped";
        }

        public string GetStatsDescription()
        {
            var sb = StringBuilderCache.Allocate();
            sb.Append("#MQ HOST STATS:\n");
            sb.AppendLine("===============");
            foreach (var messageHandler in messageHandlers)
            {
                sb.AppendLine(messageHandler.GetStats().ToString());
                sb.AppendLine("---------------");
            }
            return StringBuilderCache.ReturnAndFree(sb);
        }

        protected IMessageHandlerFactory CreateMessageHandlerFactory<T>(
            Func<IMessage<T>, object> processMessageFn,
            Action<IMessageHandler, IMessage<T>, Exception> processExceptionEx)
        {
            return new MessageHandlerFactory<T>(this, processMessageFn, processExceptionEx)
            {
                RetryCount = RetryCount,
            };
        }

        public virtual void Start()
        {
            isRunning = true;

            lock (handlerMap)
            {
                if (messageHandlers == null)
                {
                    messageHandlers = this.handlerMap.Values.ToList().ConvertAll(
                        x => x.CreateMessageHandler()).ToArray();
                }

                using (var mqClient = MessageFactory.CreateMessageQueueClient())
                {
                    foreach (var handler in messageHandlers)
                    {
                        handler.Process(mqClient);
                    }
                }
            }

            this.Stop();
        }

        public virtual void Stop()
        {
            isRunning = false;
            lock (handlerMap)
            {
                messageHandlers = null;
            }
        }

        public virtual void Dispose()
        {
            Stop();
        }

        public virtual void DisposeMessageHandler(IMessageHandler messageHandler)
        {
            lock (handlerMap)
            {
                if (!isRunning) return;

                var allHandlersAreDisposed = true;
                for (var i = 0; i < messageHandlers.Length; i++)
                {
                    if (messageHandlers[i] == messageHandler)
                    {
                        messageHandlers[i] = null;
                    }
                    allHandlersAreDisposed = allHandlersAreDisposed
                        && messageHandlers[i] == null;
                }

                if (allHandlersAreDisposed)
                {
                    Stop();
                }
            }
        }
    }
}
#endif