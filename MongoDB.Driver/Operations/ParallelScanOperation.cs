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

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver.Internal;
using System.Threading.Tasks;

namespace MongoDB.Driver.Operations
{
    internal class ParallelScanOperation<TDocument>
    {
        // private fields
        private readonly int? _batchSize;
        private readonly string _collectionName;
        private readonly string _databaseName;
        private readonly int _numberOfCursors;
        private readonly BsonBinaryReaderSettings _readerSettings;
        private readonly ReadPreference _readPreference;
        private readonly IBsonSerializationOptions _serializationOptions;
        private readonly IBsonSerializer _serializer;
        private readonly BsonBinaryWriterSettings _writerSettings;

        // constructors
        public ParallelScanOperation(
            string databaseName,
            string collectionName,
            int numberOfCursors,
            int? batchSize,
            IBsonSerializer serializer,
            IBsonSerializationOptions serializationOptions,
            ReadPreference readPreference,
            BsonBinaryReaderSettings readerSettings,
            BsonBinaryWriterSettings writerSettings)
        {
            _databaseName = databaseName;
            _collectionName = collectionName;
            _numberOfCursors = numberOfCursors;
            _batchSize = batchSize;
            _serializer = serializer;
            _serializationOptions = serializationOptions;
            _readPreference = readPreference;
            _readerSettings = readerSettings;
            _writerSettings = writerSettings;
        }

        // public methods
        public async Task<ReadOnlyCollection<IEnumerator<TDocument>>> ExecuteAsync(MongoConnection connection)
        {
            var command = new CommandDocument
            {
                { "parallelCollectionScan", _collectionName },
                { "numCursors", _numberOfCursors }
            };
            var options = new BsonDocument();
            var commandResultSerializer = BsonSerializer.LookupSerializer(typeof(CommandResult));

            var operation = new CommandOperation<CommandResult>(
                _databaseName,
                _readerSettings,
                _writerSettings,
                command,
                QueryFlags.None,
                options,
                _readPreference,
                null, // serializationOptions
                commandResultSerializer);

            var result = await operation.ExecuteAsync(connection).ConfigureAwait(false);
            var response = result.Response;

            var connectionProvider = new ServerInstanceConnectionProvider(result.ServerInstance);
            var collectionFullName = _databaseName + "." + _collectionName;

            var enumerators = new List<IEnumerator<TDocument>>();
            foreach (BsonDocument cursorsArrayItem in response["cursors"].AsBsonArray)
            {
                var cursor = cursorsArrayItem["cursor"].AsBsonDocument;

                var firstBatch = cursor["firstBatch"].AsBsonArray.Select(v =>
                {
                    using (var reader = BsonReader.Create(v.AsBsonDocument))
                    {
                        return (TDocument)_serializer.Deserialize(reader, typeof(TDocument), _serializationOptions);
                    }
                });
                var cursorId = cursor["id"].ToInt64();

                var enumerator = new CursorEnumerator<TDocument>(
                    connectionProvider,
                    collectionFullName,
                    firstBatch,
                    cursorId,
                    _batchSize ?? 0,
                    0, // limit
                    _readerSettings,
                    _serializer,
                    _serializationOptions);
                enumerators.Add(enumerator);
            }

            return enumerators.AsReadOnly();
        }
    }
}
