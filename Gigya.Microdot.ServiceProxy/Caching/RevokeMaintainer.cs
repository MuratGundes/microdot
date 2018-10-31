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

        //void Maintain(TimeSpan olderThan);
    }

    // convert to general-purpose time-bound queue; beware of concurrent Maintain() (double-lock around TryPeek)
    // - Enqueue((time, T))
    // - IEnumerable<(time, T)> Dequeue(time)
    /// <summary>
    /// ??
    /// </summary>
    public class RevokeQueueMaintainer : IRevokeQueueMaintainer
    {
        private readonly Timer _timer;
        private readonly ConcurrentQueue<Tuple<string, DateTime>> _revokesQueue;
        private readonly Action<string> _onMaintain;

        public int QueueCount => _revokesQueue.Count;

        public RevokeQueueMaintainer(Action<string> onMaintain, ILog log, Func<CacheConfig> getRevokeConfig)
        {
            _revokesQueue = new ConcurrentQueue<Tuple<string, DateTime>>(); // named tuples?
            _onMaintain = onMaintain;

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

        public void Maintain(TimeSpan olderThan) // , DateTime now
        {
            var cutOffTime = DateTime.UtcNow - olderThan;

            while (_revokesQueue.TryPeek(out var revoke)) // Empty queue
            {
                // if item.time > ...
                //   lock
                //     if (TryPeek() && item.time > ...)
                //       dequeu
                //       yield return
                var whenRevoked = revoke.Item2;
                var revokeKey = revoke.Item1;

                // All younger
                if (whenRevoked > cutOffTime)
                    break;

                _revokesQueue.TryDequeue(out _);

                _onMaintain?.Invoke(revokeKey);
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
