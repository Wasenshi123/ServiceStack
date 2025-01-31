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

using ServiceStack.Model;
using System.Collections.Generic;

namespace ServiceStack.Redis.Generic
{
    public interface IRedisSet<T> : ICollection<T>, IHasStringId
    {
        List<T> Sort(int startingFrom, int endingAt);
        HashSet<T> GetAll();
        T PopRandomItem();
        T GetRandomItem();
        void MoveTo(T item, IRedisSet<T> toSet);
        void PopulateWithIntersectOf(params IRedisSet<T>[] sets);
        void PopulateWithUnionOf(params IRedisSet<T>[] sets);
        void GetDifferences(params IRedisSet<T>[] withSets);
        void PopulateWithDifferencesOf(IRedisSet<T> fromSet, params IRedisSet<T>[] withSets);
    }
}
