﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Replication.Messages;
using Raven.Client.Documents.Smuggler;
using Raven.Client.Documents.Transformers;
using Raven.Client.Util;
using Raven.Server.Config.Settings;
using Raven.Server.Documents;
using Raven.Server.Smuggler.Documents.Data;
using Raven.Server.Smuggler.Documents.Processors;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Collections;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Size = Raven.Server.Config.Settings.Size;

namespace Raven.Server.Smuggler.Documents
{
    public class StreamSource : ISmugglerSource
    {
        private readonly Stream _stream;
        private readonly JsonOperationContext _context;
        private JsonOperationContext.ManagedPinnedBuffer _buffer;
        private JsonOperationContext.ReturnBuffer _returnBuffer;
        private JsonParserState _state;
        private UnmanagedJsonParser _parser;
        private DatabaseItemType? _currentType;

        private SmugglerResult _result;

        private BuildVersionType _buildVersionType;

        private Size _totalObjectsRead = new Size(0, SizeUnit.Bytes);

        public StreamSource(Stream stream, JsonOperationContext context)
        {
            _stream = stream;
            _context = context;
        }

        public IDisposable Initialize(DatabaseSmugglerOptions options, SmugglerResult result, out long buildVersion)
        {
            _result = result;
            _returnBuffer = _context.GetManagedBuffer(out _buffer);
            _state = new JsonParserState();
            _parser = new UnmanagedJsonParser(_context, _state, "file");

            if (UnmanagedJsonParserHelper.Read(_stream, _parser, _state, _buffer) == false)
                ThrowInvalidJson();

            if (_state.CurrentTokenType != JsonParserToken.StartObject)
                ThrowInvalidJson();

            buildVersion = ReadBuildVersion();
            _buildVersionType = BuildVersion.Type(buildVersion);

            return new DisposableAction(() =>
            {
                _parser.Dispose();
                _returnBuffer.Dispose();
            });
        }

        public DatabaseItemType GetNextType()
        {
            if (_currentType != null)
            {
                var currentType = _currentType.Value;
                _currentType = null;

                return currentType;
            }

            var type = ReadType();
            if (type == null)
                return DatabaseItemType.None;

            if (type.Equals("Attachments", StringComparison.OrdinalIgnoreCase))
            {
                SkipArray();
                type = ReadType();
            }

            return GetType(type);
        }

        public long SkipType(DatabaseItemType type)
        {
            switch (type)
            {
                case DatabaseItemType.None:
                    return 0;
                case DatabaseItemType.Documents:
                case DatabaseItemType.RevisionDocuments:
                case DatabaseItemType.Indexes:
                case DatabaseItemType.Transformers:
                case DatabaseItemType.Identities:
                    return SkipArray();
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        }

        public sealed class BlittableMetadataModifier : IDisposable, IBlittableDocumentModifier
        {
            private bool _readingMetadataObject;
            private int _depth;
            private State _state = State.None;
            private bool _verifyStartArray;
            private readonly ChangeVectorReader _changeVectorReader = new ChangeVectorReader();

            private class ChangeVectorReader
            {
                public readonly Dictionary<Guid, long> ChangeVector = new Dictionary<Guid, long>();
                public ChangeVectorReaderState State;
                public Guid DbId;
                public long Etag;

                public void Reset()
                {
                    ChangeVector.Clear();
                    State = ChangeVectorReaderState.StartArray;
                    DbId = Guid.Empty;
                    Etag = 0;
                }
            }

            enum ChangeVectorReaderState
            {
                StartArray,
                StartObject,
                Property,
                DbId,
                Etag
            }

            public BlittableMetadataModifier(JsonOperationContext context)
            {
                _ctx = context;
            }

            public LazyStringValue Id;
            public ChangeVectorEntry[] ChangeVector;
            public DocumentFlags Flags;
            public NonPersistentDocumentFlags NonPersistentFlags;

            private JsonOperationContext _ctx;
            private LazyStringValue _metadataCollections;

            private readonly FastList<AllocatedMemoryData> _allocations = new FastList<AllocatedMemoryData>();

            private const string HistoricalRevisionState = "Historical";
            private const string VersionedDocumentState = "Current";

            private unsafe bool ReadChangeVector(IJsonParser reader, JsonParserState state)
            {
                if (_changeVectorReader.State == ChangeVectorReaderState.StartArray)
                {
                    if (reader.Read() == false)
                    {
                        _state = State.ReadingChangeVector;
                        return false;
                    }

                    if (state.CurrentTokenType != JsonParserToken.StartArray)
                        ThrowInvalidChangeVectorType(state);

                    _changeVectorReader.State = ChangeVectorReaderState.StartObject;
                }

                while (true)
                {
                    if (reader.Read() == false)
                        return false;

                    if (state.CurrentTokenType == JsonParserToken.EndArray)
                    {
                        ChangeVector = _changeVectorReader.ChangeVector.Select(x => new ChangeVectorEntry {DbId = x.Key, Etag = x.Value}).ToArray();
                        _changeVectorReader.Reset();
                        return true;
                    }

                    switch (_changeVectorReader.State)
                    {
                        case ChangeVectorReaderState.StartObject:
                            if (state.CurrentTokenType != JsonParserToken.StartObject)
                                ThrowInvalidChangeVectorType(state);

                            _changeVectorReader.State = ChangeVectorReaderState.Property;
                            break;
                        case ChangeVectorReaderState.Property:
                            if (state.CurrentTokenType == JsonParserToken.EndObject)
                            {
                                _changeVectorReader.ChangeVector[_changeVectorReader.DbId] = _changeVectorReader.Etag;
                                _changeVectorReader.State = ChangeVectorReaderState.StartObject;
                                break;
                            }

                            if (state.CurrentTokenType != JsonParserToken.String || state.StringSize != 4)
                                ThrowInvalidChangeVectorType(state);

                            switch (*(int*)state.StringBuffer)
                            {
                                case 1682530884:
                                    _changeVectorReader.State = ChangeVectorReaderState.DbId;
                                    break;
                                case 1734440005:
                                    _changeVectorReader.State = ChangeVectorReaderState.Etag;
                                    break;
                                default:
                                    ThrowInvalidChangeVectorType(state);
                                    break;
                            }
                            break;
                        case ChangeVectorReaderState.DbId:
                            if (state.CurrentTokenType != JsonParserToken.String)
                                ThrowInvalidChangeVectorType(state);

                            _changeVectorReader.DbId = new Guid(CreateLazyStringValueFromParserState(state));
                            _changeVectorReader.State = ChangeVectorReaderState.Property;
                            break;
                        case ChangeVectorReaderState.Etag:
                            if (state.CurrentTokenType != JsonParserToken.Integer)
                                ThrowInvalidChangeVectorType(state);

                            _changeVectorReader.Etag = state.Long;
                            _changeVectorReader.State = ChangeVectorReaderState.Property;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }

            private DocumentFlags ReadFlags(JsonParserState state)
            {
                var str = CreateLazyStringValueFromParserState(state);
                if (Enum.TryParse(str, true, out DocumentFlags flags) == false)
                    ThrowInvalidFlagsProperty(str);
                return flags;
            }

            private unsafe LazyStringValue CreateLazyStringValueFromParserState(JsonParserState state)
            {
                int escapePositionsCount = state.EscapePositions.Count;

                var maxSizeOfEscapePos = escapePositionsCount * 5 // max size of var int
                                         + JsonParserState.VariableSizeIntSize(escapePositionsCount);

                var mem = _ctx.GetMemory(maxSizeOfEscapePos + state.StringSize);
                _allocations.Add(mem);
                Memory.Copy(mem.Address, state.StringBuffer, state.StringSize);
                var lazyStringValueFromParserState = _ctx.AllocateStringValue(null, mem.Address, state.StringSize);
                if (escapePositionsCount > 0)
                {
                    lazyStringValueFromParserState.EscapePositions = state.EscapePositions.ToArray();
                }
                return lazyStringValueFromParserState;
            }

            private enum State
            {
                None,
                ReadingId,
                ReadingFlags,
                ReadingChangeVector,
                IgnoreProperty,
                IgnoreArray,
                IgnoreRevisionStatusProperty
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void StartObject()
            {
                if (_readingMetadataObject == false)
                    return;

                _depth++;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void EndObject()
            {
                if (_readingMetadataObject == false)
                    return;

                _depth--;

                Debug.Assert(_depth >= 0);
                if (_depth == 0)
                    _readingMetadataObject = false;
            }

            public unsafe bool AboutToReadPropertyName(IJsonParser reader, JsonParserState state)
            {
                switch (_state)
                {
                    case State.None:
                        break;
                    case State.IgnoreProperty:
                        if (reader.Read() == false)
                            return false;

                        if (state.CurrentTokenType == JsonParserToken.StartArray ||
                            state.CurrentTokenType == JsonParserToken.StartObject)
                            ThrowInvalidMetadataProperty(state);
                        break;
                    case State.IgnoreArray:
                        if (_verifyStartArray)
                        {
                            if (reader.Read() == false)
                                return false;

                            _verifyStartArray = false;

                            if (state.CurrentTokenType != JsonParserToken.StartArray)
                                ThrowInvalidReplicationHistoryType(state);
                        }
                        while (state.CurrentTokenType != JsonParserToken.EndArray)
                        {
                            if (reader.Read() == false)
                                return false;
                        }
                        break;
                    case State.IgnoreRevisionStatusProperty:
                        if (reader.Read() == false)
                            return false;

                        if (state.CurrentTokenType != JsonParserToken.String &&
                            state.CurrentTokenType != JsonParserToken.Integer)
                            ThrowInvalidEtagType(state);

                        switch (CreateLazyStringValueFromParserState(state))
                        {
                            case VersionedDocumentState:
                                NonPersistentFlags |= NonPersistentDocumentFlags.LegacyVersioned;
                                break;
                            case HistoricalRevisionState:
                                NonPersistentFlags |= NonPersistentDocumentFlags.LegacyRevision;
                                break;
                        }
                        break;
                    case State.ReadingId:
                        if (reader.Read() == false)
                            return false;

                        if (state.CurrentTokenType != JsonParserToken.String)
                            ThrowExpectedFieldTypeOfString(Constants.Documents.Metadata.Id, state);
                        Id = CreateLazyStringValueFromParserState(state);
                        break;
                    case State.ReadingFlags:
                        if (reader.Read() == false)
                            return false;

                        if (state.CurrentTokenType != JsonParserToken.String)
                            ThrowExpectedFieldTypeOfString(Constants.Documents.Metadata.Flags, state);
                        Flags = ReadFlags(state);
                        break;
                    case State.ReadingChangeVector:
                        if (ReadChangeVector(reader, state) == false)
                        {
                            return false;
                        }

                        break;
                }

                _state = State.None;

                while (true)
                {
                    if (reader.Read() == false)
                        return false;

                    if (state.CurrentTokenType != JsonParserToken.String)
                        return true; // let the caller handle that

                    if (_readingMetadataObject == false)
                    {
                        if (state.StringSize == 9 && state.StringBuffer[0] == (byte)'@' &&
                            *(long*)(state.StringBuffer + 1) == 7022344802737087853)
                        {
                            _readingMetadataObject = true;
                        }
                        return true;
                    }

                    switch (state.StringSize)
                    {
                        case 3: // @id
                            if (state.StringBuffer[0] != (byte)'@' ||
                                *(short*)(state.StringBuffer + 1) != 25705)
                                return true;

                            if (reader.Read() == false)
                            {
                                _state = State.ReadingId;
                                return false;
                            }
                            if (state.CurrentTokenType != JsonParserToken.String)
                                ThrowExpectedFieldTypeOfString(Constants.Documents.Metadata.Id, state);
                            Id = CreateLazyStringValueFromParserState(state);
                            break;
                        case 5: // @etag
                            if (state.StringBuffer[0] != (byte)'@' ||
                                *(int*)(state.StringBuffer + 1) != 1734440037)
                                return true;

                            goto case -1;

                        case 6: // @flags
                            if (state.StringBuffer[0] != (byte)'@' ||
                                *(int*)(state.StringBuffer + 1) != 1734437990 ||
                                state.StringBuffer[1 + sizeof(int)] != (byte)'s')
                                return true;

                            if (reader.Read() == false)
                            {
                                _state = State.ReadingFlags;
                                return false;
                            }
                            if (state.CurrentTokenType != JsonParserToken.String)
                                ThrowExpectedFieldTypeOfString(Constants.Documents.Metadata.Flags, state);
                            Flags = ReadFlags(state);
                            break;

                        case 12: // @index-score
                            if (state.StringBuffer[0] != (byte)'@' ||
                                *(long*)(state.StringBuffer + 1) != 7166121427196997225 ||
                                *(short*)(state.StringBuffer + 1 + sizeof(long)) != 29295 ||
                                state.StringBuffer[1 + sizeof(long) + sizeof(short)] != (byte)'e')
                                return true;

                            goto case -1;
                        case 13: //Last-Modified
                            if (*(long*)state.StringBuffer != 7237087983830262092 ||
                                *(int*)(state.StringBuffer + sizeof(long)) != 1701406313 ||
                                state.StringBuffer[12] != (byte)'d')
                                return true;

                            goto case -1;

                        case 14:
                            if (state.StringBuffer[0] == (byte)'@')
                            {
                                // @change-vector
                                if (*(long*)(state.StringBuffer + 1) == 8515573965335390307 &&
                                    *(int*)(state.StringBuffer + 1 + sizeof(long)) == 1869898597 &&
                                    state.StringBuffer[1 + sizeof(long) + sizeof(int)] == (byte)'r')
                                {
                                    if (ReadChangeVector(reader, state) == false)
                                    {
                                        _state = State.ReadingChangeVector;
                                        return false;
                                    }
                                    break;
                                }

                                // @last-modified
                                if (*(long*)(state.StringBuffer + 1) == 7237123168202350956 &&
                                    *(int*)(state.StringBuffer + 1 + sizeof(long)) == 1701406313 &&
                                    state.StringBuffer[1 + sizeof(long) + sizeof(int)] == (byte)'d')
                                {
                                    goto case -1;
                                }
                            }

                            return true;
                        case 15: //Raven-Read-Only
                            if (*(long*)state.StringBuffer != 7300947898092904786 ||
                                *(int*)(state.StringBuffer + sizeof(long)) != 1328374881 ||
                                *(short*)(state.StringBuffer + sizeof(long) + sizeof(int)) != 27758 ||
                                state.StringBuffer[14] != (byte)'y')
                                return true;

                            goto case -1;

                        case 17: //Raven-Entity-Name --> @collection
                            if (*(long*)state.StringBuffer != 7945807069737017682 ||
                                *(long*)(state.StringBuffer + sizeof(long)) != 7881666780093245812 ||
                                state.StringBuffer[16] != (byte)'e')
                                return true;

                            var collection = _metadataCollections;
                            state.StringBuffer = collection.AllocatedMemoryData.Address;
                            state.StringSize = collection.Size;
                            return true;
                        case 19: //Raven-Last-Modified
                            if (*(long*)state.StringBuffer != 7011028672080929106 ||
                                *(long*)(state.StringBuffer + sizeof(long)) != 7379539893622240371 ||
                                *(short*)(state.StringBuffer + sizeof(long) + sizeof(long)) != 25961 ||
                                state.StringBuffer[18] != (byte)'d')
                                return true;

                            goto case -1;
                        case 23: //Raven-Document-Revision
                            if (*(long*)state.StringBuffer != 8017583188798234962 ||
                                *(long*)(state.StringBuffer + sizeof(long)) != 5921517102558967139 ||
                                *(int*)(state.StringBuffer + sizeof(long) + sizeof(long)) != 1936291429 ||
                                *(short*)(state.StringBuffer + sizeof(long) + sizeof(long) + sizeof(int)) != 28521 ||
                                state.StringBuffer[22] != (byte)'n')
                                return true;

                            goto case -1;
                        case 24: //Raven-Replication-Source
                            if (*(long*)state.StringBuffer != 7300947898092904786 ||
                                *(long*)(state.StringBuffer + sizeof(long)) != 8028075772393122928 ||
                                *(long*)(state.StringBuffer + sizeof(long) + sizeof(long)) != 7305808869229538670)
                                return true;

                            goto case -1;
                        case 25: //Raven-Replication-Version OR Raven-Replication-History
                            if (*(long*)state.StringBuffer != 7300947898092904786 ||
                                *(long*)(state.StringBuffer + sizeof(long)) != 8028075772393122928)
                                return true;

                            var value = *(long*)(state.StringBuffer + sizeof(long) + sizeof(long));
                            var lastByte = state.StringBuffer[24];
                            if ((value != 8028074745928232302 || lastByte != (byte)'n') &&
                                (value != 8245937481775066478 || lastByte != (byte)'y'))
                                return true;

                            var isReplicationHistory = lastByte == (byte)'y';
                            if (reader.Read() == false)
                            {
                                _verifyStartArray = isReplicationHistory;
                                _state = isReplicationHistory ? State.IgnoreArray : State.IgnoreProperty;
                                return false;
                            }

                            // Raven-Replication-History is an array
                            if (isReplicationHistory)
                            {
                                if (state.CurrentTokenType != JsonParserToken.StartArray)
                                    ThrowInvalidReplicationHistoryType(state);

                                do
                                {
                                    if (reader.Read() == false)
                                    {
                                        _state = State.IgnoreArray;
                                        return false;
                                    }
                                } while (state.CurrentTokenType != JsonParserToken.EndArray);
                            }
                            else if (state.CurrentTokenType == JsonParserToken.StartArray ||
                                     state.CurrentTokenType == JsonParserToken.StartObject)
                                ThrowInvalidMetadataProperty(state);
                            break;
                        case 29: //Non-Authoritative-Information
                            if (*(long*)state.StringBuffer != 7526769800038477646 ||
                                *(long*)(state.StringBuffer + sizeof(long)) != 8532478930943832687 ||
                                *(long*)(state.StringBuffer + sizeof(long) + sizeof(long)) != 7886488383206796645 ||
                                *(int*)(state.StringBuffer + sizeof(long) + sizeof(long) + sizeof(long)) != 1869182049 ||
                                state.StringBuffer[28] != (byte)'n')
                                return true;

                            goto case -1;

                        case 30: //Raven-Document-Parent-Revision OR Raven-Document-Revision-Status
                            if (*(long*)state.StringBuffer != 8017583188798234962)
                                return true;

                            if ((*(long*)(state.StringBuffer + sizeof(long)) != 5777401914483111267 ||
                                 *(long*)(state.StringBuffer + sizeof(long) + sizeof(long)) != 7300947924012593761 ||
                                 *(int*)(state.StringBuffer + sizeof(long) + sizeof(long) + sizeof(long)) != 1769171318 ||
                                 *(short*)(state.StringBuffer + sizeof(long) + sizeof(long) + sizeof(long) + sizeof(int)) != 28271) &&
                                (*(long*)(state.StringBuffer + sizeof(long)) != 5921517102558967139 ||
                                 *(long*)(state.StringBuffer + sizeof(long) + sizeof(long)) != 3273676477843469925 ||
                                 *(int*)(state.StringBuffer + sizeof(long) + sizeof(long) + sizeof(long)) != 1952543827 ||
                                 *(short*)(state.StringBuffer + sizeof(long) + sizeof(long) + sizeof(long) + sizeof(int)) != 29557))
                                return true;

                            var isRevisionStatusProperty = state.StringBuffer[29] == 's';
                            if (reader.Read() == false)
                            {
                                _state = isRevisionStatusProperty ? State.IgnoreRevisionStatusProperty : State.IgnoreProperty;
                                return false;
                            }

                            if (state.CurrentTokenType == JsonParserToken.StartArray ||
                                state.CurrentTokenType == JsonParserToken.StartObject)
                                ThrowInvalidMetadataProperty(state);

                            if (isRevisionStatusProperty)
                            {
                                switch (CreateLazyStringValueFromParserState(state))
                                {
                                    case VersionedDocumentState:
                                        NonPersistentFlags |= NonPersistentDocumentFlags.LegacyVersioned;
                                        break;
                                    case HistoricalRevisionState:
                                        NonPersistentFlags |= NonPersistentDocumentFlags.LegacyRevision;
                                        break;
                                }
                            }

                            break;
                        case 32: //Raven-Replication-Merged-History
                            if (*(long*)state.StringBuffer != 7300947898092904786 ||
                                *(long*)(state.StringBuffer + sizeof(long)) != 8028075772393122928 ||
                                *(long*)(state.StringBuffer + sizeof(long) + sizeof(long)) != 7234302117464059246 ||
                                *(long*)(state.StringBuffer + sizeof(long) + sizeof(long) + sizeof(long)) != 8751179571877464109)
                                return true;

                            goto case -1;

                        case -1: // IgnoreProperty
                        {
                            if (reader.Read() == false)
                            {
                                _state = State.IgnoreProperty;
                                return false;
                            }
                            if (state.CurrentTokenType == JsonParserToken.StartArray ||
                                state.CurrentTokenType == JsonParserToken.StartObject)
                                ThrowInvalidMetadataProperty(state);
                            break;
                        }

                        default: // accept this property
                            return true;
                    }
                }
            }

            private static void ThrowInvalidMetadataProperty(JsonParserState state)
            {
                throw new InvalidDataException($"Expected property @metadata to be a simpel type, but was {state.CurrentTokenType}");
            }

            private static void ThrowExpectedFieldTypeOfString(string field, JsonParserState state)
            {
                throw new InvalidDataException($"Expected property @metadata.{field} to have string type, but was: {state.CurrentTokenType}");
            }

            private static void ThrowInvalidFlagsProperty(LazyStringValue str)
            {
                throw new InvalidDataException($"Cannot parse the value of property @metadata.@flags: {str}");
            }

            private static void ThrowInvalidEtagType(JsonParserState state)
            {
                throw new InvalidDataException($"Expected property @metadata.@etag to have string or long type, but was: {state.CurrentTokenType}");
            }

            private static void ThrowInvalidReplicationHistoryType(JsonParserState state)
            {
                throw new InvalidDataException($"Expected property @metadata.Raven-Replication-History to have array type, but was: {state.CurrentTokenType}");
            }

            private static void ThrowInvalidChangeVectorType(JsonParserState state)
            {
                throw new InvalidDataException($"Expected property @metadata.@change-vector to have array type, but was: {state.CurrentTokenType}");
            }

            public void Dispose()
            {
                for (int i = _allocations.Count - 1; i >= 0; i--)
                {
                    _ctx.ReturnMemory(_allocations[i]);
                }
                _allocations.Clear();
            }

            public void Reset(JsonOperationContext ctx)
            {
                if (_ctx == null) // should never happen
                {
                    _ctx = ctx;
                    _metadataCollections = _ctx.GetLazyStringForFieldWithCaching(CollectionName.MetadataCollectionSegment);
                    return;
                }
                Id = null;
                ChangeVector = null;
                _changeVectorReader.Reset();
                Flags = DocumentFlags.None;
                NonPersistentFlags = NonPersistentDocumentFlags.None;
                _depth = 0;
                _state = State.None;
                _readingMetadataObject = false;
                _ctx = ctx;
                _metadataCollections = _ctx.GetLazyStringForFieldWithCaching(CollectionName.MetadataCollectionSegment);
            }
        }

        public IEnumerable<Document> GetDocuments(List<string> collectionsToExport, INewDocumentActions actions)
        {
            return ReadDocuments(actions);
        }

        public IEnumerable<Document> GetRevisionDocuments(List<string> collectionsToExport, INewDocumentActions actions, int limit)
        {
            return ReadDocuments(actions);
        }

        public IEnumerable<IndexDefinitionAndType> GetIndexes()
        {
            foreach (var reader in ReadArray())
            {
                using (reader)
                {
                    IndexType type;
                    object indexDefinition;

                    try
                    {
                        indexDefinition = IndexProcessor.ReadIndexDefinition(reader, _buildVersionType, out type);
                    }
                    catch (Exception e)
                    {
                        _result.Indexes.ErroredCount++;
                        _result.AddWarning($"Could not read index definition. Message: {e.Message}");

                        continue;
                    }

                    yield return new IndexDefinitionAndType
                    {
                        Type = type,
                        IndexDefinition = indexDefinition
                    };
                }
            }
        }

        public IEnumerable<TransformerDefinition> GetTransformers()
        {
            foreach (var reader in ReadArray())
            {
                using (reader)
                {
                    TransformerDefinition transformerDefinition;

                    try
                    {
                        transformerDefinition = TransformerProcessor.ReadTransformerDefinition(reader, _buildVersionType);
                    }
                    catch (Exception e)
                    {
                        _result.Transformers.ErroredCount++;
                        _result.AddWarning($"Could not read transformer definition. Message: {e.Message}");

                        continue;
                    }

                    yield return transformerDefinition;
                }
            }
        }

        public IEnumerable<KeyValuePair<string, long>> GetIdentities()
        {
            foreach (var reader in ReadArray())
            {
                using (reader)
                {
                    string identityKey;
                    string identityValueString;
                    long identityValue;

                    if (reader.TryGet("Key", out identityKey) == false ||
                        reader.TryGet("Value", out identityValueString) == false ||
                        long.TryParse(identityValueString, out identityValue) == false)
                    {
                        _result.Identities.ErroredCount++;
                        _result.AddWarning("Could not read identity.");

                        continue;
                    }

                    yield return new KeyValuePair<string, long>(identityKey, identityValue);
                }
            }
        }

        private unsafe string ReadType()
        {
            if (UnmanagedJsonParserHelper.Read(_stream, _parser, _state, _buffer) == false)
                ThrowInvalidJson();

            if (_state.CurrentTokenType == JsonParserToken.EndObject)
                return null;

            if (_state.CurrentTokenType != JsonParserToken.String)
                ThrowInvalidJson();

            return _context.AllocateStringValue(null, _state.StringBuffer, _state.StringSize).ToString();
        }

        private static void ThrowInvalidJson()
        {
            throw new InvalidOperationException("Invalid JSON.");
        }

        private void ReadObject(BlittableJsonDocumentBuilder builder)
        {
            UnmanagedJsonParserHelper.ReadObject(builder, _stream, _parser, _buffer);

            _totalObjectsRead.Add(builder.SizeInBytes, SizeUnit.Bytes);
        }

        private long ReadBuildVersion()
        {
            var type = ReadType();
            if (type == null)
                return 0;

            if (type.Equals("BuildVersion", StringComparison.OrdinalIgnoreCase) == false)
            {
                _currentType = GetType(type);
                return 0;
            }

            if (UnmanagedJsonParserHelper.Read(_stream, _parser, _state, _buffer) == false)
                ThrowInvalidJson();

            if (_state.CurrentTokenType != JsonParserToken.Integer)
                ThrowInvalidJson();

            return _state.Long;
        }

        private long SkipArray()
        {
            var count = 0L;
            foreach (var builder in ReadArray())
            {
                count++; //skipping
            }

            return count;
        }

        private IEnumerable<BlittableJsonReaderObject> ReadArray(INewDocumentActions actions = null)
        {
            if (UnmanagedJsonParserHelper.Read(_stream, _parser, _state, _buffer) == false)
                ThrowInvalidJson();

            if (_state.CurrentTokenType != JsonParserToken.StartArray)
                ThrowInvalidJson();

            var context = _context;
            var builder = CreateBuilder(_context, null);
            try
            {
                while (true)
                {
                    if (UnmanagedJsonParserHelper.Read(_stream, _parser, _state, _buffer) == false)
                        ThrowInvalidJson();

                    if (_state.CurrentTokenType == JsonParserToken.EndArray)
                        break;
                    if (actions != null)
                    {
                        var oldContext = _context;
                        context = actions.GetContextForNewDocument();
                        if (_context != oldContext)
                        {
                            builder.Dispose();
                            builder = CreateBuilder(context, null);
                        }
                    }
                    builder.Renew("import/object", BlittableJsonDocumentBuilder.UsageMode.ToDisk); ;
                    ReadObject(builder);

                    var reader = builder.CreateReader();
                    builder.Reset();
                    yield return reader;
                }
            }
            finally
            {
                builder.Dispose();
            }
        }

        private IEnumerable<Document> ReadDocuments(INewDocumentActions actions = null)
        {
            if (UnmanagedJsonParserHelper.Read(_stream, _parser, _state, _buffer) == false)
                ThrowInvalidJson();

            if (_state.CurrentTokenType != JsonParserToken.StartArray)
                ThrowInvalidJson();

            var context = _context;
            var modifier = new BlittableMetadataModifier(context);
            var builder = CreateBuilder(context, modifier);
            try
            {
                while (true)
                {
                    if (UnmanagedJsonParserHelper.Read(_stream, _parser, _state, _buffer) == false)
                        ThrowInvalidJson();

                    if (_state.CurrentTokenType == JsonParserToken.EndArray)
                        break;

                    if (actions != null)
                    {
                        var oldContext = context;
                        context = actions.GetContextForNewDocument();
                        if (oldContext != context)
                        {
                            builder.Dispose();
                            builder = CreateBuilder(context, modifier);
                        }
                    }
                    builder.Renew("import/object", BlittableJsonDocumentBuilder.UsageMode.ToDisk);

                    ReadObject(builder);

                    var blittableJsonReaderObject = builder.CreateReader();
                    builder.Reset();
                    yield return new Document
                    {
                        Data = blittableJsonReaderObject,
                        Key = modifier.Id,
                        ChangeVector = modifier.ChangeVector,
                        Flags = modifier.Flags,
                        NonPersistentFlags = modifier.NonPersistentFlags,
                    };
                }
            }
            finally
            {
                builder.Dispose();
            }
        }

        private BlittableJsonDocumentBuilder CreateBuilder(JsonOperationContext context, BlittableMetadataModifier modifier)
        {
            return new BlittableJsonDocumentBuilder(context,
                BlittableJsonDocumentBuilder.UsageMode.ToDisk, "import/object", _parser, _state,
                modifier: modifier);
        }

        private static DatabaseItemType GetType(string type)
        {
            if (type == null)
                return DatabaseItemType.None;

            if (type.Equals("Docs", StringComparison.OrdinalIgnoreCase))
                return DatabaseItemType.Documents;

            if (type.Equals("RevisionDocuments", StringComparison.OrdinalIgnoreCase))
                return DatabaseItemType.RevisionDocuments;

            if (type.Equals("Indexes", StringComparison.OrdinalIgnoreCase))
                return DatabaseItemType.Indexes;

            if (type.Equals("Transformers", StringComparison.OrdinalIgnoreCase))
                return DatabaseItemType.Transformers;

            if (type.Equals("Identities", StringComparison.OrdinalIgnoreCase))
                return DatabaseItemType.Identities;

            throw new InvalidOperationException();
        }
    }
}