/* Copyright 2010-2014 MongoDB Inc.
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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MongoDB.Driver.Internal
{
    /// <summary>
    /// Connects directly to a specified instance, failing over to other addresses as necessary.
    /// </summary>
    internal sealed class DirectMongoServerProxy : IMongoServerProxy
    {
        // private fields

        private SemaphoreSlim _semaphore = new SemaphoreSlim(1);        
        private readonly MongoServerSettings _settings;
        private readonly MongoServerInstance _instance;
        private int _connectionAttempt;

        // constructors
        /// <summary>
        /// Initializes a new instance of the <see cref="DirectMongoServerProxy"/> class.
        /// </summary>
        /// <param name="settings">The settings.</param>
        public DirectMongoServerProxy(MongoServerSettings settings)
        {
            _settings = settings;
            _instance = new MongoServerInstance(settings, settings.Servers.First());
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DirectMongoServerProxy"/> class.
        /// </summary>
        /// <param name="serverSettings">The server settings.</param>
        /// <param name="instance">The instance.</param>
        /// <param name="connectionAttempt">The connection attempt.</param>
        public DirectMongoServerProxy(MongoServerSettings serverSettings, MongoServerInstance instance, int connectionAttempt)
        {
            _settings = serverSettings;
            _instance = instance;
            _connectionAttempt = connectionAttempt;
        }

        // public properties
        /// <summary>
        /// Gets the build info.
        /// </summary>
        public MongoServerBuildInfo BuildInfo
        {
            get { return _instance.BuildInfo; }
        }

        /// <summary>
        /// Gets the connection attempt.
        /// </summary>
        public int ConnectionAttempt
        {
            get 
            {
                //lock (_stateLock)
                _semaphore.Wait();
                try
                {
                    return _connectionAttempt;
                }
                finally
                {
                    _semaphore.Release();
                }
            }
        }

        /// <summary>
        /// Gets the instances.
        /// </summary>
        public ReadOnlyCollection<MongoServerInstance> Instances
        {
            get { return new List<MongoServerInstance> { _instance }.AsReadOnly(); }
        }

        /// <summary>
        /// Gets the type of the proxy.
        /// </summary>
        public MongoServerProxyType ProxyType
        {
            get { return MongoServerProxyType.Direct; }
        }

        /// <summary>
        /// Gets the state.
        /// </summary>
        public MongoServerState State
        {
            get 
            {
                //lock (_stateLock)

                _semaphore.Wait();
                try
                {
                    return _instance.State;
                }
                finally
                {
                    _semaphore.Release();
                }
            }
        }

        // public methods
        /// <summary>
        /// Chooses the server instance.
        /// </summary>
        /// <param name="readPreference">The read preference.</param>
        /// <returns>A MongoServerInstance.</returns>
        public async Task<MongoServerInstance> ChooseServerInstanceAsync(ReadPreference readPreference)
        {
            if (_instance.State != MongoServerState.Connected)
            {
                // Synchronization is not needed in this method                
                await ConnectAsync(_settings.ConnectTimeout, readPreference);                
            }

            if (_instance.State == MongoServerState.Connected)
            {
                if (!_instance.IsCompatible)
                {
                    var message = "This version of the driver is not compatible with the server.";
                    throw new MongoConnectionException(message);
                }

                return _instance;
            }

            throw new MongoConnectionException(string.Format("Unable to connect to the server {0}.", _instance.Address));
        }

        /// <summary>
        /// Connects to the server respecting the timeout and readPreference.
        /// </summary>
        /// <param name="timeout">The timeout.</param>
        /// <param name="readPreference">The read preference.</param>
        public async Task ConnectAsync(TimeSpan timeout, ReadPreference readPreference)
        {
            if (_instance.State != MongoServerState.Connected)
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                //lock (_stateLock)
                try
                {
                    if (_instance.State != MongoServerState.Connected)
                    {
                        _connectionAttempt++;
                        var exceptions = new List<Exception>();
                        foreach (var address in _settings.Servers)
                        {
                            try
                            {
                                _instance.Address = address;

                                // TODO: what about timeout? -> var cts = new CancellationTokenSource(timeout); Need a cancelation token
                                await _instance.ConnectAsync().ConfigureAwait(false); 

                                if (_settings.ReplicaSetName != null &&
                                    (_instance.InstanceType != MongoServerInstanceType.ReplicaSetMember || _instance.ReplicaSetInformation.Name != _settings.ReplicaSetName))
                                {
                                    exceptions.Add(new MongoConnectionException(string.Format("The server '{0}' is not a member of replica set '{1}'.", address, _settings.ReplicaSetName)));
                                    _instance.Disconnect();
                                    continue;
                                }

                                if (_instance.IsMasterResult.MyAddress != null)
                                {
                                    _instance.Address = _instance.IsMasterResult.MyAddress;
                                }
                                return;
                            }
                            catch (Exception ex)
                            {
                                exceptions.Add(ex);
                            }
                        }

                        var firstAddress = _settings.Servers.First();
                        var firstException = exceptions.First();
                        var message = string.Format("Unable to connect to server {0}: {1}.", firstAddress, firstException.Message);
                        var connectionException = new MongoConnectionException(message, firstException);
                        connectionException.Data.Add("InnerExceptions", exceptions); // useful when there is more than one
                        throw connectionException;
                    }
                }
                finally
                {
                    _semaphore.Release();
                }
            }
        }

        /// <summary>
        /// Disconnects from the server.
        /// </summary>
        public void Disconnect()
        {
            _semaphore.Wait();
            //lock (_stateLock)
            try
            {
                _instance.Disconnect();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        /// <summary>
        /// Pings the server.
        /// </summary>
        public Task PingAsync()
        {
            return _instance.PingAsync();
        }

        /// <summary>
        /// Verifies the state of the server.
        /// </summary>
        public async Task VerifyStateAsync()
        {
            var state = _instance.State;
            // if we are disconnected or disconnecting, then our state is correct...
            if (state == MongoServerState.Disconnected || state == MongoServerState.Disconnecting)
            {
                return;
            }

            await _semaphore.WaitAsync();
            //lock (_stateLock)
            try
            {                
                await _instance.VerifyStateAsync();
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }   



}


