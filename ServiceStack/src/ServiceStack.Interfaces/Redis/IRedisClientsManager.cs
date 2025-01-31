//
// https://github.com/ServiceStack/ServiceStack.Redis
// ServiceStack.Redis: ECMA CLI Binding to the Redis key-value storage system
//
// Authors:
//   Demis Bellot (demis.bellot@gmail.com)
//
// Copyright 2017 ServiceStack, Inc. All Rights Reserved.
//
// Licensed under the same terms of ServiceStack.
//

using ServiceStack.Caching;
using System;

namespace ServiceStack.Redis
{
    public interface IRedisClientsManager : IDisposable
    {
        /// <summary>
        /// Resolver strategy for resolving hosts and creating clients
        /// </summary>
        IRedisResolver RedisResolver { get; }

        /// <summary>
        /// Returns a Read/Write client (The default) using the hosts defined in ReadWriteHosts
        /// </summary>
        /// <returns></returns>
        IRedisClient GetClient();

        /// <summary>
        /// Returns a ReadOnly client using the hosts defined in ReadOnlyHosts.
        /// </summary>
        /// <returns></returns>
        IRedisClient GetReadOnlyClient();

        /// <summary>
        /// Returns a Read/Write ICacheClient (The default) using the hosts defined in ReadWriteHosts
        /// </summary>
        /// <returns></returns>
        ICacheClient GetCacheClient();

        /// <summary>
        /// Returns a ReadOnly ICacheClient using the hosts defined in ReadOnlyHosts.
        /// </summary>
        /// <returns></returns>
        ICacheClient GetReadOnlyCacheClient();
    }
}