﻿/* Copyright 2010-2013 10gen Inc.
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
using System.IO;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Options;

namespace MongoDB.Bson.Serialization.Serializers
{
    /// <summary>
    /// Represents a serializer for three-dimensional arrays.
    /// </summary>
    /// <typeparam name="TItem">The type of the elements.</typeparam>
    public class ThreeDimensionalArraySerializer<TItem> :
        BsonBaseSerializer<TItem[,,]>,
        IChildSerializerConfigurable
    {
        // private fields
        private readonly IBsonSerializer<TItem> _itemSerializer;

        // constructors
        /// <summary>
        /// Initializes a new instance of the <see cref="ThreeDimensionalArraySerializer{TItem}"/> class.
        /// </summary>
        public ThreeDimensionalArraySerializer()
            : this(BsonSerializer.LookupSerializer<TItem>())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ThreeDimensionalArraySerializer{TItem}"/> class.
        /// </summary>
        /// <param name="itemSerializer">The item serializer.</param>
        public ThreeDimensionalArraySerializer(IBsonSerializer<TItem> itemSerializer)
        {
            _itemSerializer = itemSerializer;
        }

        // public properties
        /// <summary>
        /// Gets the item serializer.
        /// </summary>
        /// <value>
        /// The item serializer.
        /// </value>
        public IBsonSerializer<TItem> ItemSerializer
        {
            get { return _itemSerializer; }
        }

        // public methods
        /// <summary>
        /// Deserializes a value.
        /// </summary>
        /// <param name="context">The deserialization context.</param>
        /// <returns>An object.</returns>
        public override TItem[,,] Deserialize(BsonDeserializationContext context)
        {
            var bsonReader = context.Reader;
            string message;

            var bsonType = bsonReader.GetCurrentBsonType();
            switch (bsonType)
            {
                case BsonType.Null:
                    bsonReader.ReadNull();
                    return null;

                case BsonType.Array:
                    bsonReader.ReadStartArray();
                    var outerList = new List<List<List<TItem>>>();
                    while (bsonReader.ReadBsonType() != BsonType.EndOfDocument)
                    {
                        bsonReader.ReadStartArray();
                        var middleList = new List<List<TItem>>();
                        while (bsonReader.ReadBsonType() != BsonType.EndOfDocument)
                        {
                            bsonReader.ReadStartArray();
                            var innerList = new List<TItem>();
                            while (bsonReader.ReadBsonType() != BsonType.EndOfDocument)
                            {
                                var item = context.DeserializeWithChildContext(_itemSerializer);
                                innerList.Add(item);
                            }
                            bsonReader.ReadEndArray();
                            middleList.Add(innerList);
                        }
                        bsonReader.ReadEndArray();
                        outerList.Add(middleList);
                    }
                    bsonReader.ReadEndArray();

                    var length1 = outerList.Count;
                    var length2 = (length1 == 0) ? 0 : outerList[0].Count;
                    var length3 = (length2 == 0) ? 0 : outerList[0][0].Count;
                    var array = new TItem[length1, length2, length3];
                    for (int i = 0; i < length1; i++)
                    {
                        var middleList = outerList[i];
                        if (middleList.Count != length2)
                        {
                            message = string.Format("Middle list {0} is of length {1} but should be of length {2}.", i, middleList.Count, length2);
                            throw new FileFormatException(message);
                        }
                        for (int j = 0; j < length2; j++)
                        {
                            var innerList = middleList[j];
                            if (innerList.Count != length3)
                            {
                                message = string.Format("Inner list {0} is of length {1} but should be of length {2}.", j, innerList.Count, length3);
                                throw new FileFormatException(message);
                            }
                            for (int k = 0; k < length3; k++)
                            {
                                array[i, j, k] = innerList[k];
                            }
                        }
                    }

                    return array;

                default:
                    message = string.Format("Can't deserialize a {0} from BsonType {1}.", typeof(TItem[,,]).FullName, bsonType);
                    throw new FileFormatException(message);
            }
        }

        /// <summary>
        /// Serializes a value.
        /// </summary>
        /// <param name="context">The serialization context.</param>
        /// <param name="value">The object.</param>
        public override void Serialize(BsonSerializationContext context, TItem[,,] value)
        {
            var bsonWriter = context.Writer;

            if (value == null)
            {
                bsonWriter.WriteNull();
            }
            else
            {
                var length1 = value.GetLength(0);
                var length2 = value.GetLength(1);
                var length3 = value.GetLength(2);

                bsonWriter.WriteStartArray();
                for (int i = 0; i < length1; i++)
                {
                    bsonWriter.WriteStartArray();
                    for (int j = 0; j < length2; j++)
                    {
                        bsonWriter.WriteStartArray();
                        for (int k = 0; k < length3; k++)
                        {
                            context.SerializeWithChildContext(_itemSerializer, value[i, j, k]);
                        }
                        bsonWriter.WriteEndArray();
                    }
                    bsonWriter.WriteEndArray();
                }
                bsonWriter.WriteEndArray();
            }
        }

        /// <summary>
        /// Returns a serializer that has been reconfigured with the specified item serializer.
        /// </summary>
        /// <param name="itemSerializer">The item serializer.</param>
        /// <returns>The reconfigured serializer.</returns>
        public ThreeDimensionalArraySerializer<TItem> WithItemSerializer(IBsonSerializer<TItem> itemSerializer)
        {
            if (itemSerializer == _itemSerializer)
            {
                return this;
            }
            else
            {
                return new ThreeDimensionalArraySerializer<TItem>(itemSerializer);
            }
        }

        // explicit interface implementations
        IBsonSerializer IChildSerializerConfigurable.ChildSerializer
        {
            get { return _itemSerializer; }
        }

        IBsonSerializer IChildSerializerConfigurable.WithChildSerializer(IBsonSerializer childSerializer)
        {
            return WithItemSerializer((IBsonSerializer<TItem>)childSerializer);
        }
    }
}
