﻿#region Copyright 
// Copyright 2017 Gigya Inc.  All rights reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License"); 
// you may not use this file except in compliance with the License.  
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDER AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED.  IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.
#endregion

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Gigya.Microdot.Interfaces.Logging;

// ReSharper disable InconsistentlySynchronizedField

namespace Gigya.Microdot.ServiceProxy.Caching
{
    public class ReverseItem
    {
        public HashSet<string> CacheKeysSet = new HashSet<string>();
        public DateTime WhenRevoked = DateTime.MinValue;
    }

    public interface IRevokeQueueMaintainer : IDisposable
    {
        int QueueCount { get; }

        void Enqueue(string revokeKey, DateTime now);

        void Maintain(TimeSpan olderThan);
    }

    /// <summary>
    /// Cleaning periodically the reverse index and revokes queue.
    /// </summary>
    public class RevokeQueueMaintainer : IRevokeQueueMaintainer
    {
        private readonly Timer _timer;
        private readonly ConcurrentQueue<Tuple<string, DateTime>> _revokesQueue;
        private  readonly ConcurrentDictionary<string, ReverseItem> _reverseIndex;

        public int QueueCount => _revokesQueue.Count;

        public RevokeQueueMaintainer(ConcurrentDictionary<string, ReverseItem> reverseIndex, ILog log, Func<CacheConfig> getRevokeConfig)
        {
            _reverseIndex = reverseIndex;
            _revokesQueue = new ConcurrentQueue<Tuple<string, DateTime>>();

            _timer = new Timer(_ =>
            {
                var intervalMs = getRevokeConfig().RevokesCleanupMs;
                try
                {
                    Maintain(TimeSpan.FromMilliseconds(intervalMs));
                }
                catch (Exception ex)
                {
                    log.Critical(x => x("Programmatic error", exception: ex));
                }
                finally{
                    try{
                        _timer.Change(intervalMs, Timeout.Infinite);
                    }
                    catch (ObjectDisposedException){}
                }
            });

            _timer.Change(0, Timeout.Infinite);
        }

        public void Enqueue(string revokeKey, DateTime now)
        {
            _revokesQueue.Enqueue(new Tuple<string, DateTime>(revokeKey, now));
        }

        public void Maintain(TimeSpan olderThan)
        {
            var cutOffTime = DateTime.UtcNow - olderThan;

            while (_revokesQueue.TryPeek(out var revoke)) // Empty queue
            {
                var whenRevoked = revoke.Item2;
                var revokeKey = revoke.Item1;

                // All younger
                if (whenRevoked > cutOffTime)
                    break;

                _revokesQueue.TryDequeue(out _);

                // "Empty" keys and older than interval.
                // We compete on possible call, adding the value to cache, exactly when timer fired...
                if (_reverseIndex.TryGetValue(revokeKey, out var reverseItem))
                    if (!reverseItem.CacheKeysSet.Any())
                        lock (reverseItem.CacheKeysSet)
                            if (!reverseItem.CacheKeysSet.Any())
                                _reverseIndex.TryRemove(revokeKey, out _);
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
