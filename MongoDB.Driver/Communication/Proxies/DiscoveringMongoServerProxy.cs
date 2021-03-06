﻿/* Copyright 2010-2014 MongoDB Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MongoDB.Driver.Internal
{
    /// <summary>
    /// A proxy that dynamically discovers the type of server it is connecting to.
    /// </summary>
    internal sealed class DiscoveringMongoServerProxy : IMongoServerProxy
    {
        //private readonly object _lock = new object();
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1);

        private readonly MongoServerSettings _settings;
        private readonly ReadOnlyCollection<MongoServerInstance> _instances;

        // volatile will ensure that our reads are not reordered such one could get placed before a write.  This 
        // isn't strictly required for > .NET 2.0 systems, but Mono does not offer the same memory model guarantees,
        // so we code to the ECMA standard.
        private volatile IMongoServerProxy _serverProxy;
        private volatile MongoServerState _state;
        private volatile int _connectionAttempt;

        // constructors
        /// <summary>
        /// Initializes a new instance of the <see cref="DiscoveringMongoServerProxy"/> class.
        /// </summary>
        /// <param name="settings">The settings.</param>
        public DiscoveringMongoServerProxy(MongoServerSettings settings)
        {
            _state = MongoServerState.Disconnected;
            _settings = settings;
            _instances = settings.Servers.Select(a => new MongoServerInstance(settings, a)).ToList().AsReadOnly();
        }

        // public properties
        /// <summary>
        /// Gets the build info.
        /// </summary>
        public MongoServerBuildInfo BuildInfo
        {
            get
            {
                if (_serverProxy == null)
                {
                    return null;
                }

                return _serverProxy.BuildInfo;
            }
        }

        /// <summary>
        /// Gets the connection attempt.
        /// </summary>
        public int ConnectionAttempt
        {
            get
            {
                if (_serverProxy == null)
                {
                    return _connectionAttempt;
                }

                return _serverProxy.ConnectionAttempt;
            }
        }

        /// <summary>
        /// Gets the instances.
        /// </summary>
        public ReadOnlyCollection<MongoServerInstance> Instances
        {
            get
            {
                if (_serverProxy == null)
                {
                    return _instances;
                }

                return _serverProxy.Instances;
            }
        }

        /// <summary>
        /// Gets the type of the proxy.
        /// </summary>
        public MongoServerProxyType ProxyType
        {
            get
            {
                if (_serverProxy == null)
                {
                    return MongoServerProxyType.Unknown;
                }

                return _serverProxy.ProxyType;
            }
        }

        /// <summary>
        /// Gets the name of the replica set (null if not connected to a replica set).
        /// </summary>
        public string ReplicaSetName
        {
            get
            {
                var replicaSetProxy = _serverProxy as ReplicaSetMongoServerProxy;
                if (replicaSetProxy != null)
                {
                    return replicaSetProxy.ReplicaSetName;
                }

                return null;
            }
        }

        /// <summary>
        /// Gets the state.
        /// </summary>
        public MongoServerState State
        {
            get
            {
                if (_serverProxy == null)
                {
                    return _state;
                }

                return _serverProxy.State;
            }
        }

        // internal properties
        internal IMongoServerProxy WrappedProxy
        {
            get { return _serverProxy; }
        }

        // public methods
        /// <summary>
        /// Chooses the server instance.
        /// </summary>
        /// <param name="readPreference">The read preference.</param>
        /// <returns>A MongoServerInstance</returns>
        public async Task<MongoServerInstance> ChooseServerInstanceAsync(ReadPreference readPreference)
        {
            await EnsureInstanceManagerAsync(_settings.ConnectTimeout).ConfigureAwait(false);
            return await _serverProxy.ChooseServerInstanceAsync(readPreference).ConfigureAwait(false);
        }

        /// <summary>
        /// Connects to the instances respecting the timeout and readPreference.
        /// </summary>
        /// <param name="timeout">The timeout.</param>
        /// <param name="readPreference">The read preference.</param>
        public async Task ConnectAsync(TimeSpan timeout, ReadPreference readPreference)
        {
            try
            {
                await EnsureInstanceManagerAsync(timeout).ConfigureAwait(false);
            }
            catch
            {
                _state = MongoServerState.Disconnected;
                throw;
            }
            await _serverProxy.ConnectAsync(timeout, readPreference).ConfigureAwait(false);
        }

        /// <summary>
        /// Disconnects this instance.
        /// </summary>
        public void Disconnect()
        {
            if (_serverProxy != null)
            {
                _serverProxy.Disconnect();
            }
        }

        /// <summary>
        /// Pings this instance.
        /// </summary>
        public async Task PingAsync()
        {
            if (_serverProxy == null)
            {
                foreach (var instance in _instances)
                {
                    await instance.PingAsync();
                }
            }
            else
            {
                await _serverProxy.PingAsync();
            }
        }

        /// <summary>
        /// Verifies the state.
        /// </summary>
        public Task VerifyStateAsync()
        {
            // if we have never connected, then our state is correct...
            if (_serverProxy == null)
            {
                return Task.FromResult(0);
            }

            return _serverProxy.VerifyStateAsync();
        }

        // private methods
        private async Task EnsureInstanceManagerAsync(TimeSpan timeout)
        {
            if (_serverProxy == null)
            {

                //lock (_lock)
                await _semaphore.WaitAsync();
                try
                {
                    if (_serverProxy == null)
                    {
                        _connectionAttempt++;
                        _state = MongoServerState.Connecting;
                        await DiscoverAsync(timeout);
                    }
                }
                finally
                {
                    _semaphore.Release();
                }
            }
        }

        private Task DiscoverAsync(TimeSpan timeout)
        {
            var connectionQueue = new BlockingQueue<MongoServerInstance>();

            for (int i = 0; i < _instances.Count; i++)
            {
                var local = _instances[i];
                connectionQueue.EnqueuWorkItem(() =>
                {
                    try
                    {
                        local.ConnectAsync().Wait();
                    }
                    catch
                    {
                        // instance is keeping it's last ConnectionException
                    }
                    return local;
                });
            }

            MongoServerInstance instance = null;
            var timeRemaining = timeout;
            var timeoutAt = DateTime.UtcNow + timeout;
            while ((instance = connectionQueue.Dequeue(timeRemaining)) != null)
            {
                if (instance.ConnectException == null)
                {
                    CreateActualProxy(instance, connectionQueue);
                    return Task.FromResult(0);
                }

                timeRemaining = timeoutAt - DateTime.UtcNow;
            }

            throw new MongoConnectionException(string.Format("Unable to connect in the specified timeframe of '{0}'.", timeout));
        }

        private void CreateActualProxy(MongoServerInstance instance, BlockingQueue<MongoServerInstance> connectionQueue)
        {
            //lock (_lock)
            _semaphore.Wait();
            try
            {
                if (instance.InstanceType == MongoServerInstanceType.ReplicaSetMember)
                {
                    _serverProxy = new ReplicaSetMongoServerProxy(_settings, _instances, connectionQueue, _connectionAttempt);
                }
                else if (instance.InstanceType == MongoServerInstanceType.ShardRouter)
                {
                    _serverProxy = new ShardedMongoServerProxy(_settings, _instances, connectionQueue, _connectionAttempt);
                }
                else if (instance.InstanceType == MongoServerInstanceType.StandAlone)
                {
                    var otherInstances = _instances.Where(x => x != instance).ToList();
                    foreach (var otherInstance in otherInstances)
                    {
                        otherInstance.Disconnect();
                    }

                    _serverProxy = new DirectMongoServerProxy(_settings, instance, _connectionAttempt);
                }
                else
                {
                    throw new MongoConnectionException("The type of servers in the host list could not be determined.");
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}