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

namespace ServiceStack.Redis
{
    public interface IRedisSet
        : ICollection<string>, IHasStringId
    {
        List<string> GetRangeFromSortedSet(int startingFrom, int endingAt);
        HashSet<string> GetAll();
        string Pop();
        void Move(string value, IRedisSet toSet);
        HashSet<string> Intersect(params IRedisSet[] withSets);
        void StoreIntersect(params IRedisSet[] withSets);
        HashSet<string> Union(params IRedisSet[] withSets);
        void StoreUnion(params IRedisSet[] withSets);
        HashSet<string> Diff(IRedisSet[] withSets);
        void StoreDiff(IRedisSet fromSet, params IRedisSet[] withSets);
        string GetRandomEntry();
    }
}