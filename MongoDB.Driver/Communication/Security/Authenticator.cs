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
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver.Communication.Security.Mechanisms;
using MongoDB.Driver.Internal;
using MongoDB.Driver.Operations;
using System.Threading.Tasks;

namespace MongoDB.Driver.Communication.Security
{
    /// <summary>
    /// Authenticates credentials against MongoDB.
    /// </summary>
    internal class Authenticator
    {
        // private static fields
        private static readonly List<IAuthenticationProtocol> __clientSupportedProtocols = new List<IAuthenticationProtocol>
        {
            // when we start negotiating, MONGODB-CR should be moved to the bottom of the list...
            new MongoCRAuthenticationProtocol(),
            new X509AuthenticationProtocol(),
            new SaslAuthenticationProtocol(new GssapiMechanism()),
            new SaslAuthenticationProtocol(new PlainMechanism())
        };

        // private fields
        private readonly MongoConnection _connection;
        private readonly IEnumerable<MongoCredential> _credentials;

        // constructors
        /// <summary>
        /// Initializes a new instance of the <see cref="Authenticator" /> class.
        /// </summary>
        /// <param name="connection">The connection.</param>
        /// <param name="credentials">The credentials.</param>
        public Authenticator(MongoConnection connection, IEnumerable<MongoCredential> credentials)
        {
            _connection = connection;
            _credentials = credentials;
        }

        // public methods
        /// <summary>
        /// Authenticates the specified connection.
        /// </summary>
        public async Task AuthenticateAsync()
        {
            if (!_credentials.Any())
            {
                return;
            }

            if (!await IsArbiterAsync().ConfigureAwait(false))
            {
                foreach (var credential in _credentials)
                {
                    await AuthenticateAsync(credential).ConfigureAwait(false);
                }
            }
        }

        // private methods
        private async Task AuthenticateAsync(MongoCredential credential)
        {
            foreach (var clientSupportedProtocol in __clientSupportedProtocols)
            {
                if (clientSupportedProtocol.CanUse(credential))
                {
                    await clientSupportedProtocol.AuthenticateAsync(_connection, credential).ConfigureAwait(false);
                    return;
                }
            }

            var message = string.Format("Unable to find a protocol to authenticate. The credential for source {0}, username {1} over mechanism {2} could not be authenticated.", credential.Source, credential.Username, credential.Mechanism);
            throw new MongoSecurityException(message);
        }

        private async Task<bool> IsArbiterAsync()
        {
            var command = new CommandDocument("isMaster", true);
            var result = await RunCommandAsync(_connection, "admin", command).ConfigureAwait(false);
            return result.Response.GetValue("arbiterOnly", false).ToBoolean();
        }

        private Task<CommandResult> RunCommandAsync(MongoConnection connection, string databaseName, IMongoCommand command)
        {
            var readerSettings = new BsonBinaryReaderSettings();
            var writerSettings = new BsonBinaryWriterSettings();
            var resultSerializer = BsonSerializer.LookupSerializer(typeof(CommandResult));

            var commandOperation = new CommandOperation<CommandResult>(
                databaseName,
                readerSettings,
                writerSettings,
                command,
                QueryFlags.SlaveOk,
                null, // options
                null, // readPreference
                null, // serializationOptions
                resultSerializer);

            return commandOperation.ExecuteAsync(connection);
        }
    }
}
