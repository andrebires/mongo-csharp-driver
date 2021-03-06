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
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Options;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver.Builders;
using MongoDB.Driver.GeoJsonObjectModel.Serializers;
using MongoDB.Driver.Internal;
using MongoDB.Driver.Operations;
using MongoDB.Driver.Wrappers;
using System.Threading.Tasks;
using System.Threading;

namespace MongoDB.Driver
{
    /// <summary>
    /// Represents a MongoDB collection and the settings used to access it. This class is thread-safe.
    /// </summary>
    public abstract class MongoCollection
    {
        // private fields
        private MongoServer _server;
        private MongoDatabase _database;
        private MongoCollectionSettings _settings;
        private string _name;

        // constructors
        /// <summary>
        /// Protected constructor for abstract base class.
        /// </summary>
        /// <param name="database">The database that contains this collection.</param>
        /// <param name="name">The name of the collection.</param>
        /// <param name="settings">The settings to use to access this collection.</param>
        protected MongoCollection(MongoDatabase database, string name, MongoCollectionSettings settings)
        {
            if (database == null)
            {
                throw new ArgumentNullException("database");
            }
            if (name == null)
            {
                throw new ArgumentNullException("name");
            }
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }
            string message;
            if (!database.IsCollectionNameValid(name, out message))
            {
                throw new ArgumentOutOfRangeException("name", message);
            }

            settings = settings.Clone();
            settings.ApplyDefaultValues(database.Settings);
            settings.Freeze();

            _server = database.Server;
            _database = database;
            _settings = settings;
            _name = name;
        }

        // public properties
        /// <summary>
        /// Gets the database that contains this collection.
        /// </summary>
        public virtual MongoDatabase Database
        {
            get { return _database; }
        }

        /// <summary>
        /// Gets the fully qualified name of this collection.
        /// </summary>
        public virtual string FullName
        {
            get { return _database.Name + "." + _name; }
        }

        /// <summary>
        /// Gets the name of this collection.
        /// </summary>
        public virtual string Name
        {
            get { return _name; }
        }

        /// <summary>
        /// Gets the settings being used to access this collection.
        /// </summary>
        public virtual MongoCollectionSettings Settings
        {
            get { return _settings; }
        }

        // public methods
        /// <summary>
        /// Represents an aggregate framework query. The command is not sent to the server until the result is enumerated.
        /// </summary>
        /// <param name="args">The args.</param>
        /// <returns>A sequence of documents.</returns>
        public virtual async Task<IEnumerable<BsonDocument>> AggregateAsync(AggregateArgs args)
        {
            if (args == null) { throw new ArgumentNullException("args"); }
            if (args.Pipeline == null) { throw new ArgumentException("Pipeline is null.", "args"); }

            var lastStage = args.Pipeline.LastOrDefault();

            string outputCollectionName = null;
            if (lastStage != null && lastStage.GetElement(0).Name == "$out")
            {
                outputCollectionName = lastStage["$out"].AsString;
                await RunAggregateCommandAsync(args).ConfigureAwait(false);
            }

            return new AggregateEnumerableResult(this, args, outputCollectionName);
        }

        /// <summary>
        /// Runs an aggregation framework command.
        /// </summary>
        /// <param name="pipeline">The pipeline operations.</param>
        /// <returns>
        /// An AggregateResult.
        /// </returns>
        [Obsolete("Use the overload with an AggregateArgs parameter.")]
        public virtual Task<AggregateResult> AggregateAsync(IEnumerable<BsonDocument> pipeline)
        {
            var args = new AggregateArgs { Pipeline = pipeline, OutputMode = AggregateOutputMode.Inline };
            return RunAggregateCommandAsync(args);
        }

        /// <summary>
        /// Runs an aggregation framework command.
        /// </summary>
        /// <param name="pipeline">The pipeline operations.</param>
        /// <returns>An AggregateResult.</returns>
        [Obsolete("Use the overload with an AggregateArgs parameter.")]
        public virtual Task<AggregateResult> AggregateAsync(params BsonDocument[] pipeline)
        {
            var args = new AggregateArgs { Pipeline = pipeline, OutputMode = AggregateOutputMode.Inline };
            return RunAggregateCommandAsync(args);
        }

        /// <summary>
        /// Runs an aggregate command with explain set and returns the explain result.
        /// </summary>
        /// <param name="args">The args.</param>
        /// <returns>The explain result.</returns>
        public virtual Task<CommandResult> AggregateExplainAsync(AggregateArgs args)
        {
            var aggregateCommand = new CommandDocument
            {
                { "aggregate", _name },
                { "pipeline", new BsonArray(args.Pipeline.Cast<BsonValue>()) },
                { "allowDiskUse", () => args.AllowDiskUse.Value, args.AllowDiskUse.HasValue },
                { "explain", true }
            };

            return RunCommandAsAsync<CommandResult>(aggregateCommand);
        }

        /// <summary>
        /// Executes multiple write requests.
        /// </summary>
        /// <param name="args">The args.</param>
        /// <returns>
        /// A BulkWriteResult.
        /// </returns>
        internal virtual async Task<BulkWriteResult> BulkWriteAsync(BulkWriteArgs args)
        {
            if (args == null)
            {
                throw new ArgumentNullException("args");
            }
            if (args.Requests == null)
            {
                throw new ArgumentNullException("args.Requests");
            }

            var connection = await _server.AcquireConnectionAsync(ReadPreference.Primary).ConfigureAwait(false);
            try
            {
                var assignId = args.AssignId ?? (_settings.AssignIdOnInsert ? (Action<InsertRequest>)AssignId : null);
                var checkElementNames = args.CheckElementNames ?? true;
                var maxBatchCount = args.MaxBatchCount ?? connection.ServerInstance.MaxBatchCount;
                var maxBatchLength = Math.Min(args.MaxBatchLength ?? int.MaxValue, connection.ServerInstance.MaxDocumentSize);
                var maxDocumentSize = connection.ServerInstance.MaxDocumentSize;
                var maxWireDocumentSize = connection.ServerInstance.MaxWireDocumentSize;
                var writeConcern = args.WriteConcern ?? _settings.WriteConcern;
                
                var operation = new BulkMixedWriteOperation(
                    assignId,
                    checkElementNames,
                    _name,
                    _database.Name,
                    maxBatchCount,
                    maxBatchLength,
                    maxDocumentSize,
                    maxWireDocumentSize,
                    args.IsOrdered ?? true,
                    GetBinaryReaderSettings(),
                    args.Requests,
                    writeConcern,
                    GetBinaryWriterSettings());

                return await operation.ExecuteAsync(connection).ConfigureAwait(false);
            }
            finally
            {
                _server.ReleaseConnection(connection);
            }
        }

        /// <summary>
        /// Counts the number of documents in this collection.
        /// </summary>
        /// <returns>The number of documents in this collection.</returns>
        public virtual Task<long> CountAsync()
        {
            var args = new CountArgs { Query = null };
            return CountAsync(args);
        }

        /// <summary>
        /// Counts the number of documents in this collection that match a query.
        /// </summary>
        /// <param name="args">The args.</param>
        /// <returns>
        /// The number of documents in this collection that match the query.
        /// </returns>
        public virtual async Task<long> CountAsync(CountArgs args)
        {
            if (args == null) { throw new ArgumentNullException("args"); }

            var command = new CommandDocument
            {
                { "count", _name },
                { "query", () => BsonDocumentWrapper.Create(args.Query), args.Query != null }, // optional
                { "limit", () => args.Limit.Value, args.Limit.HasValue }, // optional
                { "skip", () => args.Skip.Value, args.Skip.HasValue }, // optional
                { "maxTimeMS", () => args.MaxTime.Value.TotalMilliseconds, args.MaxTime.HasValue } //optional
            };
            var result = await RunCommandAsAsync<CommandResult>(command).ConfigureAwait(false);
            return result.Response["n"].ToInt64();
        }

        /// <summary>
        /// Counts the number of documents in this collection that match a query.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <returns>The number of documents in this collection that match the query.</returns>
        public virtual Task<long> CountAsync(IMongoQuery query)
        {
            var args = new CountArgs { Query = query };
            return CountAsync(args);
        }

        /// <summary>
        /// Creates an index for this collection.
        /// </summary>
        /// <param name="keys">The indexed fields (usually an IndexKeysDocument or constructed using the IndexKeys builder).</param>
        /// <param name="options">The index options(usually an IndexOptionsDocument or created using the IndexOption builder).</param>
        /// <returns>A WriteConcernResult.</returns>
        public virtual async Task<WriteConcernResult> CreateIndexAsync(IMongoIndexKeys keys, IMongoIndexOptions options)
        {
            using (_database.RequestStart(ReadPreference.Primary))
            {
                if (_server.RequestConnection.ServerInstance.Supports(FeatureId.CreateIndexCommand))
                {
                    try
                    {
                        await CreateIndexWithCommandAsync(keys, options).ConfigureAwait(false);
                        var fakeResponse = new BsonDocument { { "ok", 1 }, { "n", 0 } };
                        return new WriteConcernResult(fakeResponse);
                    }
                    catch (MongoCommandException ex)
                    {
                        var translatedResult = new WriteConcernResult(ex.CommandResult.Response);
                        translatedResult.Command = ex.CommandResult.Command;
                        throw new WriteConcernException(ex.Message, translatedResult);
                    }
                }
                else
                {
                    return await CreateIndexWithInsertAsync(keys, options).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Creates an index for this collection.
        /// </summary>
        /// <param name="keys">The indexed fields (usually an IndexKeysDocument or constructed using the IndexKeys builder).</param>
        /// <returns>A WriteConcernResult.</returns>
        public virtual Task<WriteConcernResult> CreateIndexAsync(IMongoIndexKeys keys)
        {
            return CreateIndexAsync(keys, IndexOptions.Null);
        }

        /// <summary>
        /// Creates an index for this collection.
        /// </summary>
        /// <param name="keyNames">The names of the indexed fields.</param>
        /// <returns>A WriteConcernResult.</returns>
        public virtual Task<WriteConcernResult> CreateIndexAsync(params string[] keyNames)
        {
            return CreateIndexAsync(IndexKeys.Ascending(keyNames));
        }

        /// <summary>
        /// Returns the distinct values for a given field.
        /// </summary>
        /// <param name="args">The args.</param>
        /// <returns>The distint values of the field.</returns>
        public async Task<IEnumerable<TValue>> DistinctAsync<TValue>(DistinctArgs args)
        {
            if (args == null) { throw new ArgumentNullException("args"); }
            if (args.Key == null) { throw new ArgumentException("Key is null.", "args"); }

            var command = new CommandDocument
            {
                { "distinct", _name },
                { "key", args.Key },
                { "query", () => BsonDocumentWrapper.Create(args.Query), args.Query != null }, // optional
                { "maxTimeMS", () => args.MaxTime.Value.TotalMilliseconds, args.MaxTime.HasValue } // optional
            };
            var valueSerializer = args.ValueSerializer ?? BsonSerializer.LookupSerializer(typeof(TValue));
            var resultSerializer = new DistinctCommandResultSerializer<TValue>(valueSerializer, args.ValueSerializationOptions);
            var result = await RunCommandAsAsync<DistinctCommandResult<TValue>>(command, resultSerializer, null).ConfigureAwait(false);
            return result.Values;
        }

        /// <summary>
        /// Returns the distinct values for a given field.
        /// </summary>
        /// <param name="key">The key of the field.</param>
        /// <returns>The distint values of the field.</returns>
        public virtual Task<IEnumerable<BsonValue>> DistinctAsync(string key)
        {
            return DistinctAsync<BsonValue>(new DistinctArgs
            {
                Key = key,
                ValueSerializer = BsonValueSerializer.Instance
            });
        }

        /// <summary>
        /// Returns the distinct values for a given field for documents that match a query.
        /// </summary>
        /// <param name="key">The key of the field.</param>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <returns>The distint values of the field.</returns>
        public virtual Task<IEnumerable<BsonValue>> DistinctAsync(string key, IMongoQuery query)
        {
            return DistinctAsync<BsonValue>(new DistinctArgs
            {
                Key = key,
                Query = query,
                ValueSerializer = BsonValueSerializer.Instance
            });
        }

        /// <summary>
        /// Returns the distinct values for a given field.
        /// </summary>
        /// <typeparam name="TValue">The type of the value.</typeparam>
        /// <param name="key">The key of the field.</param>
        /// <returns>The distint values of the field.</returns>
        public virtual Task<IEnumerable<TValue>> DistinctAsync<TValue>(string key)
        {
            return DistinctAsync<TValue>(new DistinctArgs
            {
                Key = key
            });
        }

        /// <summary>
        /// Returns the distinct values for a given field for documents that match a query.
        /// </summary>
        /// <typeparam name="TValue">The type of the value.</typeparam>
        /// <param name="key">The key of the field.</param>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <returns>The distint values of the field.</returns>
        public virtual Task<IEnumerable<TValue>> DistinctAsync<TValue>(string key, IMongoQuery query)
        {
            return DistinctAsync<TValue>(new DistinctArgs
            {
                Key = key,
                Query = query
            });
        }

        /// <summary>
        /// Drops this collection.
        /// </summary>
        /// <returns>A CommandResult.</returns>
        public virtual Task<CommandResult> DropAsync()
        {
            return _database.DropCollectionAsync(_name);
        }

        /// <summary>
        /// Drops all indexes on this collection.
        /// </summary>
        /// <returns>A <see cref="CommandResult"/>.</returns>
        public virtual Task<CommandResult> DropAllIndexesAsync()
        {
            return DropIndexByNameAsync("*");
        }

        /// <summary>
        /// Drops an index on this collection.
        /// </summary>
        /// <param name="keys">The indexed fields (usually an IndexKeysDocument or constructed using the IndexKeys builder).</param>
        /// <returns>A <see cref="CommandResult"/>.</returns>
        public virtual Task<CommandResult> DropIndexAsync(IMongoIndexKeys keys)
        {
            string indexName = GetIndexName(keys.ToBsonDocument(), null);
            return DropIndexByNameAsync(indexName);
        }

        /// <summary>
        /// Drops an index on this collection.
        /// </summary>
        /// <param name="keyNames">The names of the indexed fields.</param>
        /// <returns>A <see cref="CommandResult"/>.</returns>
        public virtual Task<CommandResult> DropIndexAsync(params string[] keyNames)
        {
            string indexName = GetIndexName(keyNames);
            return DropIndexByNameAsync(indexName);
        }

        /// <summary>
        /// Drops an index on this collection.
        /// </summary>
        /// <param name="indexName">The name of the index.</param>
        /// <returns>A <see cref="CommandResult"/>.</returns>
        public virtual async Task<CommandResult> DropIndexByNameAsync(string indexName)
        {
            var command = new CommandDocument
            {
                { "deleteIndexes", _name }, // not FullName
                { "index", indexName }
            };
            try
            {
                return await RunCommandAsAsync<CommandResult>(command).ConfigureAwait(false);
            }
            catch (MongoCommandException ex)
            {
                if (ex.CommandResult.ErrorMessage == "ns not found")
                {
                    return ex.CommandResult;
                }
                throw;
            }
        }

        /// <summary>
        /// Ensures that the desired index exists and creates it if it does not.
        /// </summary>
        /// <param name="keys">The indexed fields (usually an IndexKeysDocument or constructed using the IndexKeys builder).</param>
        /// <param name="options">The index options(usually an IndexOptionsDocument or created using the IndexOption builder).</param>
        [Obsolete("Use CreateIndex instead.")]
        public virtual Task EnsureIndexAsync(IMongoIndexKeys keys, IMongoIndexOptions options)
        {
            return CreateIndexAsync(keys, options);
        }

        /// <summary>
        /// Ensures that the desired index exists and creates it if it does not.
        /// </summary>
        /// <param name="keys">The indexed fields (usually an IndexKeysDocument or constructed using the IndexKeys builder).</param>
        [Obsolete("Use CreateIndex instead.")]
        public virtual Task EnsureIndexAsync(IMongoIndexKeys keys)
        {
            return CreateIndexAsync(keys);
        }

        /// <summary>
        /// Ensures that the desired index exists and creates it if it does not.
        /// </summary>
        /// <param name="keyNames">The names of the indexed fields.</param>
        [Obsolete("Use CreateIndex instead.")]
        public virtual Task EnsureIndexAsync(params string[] keyNames)
        {
            return CreateIndexAsync(keyNames);
        }

        /// <summary>
        /// Tests whether this collection exists.
        /// </summary>
        /// <returns>True if this collection exists.</returns>
        public virtual Task<bool> ExistsAsync()
        {
            return _database.CollectionExistsAsync(_name);
        }

        /// <summary>
        /// Returns a cursor that can be used to find all documents in this collection as TDocuments.
        /// </summary>
        /// <typeparam name="TDocument">The nominal type of the documents.</typeparam>
        /// <returns>A <see cref="MongoCursor{TDocument}"/>.</returns>
        public virtual MongoCursor<TDocument> FindAllAs<TDocument>()
        {
            return FindAs<TDocument>(Query.Null);
        }

        /// <summary>
        /// Returns a cursor that can be used to find all documents in this collection as TDocuments.
        /// </summary>
        /// <param name="documentType">The nominal type of the documents.</param>
        /// <returns>A <see cref="MongoCursor{TDocument}"/>.</returns>
        public virtual MongoCursor FindAllAs(Type documentType)
        {
            return FindAs(documentType, Query.Null);
        }

        /// <summary>
        /// Finds one matching document using the query and sortBy parameters and applies the specified update to it.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="sortBy">The sort order to select one of the matching documents.</param>
        /// <param name="update">The update to apply to the matching document.</param>
        /// <returns>A <see cref="FindAndModifyResult"/>.</returns>
        [Obsolete("Use the overload of FindAndModify that has a FindAndModifyArgs parameter instead.")]
        public virtual Task<FindAndModifyResult> FindAndModifyAsync(IMongoQuery query, IMongoSortBy sortBy, IMongoUpdate update)
        {
            var args = new FindAndModifyArgs { Query = query, SortBy = sortBy, Update = update };
            return FindAndModifyAsync(args);
        }

        /// <summary>
        /// Finds one matching document using the query and sortBy parameters and applies the specified update to it.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="sortBy">The sort order to select one of the matching documents.</param>
        /// <param name="update">The update to apply to the matching document.</param>
        /// <param name="returnNew">Whether to return the new or old version of the modified document in the <see cref="FindAndModifyResult"/>.</param>
        /// <returns>A <see cref="FindAndModifyResult"/>.</returns>
        [Obsolete("Use the overload of FindAndModify that has a FindAndModifyArgs parameter instead.")]
        public virtual Task<FindAndModifyResult> FindAndModifyAsync(
            IMongoQuery query,
            IMongoSortBy sortBy,
            IMongoUpdate update,
            bool returnNew)
        {
            var versionReturned = returnNew ? FindAndModifyDocumentVersion.Modified : FindAndModifyDocumentVersion.Original;
            var args = new FindAndModifyArgs { Query = query, SortBy = sortBy, Update = update, VersionReturned = versionReturned };
            return FindAndModifyAsync(args);
        }

        /// <summary>
        /// Finds one matching document using the query and sortBy parameters and applies the specified update to it.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="sortBy">The sort order to select one of the matching documents.</param>
        /// <param name="update">The update to apply to the matching document.</param>
        /// <param name="returnNew">Whether to return the new or old version of the modified document in the <see cref="FindAndModifyResult"/>.</param>
        /// <param name="upsert">Whether to do an upsert if no matching document is found.</param>
        /// <returns>A <see cref="FindAndModifyResult"/>.</returns>
        [Obsolete("Use the overload of FindAndModify that has a FindAndModifyArgs parameter instead.")]
        public virtual Task<FindAndModifyResult> FindAndModifyAsync(
            IMongoQuery query,
            IMongoSortBy sortBy,
            IMongoUpdate update,
            bool returnNew,
            bool upsert)
        {
            var versionReturned = returnNew ? FindAndModifyDocumentVersion.Modified : FindAndModifyDocumentVersion.Original;
            var args = new FindAndModifyArgs { Query = query, SortBy = sortBy, Update = update, VersionReturned = versionReturned, Upsert = upsert };
            return FindAndModifyAsync(args);
        }

        /// <summary>
        /// Finds one matching document using the query and sortBy parameters and applies the specified update to it.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="sortBy">The sort order to select one of the matching documents.</param>
        /// <param name="update">The update to apply to the matching document.</param>
        /// <param name="fields">Which fields of the modified document to return in the <see cref="FindAndModifyResult"/>.</param>
        /// <param name="returnNew">Whether to return the new or old version of the modified document in the <see cref="FindAndModifyResult"/>.</param>
        /// <param name="upsert">Whether to do an upsert if no matching document is found.</param>
        /// <returns>A <see cref="FindAndModifyResult"/>.</returns>
        [Obsolete("Use the overload of FindAndModify that has a FindAndModifyArgs parameter instead.")]
        public virtual Task<FindAndModifyResult> FindAndModifyAsync(
            IMongoQuery query,
            IMongoSortBy sortBy,
            IMongoUpdate update,
            IMongoFields fields,
            bool returnNew,
            bool upsert)
        {
            var versionReturned = returnNew ? FindAndModifyDocumentVersion.Modified : FindAndModifyDocumentVersion.Original;
            var args = new FindAndModifyArgs { Query = query, SortBy = sortBy, Update = update, Fields = fields, VersionReturned = versionReturned, Upsert = upsert };
            return FindAndModifyAsync(args);
        }

        /// <summary>
        /// Finds one matching document using the supplied arguments and applies the specified update to it.
        /// </summary>
        /// <param name="args">The args.</param>
        /// <returns>A <see cref="FindAndModifyResult"/>.</returns>
        public virtual async Task<FindAndModifyResult> FindAndModifyAsync(FindAndModifyArgs args)
        {
            if (args == null) { throw new ArgumentNullException("args"); }
            if (args.Update == null) { throw new ArgumentException("Update is null.", "args"); }

            var command = new CommandDocument
            {
                { "findAndModify", _name },
                { "query", () => BsonDocumentWrapper.Create(args.Query), args.Query != null }, // optional
                { "sort", () => BsonDocumentWrapper.Create(args.SortBy), args.SortBy != null }, // optional
                { "update", BsonDocumentWrapper.Create(args.Update, true) }, // isUpdateDocument = true
                { "new", () => args.VersionReturned.Value == FindAndModifyDocumentVersion.Modified, args.VersionReturned.HasValue }, // optional
                { "fields", () => BsonDocumentWrapper.Create(args.Fields), args.Fields != null }, // optional
                { "upsert", true, args.Upsert}, // optional
                { "maxTimeMS", () => args.MaxTime.Value.TotalMilliseconds, args.MaxTime.HasValue } // optional
            };
            try
            {
                return await RunCommandAsAsync<FindAndModifyResult>(command).ConfigureAwait(false);
            }
            catch (MongoCommandException ex)
            {
                if (ex.CommandResult.ErrorMessage == "No matching object found")
                {
                    // create a new command result with what the server should have responded
                    var response = new BsonDocument
                    {
                        { "value", BsonNull.Value },
                        { "ok", true }
                    };
                    return new FindAndModifyResult(response) { Command = command };
                }
                throw;
            }
        }

        /// <summary>
        /// Finds one matching document using the query and sortBy parameters and removes it from this collection.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="sortBy">The sort order to select one of the matching documents.</param>
        /// <returns>A <see cref="FindAndModifyResult"/>.</returns>
        [Obsolete("Use the overload of FindAndRemove that has a FindAndRemoveArgs parameter instead.")]
        public virtual Task<FindAndModifyResult> FindAndRemoveAsync(IMongoQuery query, IMongoSortBy sortBy)
        {
            var args = new FindAndRemoveArgs { Query = query, SortBy = sortBy };
            return FindAndRemoveAsync(args);
        }

        /// <summary>
        /// Finds one matching document using the supplied args and removes it from this collection.
        /// </summary>
        /// <param name="args">The args.</param>
        /// <returns>A <see cref="FindAndModifyResult"/>.</returns>
        public virtual async Task<FindAndModifyResult> FindAndRemoveAsync(FindAndRemoveArgs args)
        {
            if (args == null) { throw new ArgumentNullException("args"); }
            
            var command = new CommandDocument
            {
                { "findAndModify", _name },
                { "query", () => BsonDocumentWrapper.Create(args.Query), args.Query != null }, // optional
                { "sort", () => BsonDocumentWrapper.Create(args.SortBy), args.SortBy != null }, // optional
                { "remove", true },
                { "fields", () => BsonDocumentWrapper.Create(args.Fields), args.Fields != null }, // optional
                { "maxTimeMS", () => args.MaxTime.Value.TotalMilliseconds, args.MaxTime.HasValue } // optional
            };
            try
            {
                return await RunCommandAsAsync<FindAndModifyResult>(command).ConfigureAwait(false);
            }
            catch (MongoCommandException ex)
            {
                if (ex.CommandResult.ErrorMessage == "No matching object found")
                {
                    // create a new command result with what the server should have responded
                    var response = new BsonDocument
                    {
                        { "value", BsonNull.Value },
                        { "ok", true }
                    };
                    return new FindAndModifyResult(response) { Command = command };
                }
                throw;
            }
        }

        /// <summary>
        /// Returns a cursor that can be used to find all documents in this collection that match the query as TDocuments.
        /// </summary>
        /// <typeparam name="TDocument">The type to deserialize the documents as.</typeparam>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <returns>A <see cref="MongoCursor{TDocument}"/>.</returns>
        public virtual MongoCursor<TDocument> FindAs<TDocument>(IMongoQuery query)
        {
            var serializer = BsonSerializer.LookupSerializer(typeof(TDocument));
            return FindAs<TDocument>(query, serializer, null);
        }

        /// <summary>
        /// Returns a cursor that can be used to find all documents in this collection that match the query as TDocuments.
        /// </summary>
        /// <param name="documentType">The nominal type of the documents.</param>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <returns>A <see cref="MongoCursor{TDocument}"/>.</returns>
        public virtual MongoCursor FindAs(Type documentType, IMongoQuery query)
        {
            var serializer = BsonSerializer.LookupSerializer(documentType);
            return FindAs(documentType, query, serializer, null);
        }

        /// <summary>
        /// Returns one document in this collection as a TDocument.
        /// </summary>
        /// <typeparam name="TDocument">The type to deserialize the documents as.</typeparam>
        /// <returns>A TDocument (or null if not found).</returns>
        public virtual Task<TDocument> FindOneAsAsync<TDocument>()
        {
            var args = new FindOneArgs { Query = null };
            return FindOneAsAsync<TDocument>(args);
        }

        /// <summary>
        /// Returns one document in this collection as a TDocument.
        /// </summary>
        /// <typeparam name="TDocument">The type of the document.</typeparam>
        /// <param name="args">The args.</param>
        /// <returns>A TDocument (or null if not found).</returns>
        public virtual Task<TDocument> FindOneAsAsync<TDocument>(FindOneArgs args)
        {
            if (args == null) { throw new ArgumentNullException("args"); }

            var query = args.Query ?? new QueryDocument();
            var readPreference = args.ReadPreference ?? _settings.ReadPreference;
            var serializer = args.Serializer ?? BsonSerializer.LookupSerializer(typeof(TDocument));
            var cursor = new MongoCursor<TDocument>(this, query, readPreference, serializer, args.SerializationOptions);
            if (args.Fields != null)
            {
                cursor.SetFields(args.Fields);
            }
            if (args.Hint != null)
            {
                cursor.SetHint(args.Hint);
            }
            if (args.Skip.HasValue)
            {
                cursor.SetSkip(args.Skip.Value);
            }
            if (args.SortBy != null)
            {
                cursor.SetSortOrder(args.SortBy);
            }
            if (args.MaxTime.HasValue)
            {
                cursor.SetMaxTime(args.MaxTime.Value);
            }
            cursor.SetLimit(-1);
            return cursor.FirstOrDefaultAsync();
        }

        /// <summary>
        /// Returns one document in this collection that matches a query as a TDocument.
        /// </summary>
        /// <typeparam name="TDocument">The type to deserialize the documents as.</typeparam>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <returns>A TDocument (or null if not found).</returns>
        public virtual Task<TDocument> FindOneAsAsync<TDocument>(IMongoQuery query)
        {
            var args = new FindOneArgs { Query = query };
            return FindOneAsAsync<TDocument>(args);
        }

        /// <summary>
        /// Returns one document in this collection as a TDocument.
        /// </summary>
        /// <param name="documentType">The nominal type of the documents.</param>
        /// <returns>A document (or null if not found).</returns>
        public virtual object FindOneAs(Type documentType)
        {
            var args = new FindOneArgs { Query = null };
            return FindOneAs(documentType, args);
        }

        /// <summary>
        /// Returns one document in this collection as a TDocument.
        /// </summary>
        /// <param name="documentType">The nominal type of the documents.</param>
        /// <param name="args">The args.</param>
        /// <returns>A document (or null if not found).</returns>
        public virtual object FindOneAs(Type documentType, FindOneArgs args)
        {
            var methodDefinition = GetType().GetMethod("FindOneAs", new Type[] { typeof(FindOneArgs) });
            var methodInfo = methodDefinition.MakeGenericMethod(documentType);
            try
            {
                return methodInfo.Invoke(this, new object[] { args });
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException;
            }
        }

        /// <summary>
        /// Returns one document in this collection that matches a query as a TDocument.
        /// </summary>
        /// <param name="documentType">The type to deserialize the documents as.</param>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <returns>A TDocument (or null if not found).</returns>
        public virtual object FindOneAs(Type documentType, IMongoQuery query)
        {
            var args = new FindOneArgs { Query = query };
            return FindOneAs(documentType, args);
        }

        /// <summary>
        /// Returns a cursor that can be used to find one document in this collection by its _id value as a TDocument.
        /// </summary>
        /// <typeparam name="TDocument">The nominal type of the document.</typeparam>
        /// <param name="id">The id of the document.</param>
        /// <returns>A TDocument (or null if not found).</returns>
        public virtual Task<TDocument> FindOneByIdAsAsync<TDocument>(BsonValue id)
        {
            return FindOneAsAsync<TDocument>(Query.EQ("_id", id));
        }

        /// <summary>
        /// Returns a cursor that can be used to find one document in this collection by its _id value as a TDocument.
        /// </summary>
        /// <param name="documentType">The nominal type of the document.</param>
        /// <param name="id">The id of the document.</param>
        /// <returns>A TDocument (or null if not found).</returns>
        public virtual object FindOneByIdAs(Type documentType, BsonValue id)
        {
            return FindOneAs(documentType, Query.EQ("_id", id));
        }

        /// <summary>
        /// Runs a geoHaystack search command on this collection.
        /// </summary>
        /// <typeparam name="TDocument">The type of the found documents.</typeparam>
        /// <param name="x">The x coordinate of the starting location.</param>
        /// <param name="y">The y coordinate of the starting location.</param>
        /// <param name="options">The options for the geoHaystack search (null if none).</param>
        /// <returns>A <see cref="GeoNearResult{TDocument}"/>.</returns>
        [Obsolete("Use the overload of GeoHaystackSearchAs that has a GeoHaystackSearchArgs parameter instead.")]
        public virtual Task<GeoHaystackSearchResult<TDocument>> GeoHaystackSearchAsAsync<TDocument>(
            double x,
            double y,
            IMongoGeoHaystackSearchOptions options)
        {
            var optionsDocument = options.ToBsonDocument();
            var args = new GeoHaystackSearchArgs
            {
                Near = new XYPoint(x, y),
                MaxDistance = optionsDocument.Contains("maxDistance") ? (int?)optionsDocument["maxDistance"].ToInt32() : null,
                Limit = optionsDocument.Contains("limit") ? (int?)optionsDocument["limit"].ToInt32() : null
            };
            if (optionsDocument.Contains("search"))
            {
                var searchElement = optionsDocument["search"].AsBsonDocument.GetElement(0);
                args.AdditionalFieldName = searchElement.Name;
                args.AdditionalFieldValue = searchElement.Value;
            }
            return GeoHaystackSearchAsAsync<TDocument>(args);
        }

        /// <summary>
        /// Runs a geoHaystack search command on this collection.
        /// </summary>
        /// <typeparam name="TDocument">The type of the found documents.</typeparam>
        /// <param name="args">The args.</param>
        /// <returns>A <see cref="GeoNearResult{TDocument}"/>.</returns>
        public virtual Task<GeoHaystackSearchResult<TDocument>> GeoHaystackSearchAsAsync<TDocument>(GeoHaystackSearchArgs args)
        {
            if (args == null) { throw new ArgumentNullException("args"); }
            if (args.Near == null) { throw new ArgumentException("Near is null.", "args"); }

            BsonDocument search = null;
            if (args.AdditionalFieldName != null && args.AdditionalFieldValue != null)
            {
                search = new BsonDocument(args.AdditionalFieldName, args.AdditionalFieldValue);
            }

            var command = new CommandDocument
            {
                { "geoSearch", _name },
                { "near", new BsonArray { args.Near.X, args.Near.Y } },
                { "maxDistance", () => args.MaxDistance.Value, args.MaxDistance.HasValue }, // optional
                { "search", search, search != null }, // optional
                { "limit", () => args.Limit.Value, args.Limit.HasValue }, // optional
                { "maxTimeMS", () => args.MaxTime.Value.TotalMilliseconds, args.MaxTime.HasValue } // optional
            };
            return RunCommandAsAsync<GeoHaystackSearchResult<TDocument>>(command);
        }

        /// <summary>
        /// Runs a geoHaystack search command on this collection.
        /// </summary>
        /// <param name="documentType">The type to deserialize the documents as.</param>
        /// <param name="x">The x coordinate of the starting location.</param>
        /// <param name="y">The y coordinate of the starting location.</param>
        /// <param name="options">The options for the geoHaystack search (null if none).</param>
        /// <returns>A <see cref="GeoNearResult{TDocument}"/>.</returns>
        [Obsolete("Use the overload of GeoHaystackSearchAs that has a GeoHaystackSearchArgs parameter instead.")]
        public virtual GeoHaystackSearchResult GeoHaystackSearchAs(
            Type documentType,
            double x,
            double y,
            IMongoGeoHaystackSearchOptions options)
        {
            var methodDefinition = GetType().GetMethod("GeoHaystackSearchAs", new Type[] { typeof(double), typeof(double), typeof(IMongoGeoHaystackSearchOptions) });
            var methodInfo = methodDefinition.MakeGenericMethod(documentType);
            return (GeoHaystackSearchResult)methodInfo.Invoke(this, new object[] { x, y, options });
        }

        /// <summary>
        /// Runs a geoHaystack search command on this collection.
        /// </summary>
        /// <param name="documentType">The type to deserialize the documents as.</param>
        /// <param name="args">The args.</param>
        /// <returns>A <see cref="GeoNearResult{TDocument}"/>.</returns>
        public virtual GeoHaystackSearchResult GeoHaystackSearchAs(Type documentType, GeoHaystackSearchArgs args)
        {
            var methodDefinition = GetType().GetMethod("GeoHaystackSearchAs", new Type[] { typeof(GeoHaystackSearchArgs) });
            var methodInfo = methodDefinition.MakeGenericMethod(documentType);
            return (GeoHaystackSearchResult)methodInfo.Invoke(this, new object[] { args });
        }

        /// <summary>
        /// Runs a GeoNear command on this collection.
        /// </summary>
        /// <typeparam name="TDocument">The type to deserialize the documents as.</typeparam>
        /// <param name="args">The args.</param>
        /// <returns>A <see cref="GeoNearResult{TDocument}"/>.</returns>
        public virtual async Task<GeoNearResult<TDocument>> GeoNearAsAsync<TDocument>(GeoNearArgs args)
        {
            if (args == null) { throw new ArgumentNullException("args"); }
            if (args.Near == null) { throw new ArgumentException("Near is null.", "args"); }

            var command = new CommandDocument
            {
                { "geoNear", _name },
                { "near", args.Near.ToGeoNearCommandValue() },
                { "limit", () => args.Limit.Value, args.Limit.HasValue }, // optional
                { "maxDistance", () => args.MaxDistance.Value, args.MaxDistance.HasValue }, // optional
                { "query", () => BsonDocumentWrapper.Create(args.Query), args.Query != null }, // optional
                { "spherical", () => args.Spherical.Value, args.Spherical.HasValue }, // optional
                { "distanceMultiplier", () => args.DistanceMultiplier.Value, args.DistanceMultiplier.HasValue }, // optional
                { "includeLocs", () => args.IncludeLocs.Value, args.IncludeLocs.HasValue }, // optional
                { "uniqueDocs", () => args.UniqueDocs.Value, args.UniqueDocs.HasValue }, // optional
                { "maxTimeMS", () => args.MaxTime.Value.TotalMilliseconds, args.MaxTime.HasValue } // optional
            };
            var result = await RunCommandAsAsync<GeoNearResult<TDocument>>(command).ConfigureAwait(false);
            result.Response["ns"] = FullName; 
            return result;
        }

        /// <summary>
        /// Runs a GeoNear command on this collection.
        /// </summary>
        /// <typeparam name="TDocument">The type to deserialize the documents as.</typeparam>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="x">The x coordinate of the starting location.</param>
        /// <param name="y">The y coordinate of the starting location.</param>
        /// <param name="limit">The maximum number of results returned.</param>
        /// <returns>A <see cref="GeoNearResult{TDocument}"/>.</returns>
        [Obsolete("Use the overload of GeoNearAs that has a GeoNearArgs parameter instead.")]
        public virtual Task<GeoNearResult<TDocument>> GeoNearAsAsync<TDocument>(
            IMongoQuery query,
            double x,
            double y,
            int limit)
        {
#pragma warning disable 618
            return GeoNearAsAsync<TDocument>(query, x, y, limit, GeoNearOptions.Null);
#pragma warning restore
        }

        /// <summary>
        /// Runs a GeoNear command on this collection.
        /// </summary>
        /// <typeparam name="TDocument">The type to deserialize the documents as.</typeparam>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="x">The x coordinate of the starting location.</param>
        /// <param name="y">The y coordinate of the starting location.</param>
        /// <param name="limit">The maximum number of results returned.</param>
        /// <param name="options">The GeoNear command options (usually a GeoNearOptionsDocument or constructed using the GeoNearOptions builder).</param>
        /// <returns>A <see cref="GeoNearResult{TDocument}"/>.</returns>
        [Obsolete("Use the overload of GeoNearAs that has a GeoNearArgs parameter instead.")]
        public virtual Task<GeoNearResult<TDocument>> GeoNearAsAsync<TDocument>(
            IMongoQuery query,
            double x,
            double y,
            int limit,
            IMongoGeoNearOptions options)
        {
            var optionsDocument = options.ToBsonDocument();
            var args = new GeoNearArgs
            {
                Near = new XYPoint(x, y),
                Limit = limit,
                Query = query,
                DistanceMultiplier = optionsDocument.Contains("distanceMultiplier") ? (double?)optionsDocument["distanceMultiplier"].ToDouble() : null,
                MaxDistance = optionsDocument.Contains("maxDistance") ? (double?)optionsDocument["maxDistance"].ToDouble() : null,
                Spherical = optionsDocument.Contains("spherical") ? (bool?)optionsDocument["spherical"].ToBoolean() : null
            };
            return GeoNearAsAsync<TDocument>(args);
        }

        /// <summary>
        /// Runs a GeoNear command on this collection.
        /// </summary>
        /// <param name="documentType">The type to deserialize the documents as.</param>
        /// <param name="args">The args.</param>
        /// <returns>A <see cref="GeoNearResult{TDocument}"/>.</returns>
        public virtual GeoNearResult GeoNearAs(Type documentType, GeoNearArgs args)
        {
            var methodDefinition = GetType().GetMethod("GeoNearAs", new Type[] { typeof(GeoNearArgs) });
            var methodInfo = methodDefinition.MakeGenericMethod(documentType);
            return (GeoNearResult)methodInfo.Invoke(this, new object[] { args });
        }

        /// <summary>
        /// Runs a GeoNear command on this collection.
        /// </summary>
        /// <param name="documentType">The type to deserialize the documents as.</param>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="x">The x coordinate of the starting location.</param>
        /// <param name="y">The y coordinate of the starting location.</param>
        /// <param name="limit">The maximum number of results returned.</param>
        /// <returns>A <see cref="GeoNearResult{TDocument}"/>.</returns>
        [Obsolete("Use the overload of GeoNearAs that has a GeoNearArgs parameter instead.")]
        public virtual GeoNearResult GeoNearAs(Type documentType, IMongoQuery query, double x, double y, int limit)
        {
            return GeoNearAs(documentType, query, x, y, limit, GeoNearOptions.Null);
        }

        /// <summary>
        /// Runs a GeoNear command on this collection.
        /// </summary>
        /// <param name="documentType">The type to deserialize the documents as.</param>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="x">The x coordinate of the starting location.</param>
        /// <param name="y">The y coordinate of the starting location.</param>
        /// <param name="limit">The maximum number of results returned.</param>
        /// <param name="options">The GeoNear command options (usually a GeoNearOptionsDocument or constructed using the GeoNearOptions builder).</param>
        /// <returns>A <see cref="GeoNearResult{TDocument}"/>.</returns>
        [Obsolete("Use the overload of GeoNearAs that has a GeoNearArgs parameter instead.")]
        public virtual GeoNearResult GeoNearAs(
            Type documentType,
            IMongoQuery query,
            double x,
            double y,
            int limit,
            IMongoGeoNearOptions options)
        {
            var methodDefinition = GetType().GetMethod("GeoNearAs", new Type[] { typeof(IMongoQuery), typeof(double), typeof(double), typeof(int), typeof(IMongoGeoNearOptions) });
            var methodInfo = methodDefinition.MakeGenericMethod(documentType);
            return (GeoNearResult)methodInfo.Invoke(this, new object[] { query, x, y, limit, options });
        }

        /// <summary>
        /// Gets the indexes for this collection.
        /// </summary>
        /// <returns>A list of BsonDocuments that describe the indexes.</returns>
        public virtual GetIndexesResult GetIndexes()
        {
            var indexes = _database.GetCollection("system.indexes");
            var query = Query.EQ("ns", FullName);
            return new GetIndexesResult(indexes.Find(query).ToArray()); // ToArray forces execution of the query
        }

        /// <summary>
        /// Gets the stats for this collection.
        /// </summary>
        /// <returns>The stats for this collection as a <see cref="CollectionStatsResult"/>.</returns>
        public virtual Task<CollectionStatsResult> GetStatsAsync()
        {
            return GetStatsAsync(new GetStatsArgs());
        }

        /// <summary>
        /// Gets the stats for this collection.
        /// </summary>
        /// <param name="args">The args.</param>
        /// <returns>The stats for this collection as a <see cref="CollectionStatsResult"/>.</returns>
        public virtual Task<CollectionStatsResult> GetStatsAsync(GetStatsArgs args)
        {
            if (args == null) { throw new ArgumentNullException("args"); }

            var command = new CommandDocument
            {
                { "collstats", _name },
                { "scale", () => args.Scale.Value, args.Scale.HasValue }, // optional
                { "maxTimeMS", () => args.MaxTime.Value.TotalMilliseconds, args.MaxTime.HasValue } // optional
            };
            return RunCommandAsAsync<CollectionStatsResult>(command);
        }

        /// <summary>
        /// Gets the total data size for this collection (data + indexes).
        /// </summary>
        /// <returns>The total data size.</returns>
        public virtual async Task<long> GetTotalDataSizeAsync()
        {
            var totalSize = (await GetStatsAsync().ConfigureAwait(false)).DataSize;
            foreach (var index in GetIndexes())
            {
                var indexCollectionName = string.Format("{0}.${1}", _name, index.Name);
                var indexCollection = _database.GetCollection(indexCollectionName);
                totalSize += (await indexCollection.GetStatsAsync().ConfigureAwait(false)).DataSize;
            }
            return totalSize;
        }

        /// <summary>
        /// Gets the total storage size for this collection (data + indexes + overhead).
        /// </summary>
        /// <returns>The total storage size.</returns>
        public virtual async Task<long> GetTotalStorageSizeAsync()
        {
            var totalSize = (await GetStatsAsync().ConfigureAwait(false)).StorageSize;
            foreach (var index in GetIndexes())
            {
                var indexCollectionName = string.Format("{0}.${1}", _name, index.Name);
                var indexCollection = _database.GetCollection(indexCollectionName);
                totalSize += (await indexCollection.GetStatsAsync().ConfigureAwait(false)).StorageSize;
            }
            return totalSize;
        }

        /// <summary>
        /// Runs the group command on this collection.
        /// </summary>
        /// <param name="args">The args.</param>
        /// <returns>A list of results as BsonDocuments.</returns>
        public virtual async Task<IEnumerable<BsonDocument>> GroupAsync(GroupArgs args)
        {
            if (args == null) { throw new ArgumentNullException("args"); }
            if (args.KeyFields == null && args.KeyFunction == null)
            {
                throw new ArgumentException("KeyFields and KeyFunction are both null.", "args");
            }
            if (args.KeyFields != null && args.KeyFunction != null)
            {
                throw new ArgumentException("KeyFields and KeyFunction are mutually exclusive.", "args");
            }
            if (args.Initial == null)
            {
                throw new ArgumentException("Initial is null.", "args");
            }
            if (args.ReduceFunction == null)
            {
                throw new ArgumentException("ReduceFunction is null.", "args");
            }

            var command = new CommandDocument
            {
                { "group", new BsonDocument
                    {
                        { "ns", _name },
                        { "key", () => BsonDocumentWrapper.Create(args.KeyFields), args.KeyFields != null }, // key and keyf are mutually exclusive
                        { "$keyf", args.KeyFunction, args.KeyFunction != null },
                        { "$reduce", args.ReduceFunction },
                        { "initial", args.Initial },
                        { "cond", () => BsonDocumentWrapper.Create(args.Query), args.Query != null }, // optional
                        { "finalize", args.FinalizeFunction, args.FinalizeFunction != null } // optional
                    }
                },
                { "maxTimeMS", () => args.MaxTime.Value.TotalMilliseconds, args.MaxTime.HasValue } // optional
            };
            var result = await RunCommandAsAsync<CommandResult>(command).ConfigureAwait(false);
            return result.Response["retval"].AsBsonArray.Values.Cast<BsonDocument>();
        }

        /// <summary>
        /// Runs the group command on this collection.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="keyFunction">A JavaScript function that returns the key value to group on.</param>
        /// <param name="initial">Initial value passed to the reduce function for each group.</param>
        /// <param name="reduce">A JavaScript function that is called for each matching document in a group.</param>
        /// <param name="finalize">A JavaScript function that is called at the end of the group command.</param>
        /// <returns>A list of results as BsonDocuments.</returns>
        public virtual Task<IEnumerable<BsonDocument>> GroupAsync(
            IMongoQuery query,
            BsonJavaScript keyFunction,
            BsonDocument initial,
            BsonJavaScript reduce,
            BsonJavaScript finalize)
        {
            return GroupAsync(new GroupArgs
            {
                Query = query,
                KeyFunction = keyFunction,
                Initial = initial,
                ReduceFunction = reduce,
                FinalizeFunction = finalize
            });
        }

        /// <summary>
        /// Runs the group command on this collection.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="keys">The names of the fields to group on.</param>
        /// <param name="initial">Initial value passed to the reduce function for each group.</param>
        /// <param name="reduce">A JavaScript function that is called for each matching document in a group.</param>
        /// <param name="finalize">A JavaScript function that is called at the end of the group command.</param>
        /// <returns>A list of results as BsonDocuments.</returns>
        public virtual Task<IEnumerable<BsonDocument>> GroupAsync(
            IMongoQuery query,
            IMongoGroupBy keys,
            BsonDocument initial,
            BsonJavaScript reduce,
            BsonJavaScript finalize)
        {
            return GroupAsync(new GroupArgs
            {
                Query = query,
                KeyFields = keys,
                Initial = initial,
                ReduceFunction = reduce,
                FinalizeFunction = finalize
            });
        }

        /// <summary>
        /// Runs the group command on this collection.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="key">The name of the field to group on.</param>
        /// <param name="initial">Initial value passed to the reduce function for each group.</param>
        /// <param name="reduce">A JavaScript function that is called for each matching document in a group.</param>
        /// <param name="finalize">A JavaScript function that is called at the end of the group command.</param>
        /// <returns>A list of results as BsonDocuments.</returns>
        public virtual Task<IEnumerable<BsonDocument>> GroupAsync(
            IMongoQuery query,
            string key,
            BsonDocument initial,
            BsonJavaScript reduce,
            BsonJavaScript finalize)
        {
            return GroupAsync(new GroupArgs
            {
                Query = query,
                KeyFields = GroupBy.Keys(key),
                Initial = initial,
                ReduceFunction = reduce,
                FinalizeFunction = finalize
            });
        }

        /// <summary>
        /// Tests whether an index exists.
        /// </summary>
        /// <param name="keys">The indexed fields (usually an IndexKeysDocument or constructed using the IndexKeys builder).</param>
        /// <returns>True if the index exists.</returns>
        public virtual Task<bool> IndexExistsAsync(IMongoIndexKeys keys)
        {
            string indexName = GetIndexName(keys.ToBsonDocument(), null);
            return IndexExistsByNameAsync(indexName);
        }

        /// <summary>
        /// Tests whether an index exists.
        /// </summary>
        /// <param name="keyNames">The names of the fields in the index.</param>
        /// <returns>True if the index exists.</returns>
        public virtual Task<bool> IndexExistsAsync(params string[] keyNames)
        {
            string indexName = GetIndexName(keyNames);
            return IndexExistsByNameAsync(indexName);
        }

        /// <summary>
        /// Tests whether an index exists.
        /// </summary>
        /// <param name="indexName">The name of the index.</param>
        /// <returns>True if the index exists.</returns>
        public virtual async Task<bool> IndexExistsByNameAsync(string indexName)
        {
            var indexes = _database.GetCollection("system.indexes");
            var query = Query.And(Query.EQ("name", indexName), Query.EQ("ns", FullName));
            return (await indexes.CountAsync(query).ConfigureAwait(false)) != 0;
        }

        /// <summary>
        /// Creates a fluent builder for an ordered bulk operation.
        /// </summary>
        /// <returns>A fluent bulk operation builder.</returns>
        public virtual BulkWriteOperation InitializeOrderedBulkOperation()
        {
            return new BulkWriteOperation(this, true);
        }

        /// <summary>
        /// Creates a fluent builder for an unordered bulk operation.
        /// </summary>
        /// <returns>A fluent bulk operation builder.</returns>
        public virtual BulkWriteOperation InitializeUnorderedBulkOperation()
        {
            return new BulkWriteOperation(this, false);
        }

        // WARNING: be VERY careful about adding any new overloads of Insert or InsertBatch (just don't do it!)
        // it's very easy for the compiler to end up inferring the wrong type for TDocument!
        // that's also why Insert and InsertBatch have to have different names

        /// <summary>
        /// Inserts a document into this collection (see also InsertBatch to insert multiple documents at once).
        /// </summary>
        /// <typeparam name="TNominalType">The nominal type of the document to insert.</typeparam>
        /// <param name="document">The document to insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> InsertAsync<TNominalType>(TNominalType document)
        {
            return InsertAsync(typeof(TNominalType), document);
        }

        /// <summary>
        /// Inserts a document into this collection (see also InsertBatch to insert multiple documents at once).
        /// </summary>
        /// <typeparam name="TNominalType">The nominal type of the document to insert.</typeparam>
        /// <param name="document">The document to insert.</param>
        /// <param name="options">The options to use for this Insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> InsertAsync<TNominalType>(TNominalType document, MongoInsertOptions options)
        {
            return InsertAsync(typeof(TNominalType), document, options);
        }

        /// <summary>
        /// Inserts a document into this collection (see also InsertBatch to insert multiple documents at once).
        /// </summary>
        /// <typeparam name="TNominalType">The nominal type of the document to insert.</typeparam>
        /// <param name="document">The document to insert.</param>
        /// <param name="writeConcern">The write concern to use for this Insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> InsertAsync<TNominalType>(TNominalType document, WriteConcern writeConcern)
        {
            return InsertAsync(typeof(TNominalType), document, writeConcern);
        }

        /// <summary>
        /// Inserts a document into this collection (see also InsertBatch to insert multiple documents at once).
        /// </summary>
        /// <param name="nominalType">The nominal type of the document to insert.</param>
        /// <param name="document">The document to insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> InsertAsync(Type nominalType, object document)
        {
            var options = new MongoInsertOptions();
            return InsertAsync(nominalType, document, options);
        }

        /// <summary>
        /// Inserts a document into this collection (see also InsertBatch to insert multiple documents at once).
        /// </summary>
        /// <param name="nominalType">The nominal type of the document to insert.</param>
        /// <param name="document">The document to insert.</param>
        /// <param name="options">The options to use for this Insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual async Task<WriteConcernResult> InsertAsync(Type nominalType, object document, MongoInsertOptions options)
        {
            if (document == null)
            {
                throw new ArgumentNullException("document");
            }
            var results = await InsertBatchAsync(nominalType, new object[] { document }, options).ConfigureAwait(false);
            return (results == null) ? null : results.Single();
        }

        /// <summary>
        /// Inserts a document into this collection (see also InsertBatch to insert multiple documents at once).
        /// </summary>
        /// <param name="nominalType">The nominal type of the document to insert.</param>
        /// <param name="document">The document to insert.</param>
        /// <param name="writeConcern">The write concern to use for this Insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> InsertAsync(Type nominalType, object document, WriteConcern writeConcern)
        {
            var options = new MongoInsertOptions { WriteConcern = writeConcern};
            return InsertAsync(nominalType, document, options);
        }

        /// <summary>
        /// Inserts multiple documents at once into this collection (see also Insert to insert a single document).
        /// </summary>
        /// <typeparam name="TNominalType">The type of the documents to insert.</typeparam>
        /// <param name="documents">The documents to insert.</param>
        /// <returns>A list of WriteConcernResults (or null if WriteConcern is disabled).</returns>
        public virtual Task<IEnumerable<WriteConcernResult>> InsertBatchAsync<TNominalType>(IEnumerable<TNominalType> documents)
        {
            if (documents == null)
            {
                throw new ArgumentNullException("documents");
            }
            return InsertBatchAsync(typeof(TNominalType), documents.Cast<object>());
        }

        /// <summary>
        /// Inserts multiple documents at once into this collection (see also Insert to insert a single document).
        /// </summary>
        /// <typeparam name="TNominalType">The type of the documents to insert.</typeparam>
        /// <param name="documents">The documents to insert.</param>
        /// <param name="options">The options to use for this Insert.</param>
        /// <returns>A list of WriteConcernResults (or null if WriteConcern is disabled).</returns>
        public virtual Task<IEnumerable<WriteConcernResult>> InsertBatchAsync<TNominalType>(
            IEnumerable<TNominalType> documents,
            MongoInsertOptions options)
        {
            if (documents == null)
            {
                throw new ArgumentNullException("documents");
            }
            return InsertBatchAsync(typeof(TNominalType), documents.Cast<object>(), options);
        }

        /// <summary>
        /// Inserts multiple documents at once into this collection (see also Insert to insert a single document).
        /// </summary>
        /// <typeparam name="TNominalType">The type of the documents to insert.</typeparam>
        /// <param name="documents">The documents to insert.</param>
        /// <param name="writeConcern">The write concern to use for this Insert.</param>
        /// <returns>A list of WriteConcernResults (or null if WriteConcern is disabled).</returns>
        public virtual Task<IEnumerable<WriteConcernResult>> InsertBatchAsync<TNominalType>(
            IEnumerable<TNominalType> documents,
            WriteConcern writeConcern)
        {
            if (documents == null)
            {
                throw new ArgumentNullException("documents");
            }
            return InsertBatchAsync(typeof(TNominalType), documents.Cast<object>(), writeConcern);
        }

        /// <summary>
        /// Inserts multiple documents at once into this collection (see also Insert to insert a single document).
        /// </summary>
        /// <param name="nominalType">The nominal type of the documents to insert.</param>
        /// <param name="documents">The documents to insert.</param>
        /// <returns>A list of WriteConcernResults (or null if WriteConcern is disabled).</returns>
        public virtual Task<IEnumerable<WriteConcernResult>> InsertBatchAsync(Type nominalType, IEnumerable documents)
        {
            var options = new MongoInsertOptions();
            return InsertBatchAsync(nominalType, documents, options);
        }

        /// <summary>
        /// Inserts multiple documents at once into this collection (see also Insert to insert a single document).
        /// </summary>
        /// <param name="nominalType">The nominal type of the documents to insert.</param>
        /// <param name="documents">The documents to insert.</param>
        /// <param name="writeConcern">The write concern to use for this Insert.</param>
        /// <returns>A list of WriteConcernResults (or null if WriteConcern is disabled).</returns>
        public virtual Task<IEnumerable<WriteConcernResult>> InsertBatchAsync(
            Type nominalType,
            IEnumerable documents,
            WriteConcern writeConcern)
        {
            var options = new MongoInsertOptions { WriteConcern = writeConcern};
            return InsertBatchAsync(nominalType, documents, options);
        }

        /// <summary>
        /// Inserts multiple documents at once into this collection (see also Insert to insert a single document).
        /// </summary>
        /// <param name="nominalType">The nominal type of the documents to insert.</param>
        /// <param name="documents">The documents to insert.</param>
        /// <param name="options">The options to use for this Insert.</param>
        /// <returns>A list of WriteConcernResults (or null if WriteConcern is disabled).</returns>
        public virtual async Task<IEnumerable<WriteConcernResult>> InsertBatchAsync(
            Type nominalType,
            IEnumerable documents,
            MongoInsertOptions options)
        {
            if (nominalType == null)
            {
                throw new ArgumentNullException("nominalType");
            }
            if (documents == null)
            {
                throw new ArgumentNullException("documents");
            }
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            var connection = await _server.AcquireConnectionAsync(ReadPreference.Primary).ConfigureAwait(false);
            try
            {
                var assignId = _settings.AssignIdOnInsert ? (Action<InsertRequest>)AssignId : null;
                var checkElementNames = options.CheckElementNames;
                var isOrdered = ((options.Flags & InsertFlags.ContinueOnError) == 0);
                var maxBatchCount = int.MaxValue;
                var maxBatchLength = connection.ServerInstance.MaxMessageLength;
                var maxDocumentSize = connection.ServerInstance.MaxDocumentSize;
                var maxWireDocumentSize = connection.ServerInstance.MaxWireDocumentSize;
                var requests = documents.Cast<object>().Select(document =>
                {
                    return new InsertRequest(nominalType, document);
                });
                var writeConcern = options.WriteConcern ?? _settings.WriteConcern;

                var args = new BulkInsertOperationArgs(
                    assignId,
                    checkElementNames,
                    _name,
                    _database.Name,
                    maxBatchCount,
                    maxBatchLength,
                    maxDocumentSize,
                    maxWireDocumentSize,
                    isOrdered,
                    GetBinaryReaderSettings(),
                    requests,
                    writeConcern,
                    GetBinaryWriterSettings());
                var insertOperation = new InsertOpcodeOperation(args);

                return await insertOperation.ExecuteAsync(connection).ConfigureAwait(false);
            }
            finally
            {
                _server.ReleaseConnection(connection);
            }
        }

        /// <summary>
        /// Tests whether this collection is capped.
        /// </summary>
        /// <returns>True if this collection is capped.</returns>
        public virtual async Task<bool> IsCappedAsync()
        {
            return (await GetStatsAsync()).IsCapped;
        }

        /// <summary>
        /// Runs a Map/Reduce command on this collection.
        /// </summary>
        /// <param name="map">A JavaScript function called for each document.</param>
        /// <param name="reduce">A JavaScript function called on the values emitted by the map function.</param>
        /// <param name="options">Options for this map/reduce command (see <see cref="MapReduceOptionsDocument"/>, <see cref="MapReduceOptionsWrapper"/> and the <see cref="MapReduceOptions"/> builder).</param>
        /// <returns>A <see cref="MapReduceResult"/>.</returns>
        [Obsolete("Use the overload of MapReduce that has a MapReduceArgs parameter instead.")]
        public virtual Task<MapReduceResult> MapReduceAsync(
            BsonJavaScript map,
            BsonJavaScript reduce,
            IMongoMapReduceOptions options)
        {
            var args = MapReduceArgsFromOptions(options);
            args.MapFunction = map;
            args.ReduceFunction = reduce;
            return MapReduceAsync(args);
        }

        /// <summary>
        /// Runs a Map/Reduce command on document in this collection that match a query.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="map">A JavaScript function called for each document.</param>
        /// <param name="reduce">A JavaScript function called on the values emitted by the map function.</param>
        /// <param name="options">Options for this map/reduce command (see <see cref="MapReduceOptionsDocument"/>, <see cref="MapReduceOptionsWrapper"/> and the <see cref="MapReduceOptions"/> builder).</param>
        /// <returns>A <see cref="MapReduceResult"/>.</returns>
        [Obsolete("Use the overload of MapReduce that has a MapReduceArgs parameter instead.")]
        public virtual Task<MapReduceResult> MapReduceAsync(
            IMongoQuery query,
            BsonJavaScript map,
            BsonJavaScript reduce,
            IMongoMapReduceOptions options)
        {
            var args = MapReduceArgsFromOptions(options);
            args.Query = query;
            args.MapFunction = map;
            args.ReduceFunction = reduce;
            return MapReduceAsync(args);
        }

        /// <summary>
        /// Runs a Map/Reduce command on document in this collection that match a query.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="map">A JavaScript function called for each document.</param>
        /// <param name="reduce">A JavaScript function called on the values emitted by the map function.</param>
        /// <returns>A <see cref="MapReduceResult"/>.</returns>
        [Obsolete("Use the overload of MapReduce that has a MapReduceArgs parameter instead.")]
        public virtual Task<MapReduceResult> MapReduceAsync(IMongoQuery query, BsonJavaScript map, BsonJavaScript reduce)
        {
            return MapReduceAsync(new MapReduceArgs { Query = query, MapFunction = map, ReduceFunction = reduce });
        }

        /// <summary>
        /// Runs a Map/Reduce command on this collection.
        /// </summary>
        /// <param name="map">A JavaScript function called for each document.</param>
        /// <param name="reduce">A JavaScript function called on the values emitted by the map function.</param>
        /// <returns>A <see cref="MapReduceResult"/>.</returns>
        [Obsolete("Use the overload of MapReduce that has a MapReduceArgs parameter instead.")]
        public virtual Task<MapReduceResult> MapReduceAsync(BsonJavaScript map, BsonJavaScript reduce)
        {
            return MapReduceAsync(new MapReduceArgs { MapFunction = map, ReduceFunction = reduce });
        }

        /// <summary>
        /// Runs a Map/Reduce command on this collection.
        /// </summary>
        /// <param name="args">The args.</param>
        /// <returns>A <see cref="MapReduceResult"/>.</returns>
        public virtual async Task<MapReduceResult> MapReduceAsync(MapReduceArgs args)
        {
            if (args == null) { throw new ArgumentNullException("args"); }
            if (args.MapFunction == null) { throw new ArgumentException("MapFunction is null.", "args"); }
            if (args.ReduceFunction == null) { throw new ArgumentException("ReduceFunction is null.", "args"); }

            BsonDocument output;
            if (args.OutputMode == MapReduceOutputMode.Inline)
            {
                output = new BsonDocument("inline", 1);
            }
            else
            {
                if (args.OutputCollectionName == null) { throw new ArgumentException("OutputCollectionName is null and OutputMode is not Inline.", "args"); }
                var action = MongoUtils.ToCamelCase(args.OutputMode.ToString());
                output = new BsonDocument
                {
                    { action, args.OutputCollectionName },
                    { "db", args.OutputDatabaseName, args.OutputDatabaseName != null }, // optional
                    { "sharded", () => args.OutputIsSharded.Value, args.OutputIsSharded.HasValue }, // optional
                    { "nonAtomic", () => args.OutputIsNonAtomic.Value, args.OutputIsNonAtomic.HasValue } // optional
                };
            }

            var command = new CommandDocument
            {
                { "mapreduce", _name }, // all lowercase for backwards compatibility
                { "map", args.MapFunction },
                { "reduce", args.ReduceFunction },
                { "out", output },
                { "query", () => BsonDocumentWrapper.Create(args.Query), args.Query != null }, // optional
                { "sort", () => BsonDocumentWrapper.Create(args.SortBy), args.SortBy != null }, // optional
                { "limit", () => args.Limit.Value, args.Limit.HasValue }, // optional
                { "finalize", args.FinalizeFunction, args.FinalizeFunction != null }, // optional
                { "scope", () => BsonDocumentWrapper.Create(args.Scope), args.Scope != null }, // optional
                { "jsMode", () => args.JsMode.Value, args.JsMode.HasValue }, // optional
                { "verbose", () => args.Verbose.Value, args.Verbose.HasValue }, // optional
                { "maxTimeMS", () => args.MaxTime.Value.TotalMilliseconds, args.MaxTime.HasValue } // optional
            };
            var result = await RunCommandAsAsync<MapReduceResult>(command);
            result.SetInputDatabase(_database);

            return result;
        }

        /// <summary>
        /// Scans an entire collection in parallel using multiple cursors.
        /// </summary>
        /// <typeparam name="TDocument">The type of the document.</typeparam>
        /// <param name="args">The args.</param>
        /// <returns>Multiple enumerators, one for each cursor.</returns>
        public async Task<ReadOnlyCollection<IEnumerator<TDocument>>> ParallelScanAsAsync<TDocument>(ParallelScanArgs args)
        {
            var readPreference = args.ReadPreference ?? _settings.ReadPreference ?? ReadPreference.Primary;
            var connection = await _server.AcquireConnectionAsync(readPreference).ConfigureAwait(false);
            try
            {
                var serializer = args.Serializer ?? BsonSerializer.LookupSerializer(typeof(TDocument));
                var serializationOptions = args.SerializationOptions;

                var operation = new ParallelScanOperation<TDocument>(
                    _database.Name,
                    _name,
                    args.NumberOfCursors,
                    args.BatchSize,
                    serializer,    
                    serializationOptions,
                    readPreference,
                    GetBinaryReaderSettings(),
                    GetBinaryWriterSettings());
                return await operation.ExecuteAsync(connection).ConfigureAwait(false);
            }
            finally
            {
                _server.ReleaseConnection(connection);
            }
        }

        /// <summary>
        /// Scans an entire collection in parallel using multiple cursors.
        /// </summary>
        /// <param name="documentType">Type of the document.</param>
        /// <param name="args">The args.</param>
        /// <returns>Multiple enumerators, one for each cursor.</returns>
        public ReadOnlyCollection<IEnumerator> ParallelScanAs(Type documentType, ParallelScanArgs args)
        {
            throw new NotImplementedException();

            //var methodDefinition = GetType().GetMethod("ParallelScanAs", new Type[] { typeof(ParallelScanArgs) });
            //var methodInfo = methodDefinition.MakeGenericMethod(documentType);
            //try
            //{
            //    return ((IEnumerable)methodInfo.Invoke(this, new object[] { args })).Cast<IEnumerator>().ToList().AsReadOnly();
            //}
            //catch (TargetInvocationException ex)
            //{
            //    throw ex.InnerException;
            //}
        }

        /// <summary>
        /// Runs the ReIndex command on this collection.
        /// </summary>
        /// <returns>A CommandResult.</returns>
        public virtual Task<CommandResult> ReIndexAsync()
        {
            var command = new CommandDocument("reIndex", _name);
            return RunCommandAsAsync<CommandResult>(command);
        }

        /// <summary>
        /// Removes documents from this collection that match a query.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> RemoveAsync(IMongoQuery query)
        {
            return RemoveAsync(query, RemoveFlags.None, null);
        }

        /// <summary>
        /// Removes documents from this collection that match a query.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="writeConcern">The write concern to use for this Insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> RemoveAsync(IMongoQuery query, WriteConcern writeConcern)
        {
            return RemoveAsync(query, RemoveFlags.None, writeConcern);
        }

        /// <summary>
        /// Removes documents from this collection that match a query.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="flags">The flags for this Remove (see <see cref="RemoveFlags"/>).</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> RemoveAsync(IMongoQuery query, RemoveFlags flags)
        {
            return RemoveAsync(query, flags, null);
        }

        /// <summary>
        /// Removes documents from this collection that match a query.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="flags">The flags for this Remove (see <see cref="RemoveFlags"/>).</param>
        /// <param name="writeConcern">The write concern to use for this Insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual async Task<WriteConcernResult> RemoveAsync(IMongoQuery query, RemoveFlags flags, WriteConcern writeConcern)
        {
            var connection = await _server.AcquireConnectionAsync(ReadPreference.Primary).ConfigureAwait(false);
            try
            {
                var maxBatchCount = 1;
                var maxBatchLength = connection.ServerInstance.MaxDocumentSize;
                var maxDocumentSize = connection.ServerInstance.MaxDocumentSize;
                var maxWireDocumentSize = connection.ServerInstance.MaxWireDocumentSize;
                var isOrdered = true;
                var requests = new[]
                {
                    new DeleteRequest(query) { Limit = ((flags & RemoveFlags.Single) != 0) ? 1 : 0 }
                };
                writeConcern = writeConcern ?? _settings.WriteConcern;

                var deleteOperationArgs = new BulkDeleteOperationArgs(
                    _name,
                    _database.Name,
                    maxBatchCount,
                    maxBatchLength,
                    maxDocumentSize,
                    maxWireDocumentSize,
                    isOrdered,
                    GetBinaryReaderSettings(),
                    requests,
                    writeConcern,
                    GetBinaryWriterSettings());
                var deleteOperation = new DeleteOpcodeOperation(deleteOperationArgs);

                return await deleteOperation.ExecuteAsync(connection).ConfigureAwait(false);
            }
            finally
            {
                _server.ReleaseConnection(connection);
            }
        }

        /// <summary>
        /// Removes all documents from this collection (see also <see cref="DropAsync"/>).
        /// </summary>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> RemoveAllAsync()
        {
            return RemoveAsync(Query.Null, RemoveFlags.None, null);
        }

        /// <summary>
        /// Removes all documents from this collection (see also <see cref="DropAsync"/>).
        /// </summary>
        /// <param name="writeConcern">The write concern to use for this Insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> RemoveAllAsync(WriteConcern writeConcern)
        {
            return RemoveAsync(Query.Null, RemoveFlags.None, writeConcern);
        }

        /// <summary>
        /// Saves a document to this collection. The document must have an identifiable Id field. Based on the value
        /// of the Id field Save will perform either an Insert or an Update.
        /// </summary>
        /// <typeparam name="TNominalType">The type of the document to save.</typeparam>
        /// <param name="document">The document to save.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> SaveAsync<TNominalType>(TNominalType document)
        {
            return SaveAsync(typeof(TNominalType), document);
        }

        /// <summary>
        /// Saves a document to this collection. The document must have an identifiable Id field. Based on the value
        /// of the Id field Save will perform either an Insert or an Update.
        /// </summary>
        /// <typeparam name="TNominalType">The type of the document to save.</typeparam>
        /// <param name="document">The document to save.</param>
        /// <param name="options">The options to use for this Save.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> SaveAsync<TNominalType>(TNominalType document, MongoInsertOptions options)
        {
            return SaveAsync(typeof(TNominalType), document, options);
        }

        /// <summary>
        /// Saves a document to this collection. The document must have an identifiable Id field. Based on the value
        /// of the Id field Save will perform either an Insert or an Update.
        /// </summary>
        /// <typeparam name="TNominalType">The type of the document to save.</typeparam>
        /// <param name="document">The document to save.</param>
        /// <param name="writeConcern">The write concern to use for this Insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> SaveAsync<TNominalType>(TNominalType document, WriteConcern writeConcern)
        {
            return SaveAsync(typeof(TNominalType), document, writeConcern);
        }

        /// <summary>
        /// Saves a document to this collection. The document must have an identifiable Id field. Based on the value
        /// of the Id field Save will perform either an Insert or an Update.
        /// </summary>
        /// <param name="nominalType">The type of the document to save.</param>
        /// <param name="document">The document to save.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> SaveAsync(Type nominalType, object document)
        {
            var options = new MongoInsertOptions();
            return SaveAsync(nominalType, document, options);
        }

        /// <summary>
        /// Saves a document to this collection. The document must have an identifiable Id field. Based on the value
        /// of the Id field Save will perform either an Insert or an Update.
        /// </summary>
        /// <param name="nominalType">The type of the document to save.</param>
        /// <param name="document">The document to save.</param>
        /// <param name="options">The options to use for this Save.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> SaveAsync(Type nominalType, object document, MongoInsertOptions options)
        {
            if (nominalType == null)
            {
                throw new ArgumentNullException("nominalType");
            }
            if (document == null)
            {
                throw new ArgumentNullException("document");
            }

            var serializer = BsonSerializer.LookupSerializer(document.GetType());

            // if we can determine for sure that it is a new document and we can generate an Id for it then insert it
            var idProvider = serializer as IBsonIdProvider;
            if (idProvider != null)
            {
                object id;
                Type idNominalType;
                IIdGenerator idGenerator;
                var hasId = idProvider.GetDocumentId(document, out id, out idNominalType, out idGenerator);

                if (idGenerator == null && (!hasId || id == null))
                {
                    throw new InvalidOperationException("No IdGenerator found.");
                }

                if (idGenerator != null && (!hasId || idGenerator.IsEmpty(id)))
                {
                    id = idGenerator.GenerateId(this, document);
                    idProvider.SetDocumentId(document, id);
                    return InsertAsync(nominalType, document, options);
                }
            }

            // since we can't determine for sure whether it's a new document or not upsert it
            // the only safe way to get the serialized _id value needed for the query is to serialize the entire document

            var bsonDocument = new BsonDocument();
            var writerSettings = new BsonDocumentWriterSettings { GuidRepresentation = _settings.GuidRepresentation };
            using (var bsonWriter = new BsonDocumentWriter(bsonDocument, writerSettings))
            {
                serializer.Serialize(bsonWriter, nominalType, document, null);
            }

            BsonValue idBsonValue;
            if (!bsonDocument.TryGetValue("_id", out idBsonValue))
            {
                throw new InvalidOperationException("Save can only be used with documents that have an Id.");
            }

            var query = Query.EQ("_id", idBsonValue);
            var update = Builders.Update.Replace(bsonDocument);
            var updateOptions = new MongoUpdateOptions
            {
                CheckElementNames = options.CheckElementNames,
                Flags = UpdateFlags.Upsert,
                WriteConcern = options.WriteConcern
            };

            return UpdateAsync(query, update, updateOptions);
        }

        /// <summary>
        /// Saves a document to this collection. The document must have an identifiable Id field. Based on the value
        /// of the Id field Save will perform either an Insert or an Update.
        /// </summary>
        /// <param name="nominalType">The type of the document to save.</param>
        /// <param name="document">The document to save.</param>
        /// <param name="writeConcern">The write concern to use for this Insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> SaveAsync(Type nominalType, object document, WriteConcern writeConcern)
        {
            var options = new MongoInsertOptions { WriteConcern = writeConcern};
            return SaveAsync(nominalType, document, options);
        }

        /// <summary>
        /// Gets a canonical string representation for this database.
        /// </summary>
        /// <returns>A canonical string representation for this database.</returns>
        public override string ToString()
        {
            return FullName;
        }

        /// <summary>
        /// Updates one matching document in this collection.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="update">The update to perform on the matching document.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> UpdateAsync(IMongoQuery query, IMongoUpdate update)
        {
            var options = new MongoUpdateOptions();
            return UpdateAsync(query, update, options);
        }

        /// <summary>
        /// Updates one or more matching documents in this collection (for multiple updates use UpdateFlags.Multi).
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="update">The update to perform on the matching document.</param>
        /// <param name="options">The update options.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual async Task<WriteConcernResult> UpdateAsync(IMongoQuery query, IMongoUpdate update, MongoUpdateOptions options)
        {
            var updateBuilder = update as UpdateBuilder;
            if (updateBuilder != null)
            {
                if (updateBuilder.Document.ElementCount == 0)
                {
                    throw new ArgumentException("Update called with an empty UpdateBuilder that has no update operations.");
                }
            }

            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            var connection = await _server.AcquireConnectionAsync(ReadPreference.Primary).ConfigureAwait(false);
            try
            {
                var checkElementNames = options.CheckElementNames;
                var maxBatchCount = 1;
                var maxBatchLength = connection.ServerInstance.MaxDocumentSize;
                var maxDocumentSize = connection.ServerInstance.MaxDocumentSize;
                var maxWireDocumentSize = connection.ServerInstance.MaxWireDocumentSize;
                var isOrdered = true;
                var requests = new[]
                {
                    new UpdateRequest(query, update)
                    {
                        IsMultiUpdate = (options.Flags & UpdateFlags.Multi) != 0,
                        IsUpsert = (options.Flags & UpdateFlags.Upsert) != 0
                    }
                };
                var writeConcern = options.WriteConcern ?? _settings.WriteConcern;

                var updateOperationArgs = new BulkUpdateOperationArgs(
                    checkElementNames,
                    _name, // collectionName
                    _database.Name,
                    maxBatchCount,
                    maxBatchLength,
                    maxDocumentSize,
                    maxWireDocumentSize,
                    isOrdered,
                    GetBinaryReaderSettings(),
                    requests,
                    writeConcern,
                    GetBinaryWriterSettings());
                var updateOperation = new UpdateOpcodeOperation(updateOperationArgs);

                return await updateOperation.ExecuteAsync(connection).ConfigureAwait(false);
            }
            finally
            {
                _server.ReleaseConnection(connection);
            }
        }

        /// <summary>
        /// Updates one matching document in this collection.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="update">The update to perform on the matching document.</param>
        /// <param name="writeConcern">The write concern to use for this Insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> UpdateAsync(IMongoQuery query, IMongoUpdate update, WriteConcern writeConcern)
        {
            var options = new MongoUpdateOptions { WriteConcern = writeConcern};
            return UpdateAsync(query, update, options);
        }

        /// <summary>
        /// Updates one or more matching documents in this collection (for multiple updates use UpdateFlags.Multi).
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="update">The update to perform on the matching document.</param>
        /// <param name="flags">The flags for this Update.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> UpdateAsync(IMongoQuery query, IMongoUpdate update, UpdateFlags flags)
        {
            var options = new MongoUpdateOptions { Flags = flags };
            return UpdateAsync(query, update, options);
        }

        /// <summary>
        /// Updates one or more matching documents in this collection (for multiple updates use UpdateFlags.Multi).
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="update">The update to perform on the matching document.</param>
        /// <param name="flags">The flags for this Update.</param>
        /// <param name="writeConcern">The write concern to use for this Insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> UpdateAsync(
            IMongoQuery query,
            IMongoUpdate update,
            UpdateFlags flags,
            WriteConcern writeConcern)
        {
            var options = new MongoUpdateOptions
            {
                Flags = flags,
                WriteConcern = writeConcern
            };
            return UpdateAsync(query, update, options);
        }

        /// <summary>
        /// Validates the integrity of this collection.
        /// </summary>
        /// <returns>A <see cref="ValidateCollectionResult"/>.</returns>
        public virtual Task<ValidateCollectionResult> ValidateAsync()
        {
            return ValidateAsync(new ValidateCollectionArgs());
        }

        /// <summary>
        /// Validates the integrity of this collection.
        /// </summary>
        /// <param name="args">The args.</param>
        /// <returns>A <see cref="ValidateCollectionResult"/>.</returns>
        public virtual Task<ValidateCollectionResult> ValidateAsync(ValidateCollectionArgs args)
        {
            if (args == null) { throw new ArgumentNullException("args"); }

            var command = new CommandDocument
            {
                { "validate", _name },
                { "full", () => args.Full.Value, args.Full.HasValue }, // optional
                { "scandata", () => args.ScanData.Value, args.ScanData.HasValue }, // optional
                { "maxTimeMS", () => args.MaxTime.Value.TotalMilliseconds, args.MaxTime.HasValue } // optional
            };
            return RunCommandAsAsync<ValidateCollectionResult>(command);
        }

        // internal methods
        // TODO: this method can be removed when MongoCursorEnumerator is removed
        internal BsonBinaryReaderSettings GetReaderSettings(MongoConnection connection)
        {
            return new BsonBinaryReaderSettings
            {
                Encoding = _settings.ReadEncoding ?? MongoDefaults.ReadEncoding,
                GuidRepresentation = _settings.GuidRepresentation,
                MaxDocumentSize = connection.ServerInstance.MaxDocumentSize
            };
        }

        // TODO: this method can be removed when MongoCursorEnumerator is removed
        internal BsonBinaryWriterSettings GetWriterSettings(MongoConnection connection)
        {
            return new BsonBinaryWriterSettings
            {
                Encoding = _settings.WriteEncoding ?? MongoDefaults.WriteEncoding,
                GuidRepresentation = _settings.GuidRepresentation,
                MaxDocumentSize = connection.ServerInstance.MaxDocumentSize
            };
        }

        internal Task<AggregateResult> RunAggregateCommandAsync(AggregateArgs args)
        {
            BsonDocument cursor = null;
            if (args.OutputMode == AggregateOutputMode.Cursor)
            {
                cursor = new BsonDocument
                {
                    { "batchSize", () => args.BatchSize.Value, args.BatchSize.HasValue }
                };
            }

            var aggregateCommand = new CommandDocument
            {
                { "aggregate", _name },
                { "pipeline", new BsonArray(args.Pipeline.Cast<BsonValue>()) },
                { "cursor", cursor, cursor != null }, // optional
                { "allowDiskUse", () => args.AllowDiskUse.Value, args.AllowDiskUse.HasValue }, // optional
                { "maxTimeMS", () => args.MaxTime.Value.TotalMilliseconds, args.MaxTime.HasValue } // optional
            };

            return RunCommandAsAsync<AggregateResult>(aggregateCommand);
        }

        // private methods
        private void AssignId(InsertRequest request)
        {
            var serializer = request.Serializer ?? BsonSerializer.LookupSerializer(request.NominalType);
            var idProvider = serializer as IBsonIdProvider;
            if (idProvider != null)
            {
                var document = request.Document;
                if (document != null)
                {
                    object id;
                    Type idNominalType;
                    IIdGenerator idGenerator;
                    if (idProvider.GetDocumentId(document, out id, out idNominalType, out idGenerator))
                    {
                        if (idGenerator != null && idGenerator.IsEmpty(id))
                        {
                            id = idGenerator.GenerateId((MongoCollection)this, document);
                            idProvider.SetDocumentId(document, id);
                        }
                    }
                }
            }
        }

        private BsonDocument CreateIndexDocument(IMongoIndexKeys keys, IMongoIndexOptions options)
        {
            var keysDocument = keys.ToBsonDocument();
            var optionsDocument = options.ToBsonDocument();
            var indexName = GetIndexName(keysDocument, optionsDocument);
            var index = new BsonDocument
            {
                { "ns", FullName },
                { "name", indexName },
                { "key", keysDocument }
            };
            index.Merge(optionsDocument);

            return index;
        }

        private Task<CommandResult> CreateIndexWithCommandAsync(IMongoIndexKeys keys, IMongoIndexOptions options)
        {
            var command = new CommandDocument
            {
                { "createIndexes", Name },
                { "indexes", new BsonArray { CreateIndexDocument(keys, options) } }
            };

            return RunCommandAsAsync<CommandResult>(command);
        }

        private Task<WriteConcernResult> CreateIndexWithInsertAsync(IMongoIndexKeys keys, IMongoIndexOptions options)
        {
            var index = CreateIndexDocument(keys, options);
            var insertOptions = new MongoInsertOptions
            {
                CheckElementNames = false,
                WriteConcern = WriteConcern.Acknowledged
            };
            var indexes = _database.GetCollection("system.indexes");
            var result = indexes.InsertAsync(index, insertOptions);
            return result;
        }

        private MongoCursor FindAs(Type documentType, IMongoQuery query, IBsonSerializer serializer, IBsonSerializationOptions serializationOptions)
        {
            return MongoCursor.Create(documentType, this, query, _settings.ReadPreference, serializer, serializationOptions);
        }

        private MongoCursor<TDocument> FindAs<TDocument>(IMongoQuery query, IBsonSerializer serializer, IBsonSerializationOptions serializationOptions)
        {
            return new MongoCursor<TDocument>(this, query, _settings.ReadPreference, serializer, serializationOptions);
        }

        private CommandResult FindOneAs(Type documentType, IMongoQuery query, IBsonSerializer serializer, IBsonSerializationOptions serializationOptions)
        {
            return FindAs(documentType, query, serializer, serializationOptions).SetLimit(1).Cast<CommandResult>().FirstOrDefault();
        }

        private TDocument FindOneAs<TDocument>(IMongoQuery query, IBsonSerializer serializer, IBsonSerializationOptions serializationOptions)
        {
            return FindAs<TDocument>(query, serializer, serializationOptions).SetLimit(1).FirstOrDefault();
        }

        private BsonBinaryReaderSettings GetBinaryReaderSettings()
        {
            return new BsonBinaryReaderSettings
            {
                Encoding = _settings.ReadEncoding ?? MongoDefaults.ReadEncoding,
                GuidRepresentation = _settings.GuidRepresentation
            };
        }

        private BsonBinaryWriterSettings GetBinaryWriterSettings()
        {
            return new BsonBinaryWriterSettings
            {
                Encoding = _settings.WriteEncoding ?? MongoDefaults.WriteEncoding,
                GuidRepresentation = _settings.GuidRepresentation
            };
        }

        private string GetIndexName(BsonDocument keys, BsonDocument options)
        {
            if (options != null)
            {
                if (options.Contains("name"))
                {
                    return options["name"].AsString;
                }
            }

            StringBuilder sb = new StringBuilder();
            foreach (var element in keys)
            {
                if (sb.Length > 0)
                {
                    sb.Append("_");
                }
                sb.Append(element.Name);
                sb.Append("_");
                var value = element.Value;
                string valueString;
                switch (value.BsonType)
                {
                    case BsonType.Int32: valueString = ((BsonInt32)value).Value.ToString(); break;
                    case BsonType.Int64: valueString = ((BsonInt64)value).Value.ToString(); break;
                    case BsonType.Double: valueString = ((BsonDouble)value).Value.ToString(); break;
                    case BsonType.String: valueString = ((BsonString)value).Value; break;
                    default: valueString = "x"; break;
                }
                sb.Append(valueString.Replace(' ', '_'));
            }
            return sb.ToString();
        }

        private string GetIndexName(string[] keyNames)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string name in keyNames)
            {
                if (sb.Length > 0)
                {
                    sb.Append("_");
                }
                sb.Append(name);
                sb.Append("_1");
            }
            return sb.ToString();
        }

#pragma warning disable 618
        private MapReduceArgs MapReduceArgsFromOptions(IMongoMapReduceOptions options)
#pragma warning restore
        {
            var args = new MapReduceArgs();
            var optionsDocument = options.ToBsonDocument();

            BsonValue finalizeFunction;
            if (optionsDocument.TryGetValue("finalize", out finalizeFunction))
            {
                args.FinalizeFunction = finalizeFunction.AsBsonJavaScript;
            }

            BsonValue jsMode;
            if (optionsDocument.TryGetValue("jsMode", out jsMode))
            {
                args.JsMode = jsMode.ToBoolean();
            }

            BsonValue limit;
            if (optionsDocument.TryGetValue("limit", out limit))
            {
                args.Limit = limit.ToInt64();
            }

            BsonValue mapFunction;
            if (optionsDocument.TryGetValue("map", out mapFunction))
            {
                args.MapFunction = mapFunction.AsBsonJavaScript;
            }

            BsonValue output;
            if (optionsDocument.TryGetValue("out", out output))
            {
                var outputDocument = output.AsBsonDocument;
                var actionElement = outputDocument.GetElement(0);

                args.OutputMode = (MapReduceOutputMode)Enum.Parse(typeof(MapReduceOutputMode), actionElement.Name, true); // ignoreCase
                if (args.OutputMode != MapReduceOutputMode.Inline)
                {
                    args.OutputCollectionName = actionElement.Value.AsString;

                    BsonValue databaseName;
                    if (outputDocument.TryGetValue("db", out databaseName))
                    {
                        args.OutputDatabaseName = databaseName.AsString;
                    }

                    BsonValue outputIsSharded;
                    if (outputDocument.TryGetValue("sharded", out outputIsSharded))
                    {
                        args.OutputIsSharded = outputIsSharded.ToBoolean();
                    }

                    BsonValue nonAtomic;
                    if (outputDocument.TryGetValue("nonAtomic", out nonAtomic))
                    {
                        args.OutputIsNonAtomic = nonAtomic.ToBoolean();
                    }
                }
            }

            BsonValue queryOption;
            if (optionsDocument.TryGetValue("query", out queryOption))
            {
                args.Query = new QueryDocument(queryOption.ToBsonDocument());
            }

            BsonValue reduceFunction;
            if (optionsDocument.TryGetValue("reduce", out reduceFunction))
            {
                args.ReduceFunction = reduceFunction.AsBsonJavaScript;
            }

            BsonValue scope;
            if (optionsDocument.TryGetValue("scope", out scope))
            {
                args.SortBy = new SortByDocument(scope.ToBsonDocument());
            }

            BsonValue sortBy;
            if (optionsDocument.TryGetValue("sort", out sortBy))
            {
                args.SortBy = new SortByDocument(sortBy.ToBsonDocument());
            }

            BsonValue verbose;
            if (optionsDocument.TryGetValue("verbose", out verbose))
            {
                args.Verbose = verbose.ToBoolean();
            }

            return args;
        }

        private Task<TCommandResult> RunCommandAsAsync<TCommandResult>(IMongoCommand command) where TCommandResult : CommandResult
        {
            var resultSerializer = BsonSerializer.LookupSerializer(typeof(TCommandResult));
            return RunCommandAsAsync<TCommandResult>(command, resultSerializer, null);
        }

        private async Task<TCommandResult> RunCommandAsAsync<TCommandResult>(
            IMongoCommand command,
            IBsonSerializer resultSerializer,
            IBsonSerializationOptions resultSerializationOptions) where TCommandResult : CommandResult
        {
            var readPreference = _settings.ReadPreference;
            if (readPreference != ReadPreference.Primary)
            {
                if (_server.ProxyType == MongoServerProxyType.Unknown)
                {
                    await _server.ConnectAsync().ConfigureAwait(false);
                }
                if (_server.ProxyType == MongoServerProxyType.ReplicaSet  && !CanCommandBeSentToSecondary.Delegate(command.ToBsonDocument()))
                {
                    readPreference = ReadPreference.Primary;
                }
            }
            var flags = (readPreference == ReadPreference.Primary) ? QueryFlags.None : QueryFlags.SlaveOk;

            var commandOperation = new CommandOperation<TCommandResult>(
                _database.Name,
                GetBinaryReaderSettings(),
                GetBinaryWriterSettings(),
                command,
                flags,
                null, // options
                readPreference,
                resultSerializationOptions,
                resultSerializer);

            var connection = await _server.AcquireConnectionAsync(readPreference).ConfigureAwait(false);
            try
            {
                return await commandOperation.ExecuteAsync(connection).ConfigureAwait(false);
            }
            finally
            {
                _server.ReleaseConnection(connection);
            }
        }
    }

    // this subclass provides a default document type for Find methods
    // you can still Find any other document type by using the FindAs<TDocument> methods

    /// <summary>
    /// Represents a MongoDB collection and the settings used to access it as well as a default document type. This class is thread-safe.
    /// </summary>
    /// <typeparam name="TDefaultDocument">The default document type of the collection.</typeparam>
    public class MongoCollection<TDefaultDocument> : MongoCollection
    {
        // constructors
        /// <summary>
        /// Creates a new instance of MongoCollection. Normally you would call one of the indexers or GetCollection methods
        /// of MongoDatabase instead.
        /// </summary>
        /// <param name="database">The database that contains this collection.</param>
        /// <param name="name">The name of the collection.</param>
        /// <param name="settings">The settings to use to access this collection.</param>
        public MongoCollection(MongoDatabase database, string name, MongoCollectionSettings settings)
            : base(database, name, settings)
        {
        }

        /// <summary>
        /// Creates a new instance of MongoCollection. Normally you would call one of the indexers or GetCollection methods
        /// of MongoDatabase instead.
        /// </summary>
        /// <param name="database">The database that contains this collection.</param>
        /// <param name="settings">The settings to use to access this collection.</param>
        [Obsolete("Use MongoCollection(MongoDatabase database, string name, MongoCollectionSettings settings) instead.")]
        public MongoCollection(MongoDatabase database, MongoCollectionSettings<TDefaultDocument> settings)
            : this(database, settings.CollectionName, settings)
        {
        }

        // public methods
        /// <summary>
        /// Returns a cursor that can be used to find all documents in this collection that match the query as TDefaultDocuments.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <returns>A <see cref="MongoCursor{TDocument}"/>.</returns>
        public virtual MongoCursor<TDefaultDocument> Find(IMongoQuery query)
        {
            return FindAs<TDefaultDocument>(query);
        }

        /// <summary>
        /// Returns a cursor that can be used to find all documents in this collection as TDefaultDocuments.
        /// </summary>
        /// <returns>A <see cref="MongoCursor{TDocument}"/>.</returns>
        public virtual MongoCursor<TDefaultDocument> FindAll()
        {
            return FindAllAs<TDefaultDocument>();
        }

        /// <summary>
        /// Returns one document in this collection as a TDefaultDocument.
        /// </summary>
        /// <returns>A TDefaultDocument (or null if not found).</returns>
        public virtual Task<TDefaultDocument> FindOneAsync()
        {
            return FindOneAsAsync<TDefaultDocument>();
        }

        /// <summary>
        /// Returns one document in this collection that matches a query as a TDefaultDocument.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <returns>A TDefaultDocument (or null if not found).</returns>
        public virtual Task<TDefaultDocument> FindOneAsync(IMongoQuery query)
        {
            return FindOneAsAsync<TDefaultDocument>(query);
        }

        /// <summary>
        /// Returns a cursor that can be used to find one document in this collection by its _id value as a TDefaultDocument.
        /// </summary>
        /// <param name="id">The id of the document.</param>
        /// <returns>A TDefaultDocument (or null if not found).</returns>
        public virtual Task<TDefaultDocument> FindOneByIdAsync(BsonValue id)
        {
            return FindOneByIdAsAsync<TDefaultDocument>(id);
        }

        /// <summary>
        /// Runs a geoHaystack search command on this collection.
        /// </summary>
        /// <param name="x">The x coordinate of the starting location.</param>
        /// <param name="y">The y coordinate of the starting location.</param>
        /// <param name="options">The options for the geoHaystack search (null if none).</param>
        /// <returns>A <see cref="GeoHaystackSearchResult{TDocument}"/>.</returns>
        [Obsolete("Use the overload of GeoHaystackSearch that has a GeoHaystackSearchArgs parameter instead.")]
        public virtual Task<GeoHaystackSearchResult<TDefaultDocument>> GeoHaystackSearchAsync(
            double x,
            double y,
            IMongoGeoHaystackSearchOptions options)
        {
            return GeoHaystackSearchAsAsync<TDefaultDocument>(x, y, options);
        }

        /// <summary>
        /// Runs a geoHaystack search command on this collection.
        /// </summary>
        /// <param name="args">The args.</param>
        /// <returns>A <see cref="GeoHaystackSearchResult{TDocument}"/>.</returns>
        public virtual Task<GeoHaystackSearchResult<TDefaultDocument>> GeoHaystackSearchAsync(GeoHaystackSearchArgs args)
        {
            return GeoHaystackSearchAsAsync<TDefaultDocument>(args);
        }

        /// <summary>
        /// Runs a GeoNear command on this collection.
        /// </summary>
        /// <param name="args">The args.</param>
        /// <returns>A <see cref="GeoNearResult{TDefaultDocument}"/>.</returns>
        public virtual Task<GeoNearResult<TDefaultDocument>> GeoNearAsync(GeoNearArgs args)
        {
            return GeoNearAsAsync<TDefaultDocument>(args);
        }

        /// <summary>
        /// Runs a GeoNear command on this collection.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="x">The x coordinate of the starting location.</param>
        /// <param name="y">The y coordinate of the starting location.</param>
        /// <param name="limit">The maximum number of results returned.</param>
        /// <returns>A <see cref="GeoNearResult{TDefaultDocument}"/>.</returns>
        [Obsolete("Use the overload of GeoNear that takes a GeoNearArgs parameter instead.")]
        public virtual Task<GeoNearResult<TDefaultDocument>> GeoNearAsync(IMongoQuery query, double x, double y, int limit)
        {
            return GeoNearAsAsync<TDefaultDocument>(query, x, y, limit);
        }

        /// <summary>
        /// Runs a GeoNear command on this collection.
        /// </summary>
        /// <param name="query">The query (usually a QueryDocument or constructed using the Query builder).</param>
        /// <param name="x">The x coordinate of the starting location.</param>
        /// <param name="y">The y coordinate of the starting location.</param>
        /// <param name="limit">The maximum number of results returned.</param>
        /// <param name="options">Options for the GeoNear command (see <see cref="GeoNearOptionsDocument"/>, <see cref="GeoNearOptionsWrapper"/>, and the <see cref="GeoNearOptions"/> builder).</param>
        /// <returns>A <see cref="GeoNearResult{TDefaultDocument}"/>.</returns>
        [Obsolete("Use the overload of GeoNear that takes a GeoNearArgs parameter instead.")]
        public virtual Task<GeoNearResult<TDefaultDocument>> GeoNearAsync(
            IMongoQuery query,
            double x,
            double y,
            int limit,
            IMongoGeoNearOptions options)
        {
            return GeoNearAsAsync<TDefaultDocument>(query, x, y, limit, options);
        }

        /// <summary>
        /// Inserts a document into this collection (see also InsertBatch to insert multiple documents at once).
        /// </summary>
        /// <param name="document">The document to insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> InsertAsync(TDefaultDocument document)
        {
            return InsertAsync<TDefaultDocument>(document);
        }

        /// <summary>
        /// Inserts a document into this collection (see also InsertBatch to insert multiple documents at once).
        /// </summary>
        /// <param name="document">The document to insert.</param>
        /// <param name="options">The options to use for this Insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> InsertAsync(TDefaultDocument document, MongoInsertOptions options)
        {
            return InsertAsync<TDefaultDocument>(document, options);
        }

        /// <summary>
        /// Inserts a document into this collection (see also InsertBatch to insert multiple documents at once).
        /// </summary>
        /// <param name="document">The document to insert.</param>
        /// <param name="writeConcern">The write concern to use for this Insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> InsertAsync(TDefaultDocument document, WriteConcern writeConcern)
        {
            return InsertAsync<TDefaultDocument>(document, writeConcern);
        }

        /// <summary>
        /// Inserts multiple documents at once into this collection (see also Insert to insert a single document).
        /// </summary>
        /// <param name="documents">The documents to insert.</param>
        /// <returns>A list of WriteConcernResults (or null if WriteConcern is disabled).</returns>
        public virtual Task<IEnumerable<WriteConcernResult>> InsertBatchAsync(IEnumerable<TDefaultDocument> documents)
        {
            return InsertBatchAsync<TDefaultDocument>(documents);
        }

        /// <summary>
        /// Inserts multiple documents at once into this collection (see also Insert to insert a single document).
        /// </summary>
        /// <param name="documents">The documents to insert.</param>
        /// <param name="options">The options to use for this Insert.</param>
        /// <returns>A list of WriteConcernResults (or null if WriteConcern is disabled).</returns>
        public virtual Task<IEnumerable<WriteConcernResult>> InsertBatchAsync(
            IEnumerable<TDefaultDocument> documents,
            MongoInsertOptions options)
        {
            return InsertBatchAsync<TDefaultDocument>(documents, options);
        }

        /// <summary>
        /// Inserts multiple documents at once into this collection (see also Insert to insert a single document).
        /// </summary>
        /// <param name="documents">The documents to insert.</param>
        /// <param name="writeConcern">The write concern to use for this Insert.</param>
        /// <returns>A list of WriteConcernResults (or null if WriteConcern is disabled).</returns>
        public virtual Task<IEnumerable<WriteConcernResult>> InsertBatchAsync(
            IEnumerable<TDefaultDocument> documents,
            WriteConcern writeConcern)
        {
            return InsertBatchAsync<TDefaultDocument>(documents, writeConcern);
        }

        /// <summary>
        /// Scans an entire collection in parallel using multiple cursors.
        /// </summary>
        /// <param name="args">The args.</param>
        /// <returns>Multiple enumerators, one for each cursor.</returns>
        public virtual Task<ReadOnlyCollection<IEnumerator<TDefaultDocument>>> ParallelScanAsync(ParallelScanArgs args)
        {
            return ParallelScanAsAsync<TDefaultDocument>(args);
        }

        /// <summary>
        /// Saves a document to this collection. The document must have an identifiable Id field. Based on the value
        /// of the Id field Save will perform either an Insert or an Update.
        /// </summary>
        /// <param name="document">The document to save.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> SaveAsync(TDefaultDocument document)
        {
            return SaveAsync<TDefaultDocument>(document);
        }

        /// <summary>
        /// Saves a document to this collection. The document must have an identifiable Id field. Based on the value
        /// of the Id field Save will perform either an Insert or an Update.
        /// </summary>
        /// <param name="document">The document to save.</param>
        /// <param name="options">The options to use for this Save.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> SaveAsync(TDefaultDocument document, MongoInsertOptions options)
        {
            return SaveAsync<TDefaultDocument>(document, options);
        }

        /// <summary>
        /// Saves a document to this collection. The document must have an identifiable Id field. Based on the value
        /// of the Id field Save will perform either an Insert or an Update.
        /// </summary>
        /// <param name="document">The document to save.</param>
        /// <param name="writeConcern">The write concern to use for this Insert.</param>
        /// <returns>A WriteConcernResult (or null if WriteConcern is disabled).</returns>
        public virtual Task<WriteConcernResult> SaveAsync(TDefaultDocument document, WriteConcern writeConcern)
        {
            return SaveAsync<TDefaultDocument>(document, writeConcern);
        }
    }
}
