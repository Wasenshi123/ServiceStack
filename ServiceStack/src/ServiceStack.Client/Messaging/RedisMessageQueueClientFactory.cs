//Copyright (c) ServiceStack, Inc. All Rights Reserved.
//License: https://raw.github.com/ServiceStack/ServiceStack/master/license.txt

using ServiceStack.Redis;
using System;

namespace ServiceStack.Messaging
{
    public class RedisMessageQueueClientFactory
        : IMessageQueueClientFactory
    {
        private readonly Action onPublishedCallback;
        private readonly IRedisClientsManager clientsManager;

        public RedisMessageQueueClientFactory(
            IRedisClientsManager clientsManager, Action onPublishedCallback)
        {
            this.onPublishedCallback = onPublishedCallback;
            this.clientsManager = clientsManager;
        }

        public IMessageQueueClient CreateMessageQueueClient()
        {
            return new RedisMessageQueueClient(
                this.clientsManager, this.onPublishedCallback);
        }

        public void Dispose()
        {
        }
    }
}