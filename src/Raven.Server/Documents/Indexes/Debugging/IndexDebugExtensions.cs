﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Client.Extensions;
using Raven.Client.Util;
using Raven.Server.Documents.Indexes.Auto;
using Raven.Server.Documents.Indexes.MapReduce;
using Raven.Server.Documents.Indexes.MapReduce.Auto;
using Raven.Server.Documents.Indexes.MapReduce.Static;
using Raven.Server.Documents.Indexes.Static;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.Results;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Binary;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Voron;
using Voron.Data;
using Voron.Data.BTrees;
using Voron.Data.Compression;
using Voron.Data.Fixed;
using Voron.Data.Tables;

namespace Raven.Server.Documents.Indexes.Debugging
{
    public static class IndexDebugExtensions
    {
        public static IDisposable GetIdentifiersOfMappedDocuments(this Index self, string startsWith, int start, int take, out IEnumerable<string> docIds)
        {
            if (self.Type.IsMapReduce() == false)
                throw new NotSupportedException("Getting doc ids for map indexes is not supported");

            using (var scope = new DisposeableScope())
            {
                TransactionOperationContext indexContext;
                scope.EnsureDispose(self._contextPool.AllocateOperationContext(out indexContext));

                RavenTransaction tx;
                scope.EnsureDispose(tx = indexContext.OpenReadTransaction());

                var tree = tx.InnerTransaction.ReadTree(MapReduceIndexBase<MapReduceIndexDefinition>.MapPhaseTreeName);

                if (tree == null)
                {
                    docIds = Enumerable.Empty<string>();
                    return scope;
                }

                TreeIterator it;
                scope.EnsureDispose(it = tree.Iterate(false));

                docIds = IterateKeys(it, startsWith, start, take, indexContext);

                return scope.Delay();
            }
        }

        private static IEnumerable<string> IterateKeys(IIterator it, string prefix, int start, int take, TransactionOperationContext context)
        {
            if (it.Seek(Slices.BeforeAllKeys) == false)
                yield break;

            ByteStringContext<ByteStringMemoryCache>.InternalScope? scope = null;
            try
            {
                if (string.IsNullOrEmpty(prefix) == false)
                {
                    if (SetupPrefix(it, prefix, context, out scope) == false)
                        yield break;
                }
                else if (it.Seek(MapReduceIndexingContext.LastMapResultIdKey))
                {
                    if (it.MoveNext() == false)
                        yield break;
                }

                do
                {
                    if (start > 0)
                    {
                        start--;
                        continue;
                    }

                    if (--take < 0)
                        yield break;

                    yield return it.CurrentKey.ToString();
                } while (it.MoveNext());
            }
            finally
            {
                if (scope != null)
                    scope.Value.Dispose();
            }
        }

        private static bool SetupPrefix(IIterator it, string prefix, TransactionOperationContext context,
            out ByteStringContext<ByteStringMemoryCache>.InternalScope? scope)
        {
            Slice prefixSlice;
            scope = Slice.From(context.Transaction.InnerTransaction.Allocator, prefix, out prefixSlice);

            it.SetRequiredPrefix(prefixSlice);

            if (it.Seek(prefixSlice))
                return true;

            scope.Value.Dispose();
            scope = null;

            it.SetRequiredPrefix(Slices.Empty);

            if (it.Seek(Slices.BeforeAllKeys) == false)
                return false;

            if (SliceComparer.Compare(it.CurrentKey, MapReduceIndexingContext.LastMapResultIdKey) == 0)
            {
                if (it.MoveNext() == false)
                    return false;
            }
            var firstKey = it.CurrentKey.ToString();
            if (it.Seek(Slices.AfterAllKeys) == false)
                return false;
            var lastKey = it.CurrentKey.ToString();

            int index = -1;
            for (int i = 0; i < Math.Min(firstKey.Length, lastKey.Length); i++)
            {
                if (firstKey[i] != lastKey[i])
                {
                    break;
                }
                index = i;
            }
            if (index == -1)
                return false;

            prefix = firstKey.Substring(0, index + 1) + prefix;

            scope = Slice.From(context.Transaction.InnerTransaction.Allocator, prefix, out prefixSlice);

            it.SetRequiredPrefix(prefixSlice);

            if (it.Seek(prefixSlice) == false)
            {
                scope.Value.Dispose();
                scope = null;
                return false;
            }
            return true;
        }

        public static IDisposable GetReduceTree(this Index self, string[] docIds, out IEnumerable<ReduceTree> trees)
        {
            using (var scope = new DisposeableScope())
            {
                TransactionOperationContext indexContext;
                scope.EnsureDispose(self._contextPool.AllocateOperationContext(out indexContext));

                RavenTransaction tx;
                scope.EnsureDispose(tx = indexContext.OpenReadTransaction());

                var mapPhaseTree = tx.InnerTransaction.ReadTree(MapReduceIndexBase<MapReduceIndexDefinition>.MapPhaseTreeName);

                if (mapPhaseTree == null)
                {
                    trees = Enumerable.Empty<ReduceTree>();
                    return scope;
                }

                var reducePhaseTree = tx.InnerTransaction.ReadTree(MapReduceIndexBase<MapReduceIndexDefinition>.ReducePhaseTreeName);

                if (reducePhaseTree == null)
                {
                    trees = Enumerable.Empty<ReduceTree>();
                    return scope;
                }

                var mapEntries = new List<FixedSizeTree>(docIds.Length);
                foreach (var docId in docIds)
                {
                    FixedSizeTree mapEntriesTree;
                    scope.EnsureDispose(mapEntriesTree = mapPhaseTree.FixedTreeFor(docId, sizeof(long)));
                    mapEntries.Add(mapEntriesTree);
                }

                FixedSizeTree typePerHash;
                scope.EnsureDispose(typePerHash = reducePhaseTree.FixedTreeFor(MapReduceIndexBase<MapReduceIndexDefinition>.ResultsStoreTypesTreeName, sizeof(byte)));

                trees = IterateTrees(self, mapEntries, reducePhaseTree, typePerHash, indexContext, scope);

                return scope.Delay();
            }
        }

        private static IEnumerable<ReduceTree> IterateTrees(Index self, List<FixedSizeTree> mapEntries,
            Tree reducePhaseTree, FixedSizeTree typePerHash, TransactionOperationContext indexContext, DisposeableScope scope)
        {
            var reduceKeys = new HashSet<ulong>();
            var idToDocIdHash = new Dictionary<long, string>();

            foreach (var tree in mapEntries)
            foreach (var mapEntry in MapReduceIndexBase<MapReduceIndexDefinition>.GetMapEntries(tree))
            {
                reduceKeys.Add(mapEntry.ReduceKeyHash);
                idToDocIdHash[mapEntry.Id] = tree.Name.ToString();
            }

            foreach (var reduceKeyHash in reduceKeys)
            {
                MapReduceResultsStore store;

                var mapReduceIndex = self as MapReduceIndex;

                if (mapReduceIndex != null)
                    store = mapReduceIndex.CreateResultsStore(typePerHash,
                        reduceKeyHash, indexContext, false);
                else
                    store = ((AutoMapReduceIndex)self).CreateResultsStore(typePerHash,
                        reduceKeyHash, indexContext, false);

                using (store)
                {
                    ReduceTree tree;
                    switch (store.Type)
                    {
                        case MapResultsStorageType.Tree:
                            tree = RenderTree(store.Tree, reduceKeyHash, idToDocIdHash, self, indexContext);
                            break;
                        case MapResultsStorageType.Nested:
                            tree = RenderNestedSection(store.GetNestedResultsSection(reducePhaseTree), reduceKeyHash, idToDocIdHash, self,
                                indexContext);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(store.Type.ToString());
                    }

                    scope.EnsureDispose(tree);
                    yield return tree;
                }
            }
        }

        private static unsafe ReduceTree RenderTree(Tree tree, ulong reduceKeyHash, Dictionary<long, string> idToDocIdHash, Index index, TransactionOperationContext context)
        {
            var stack = new Stack<ReduceTreePage>();
            var rootPage = tree.GetReadOnlyTreePage(tree.State.RootPageNumber);

            var root = new ReduceTreePage(rootPage);

            root.AggregationResult = GetReduceResult(reduceKeyHash, index, context);

            stack.Push(root);

            var table =
                context.Transaction.InnerTransaction.OpenTable(
                    ReduceMapResultsBase<MapReduceIndexDefinition>.ReduceResultsSchema,
                    ReduceMapResultsBase<MapReduceIndexDefinition>.PageNumberToReduceResultTableName);

            var tx = tree.Llt;
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                var page = node.Page;

                if (page.IsCompressed)
                {
                    var decompressed = tree.DecompressPage(page, DecompressionUsage.Read, true);

                    node.DecompressedLeaf = decompressed;
                    page = decompressed;
                }

                if (page.NumberOfEntries == 0 && page != rootPage)
                    throw new InvalidOperationException($"The page {page.PageNumber} is empty");

                for (var i = 0; i < page.NumberOfEntries; i++)
                {
                    if (page.IsBranch)
                    {
                        var p = page.GetNode(i)->PageNumber;

                        var childNode = new ReduceTreePage(tree.GetReadOnlyTreePage(p));

                        node.Children.Add(childNode);

                        stack.Push(childNode);
                    }
                    else
                    {
                        var entry = new MapResultInLeaf();

                        var valueReader = TreeNodeHeader.Reader(tx, page.GetNode(i));
                        entry.Data = new BlittableJsonReaderObject(valueReader.Base, valueReader.Length, context);

                        Slice s;
                        using (page.GetNodeKey(tx, i, out s))
                        {
                            var mapEntryId = Bits.SwapBytes(*(long*)s.Content.Ptr);

                            string docId;
                            if (idToDocIdHash.TryGetValue(mapEntryId, out docId)) 
                                entry.Source = docId;
                        }

                        node.Entries.Add(entry);
                    }
                }

                if (node != root)
                    node.AggregationResult = GetAggregationResult(node.PageNumber, table, context);
            }

            return new ReduceTree
            {
                DisplayName = GetTreeName(root.AggregationResult, index.Definition, context),
                Name = tree.Name.ToString(),
                Root = root,
                Depth = tree.State.Depth,
                PageCount = tree.State.PageCount,
                NumberOfEntries = tree.State.NumberOfEntries
            };
        }

        private static string GetTreeName(BlittableJsonReaderObject reduceEntry, IndexDefinitionBase indexDefinition, TransactionOperationContext context)
        {
            HashSet<string> groupByFields;

            if (indexDefinition is MapReduceIndexDefinition)
                groupByFields = ((MapReduceIndexDefinition)indexDefinition).GroupByFields;
            else if (indexDefinition is AutoMapReduceIndexDefinition)
                groupByFields = ((AutoMapReduceIndexDefinition)indexDefinition).GroupByFields.Keys.ToHashSet();
            else
                throw new InvalidOperationException("Invalid map reduce index definition: " + indexDefinition.GetType());

            foreach (var prop in reduceEntry.GetPropertyNames())
            {
                if (groupByFields.Contains(prop))
                    continue;

                if (reduceEntry.Modifications == null)
                    reduceEntry.Modifications = new DynamicJsonValue(reduceEntry);

                reduceEntry.Modifications.Remove(prop);
            }

            var reduceKey = context.ReadObject(reduceEntry, "debug: creating reduce tree name");

            return reduceKey.ToString();
        }

        private static unsafe BlittableJsonReaderObject GetAggregationResult(long pageNumber, Table table, TransactionOperationContext context)
        {
            var tmp = Bits.SwapBytes(pageNumber);

            Slice pageNumberSlice;
            using (Slice.External(context.Allocator, (byte*)&tmp, sizeof(long), out pageNumberSlice))
            {
                TableValueReader tvr;
                table.ReadByKey(pageNumberSlice, out tvr);

                int size;
                return new BlittableJsonReaderObject(tvr.Read(3, out size), size, context);
            }
        }

        private static ReduceTree RenderNestedSection(NestedMapResultsSection section, ulong reduceKeyHash, Dictionary<long, string> idToDocIdHash, Index index, TransactionOperationContext context)
        {
            var entries = new Dictionary<long, BlittableJsonReaderObject>();

            var root = new ReduceTreePage(section.RelevantPage);

            root.AggregationResult = GetReduceResult(reduceKeyHash, index, context);

            section.GetResultsForDebug(context, entries);

            foreach (var item in entries)
            {
                var entry = new MapResultInLeaf
                {
                    Data = item.Value
                };

                var id = Bits.SwapBytes(item.Key);

                string docId;
                if (idToDocIdHash.TryGetValue(id, out docId))
                    entry.Source = docId;

                root.Entries.Add(entry);
            }

            return new ReduceTree
            {
                DisplayName = GetTreeName(root.AggregationResult, index.Definition, context),
                Name = section.Name.ToString(),
                Root = root,
                Depth = 1,
                PageCount = 1,
                NumberOfEntries = entries.Count
            };
        }

        private static BlittableJsonReaderObject GetReduceResult(ulong reduceKeyHash, Index index, TransactionOperationContext context)
        {
            using (var reader = index.IndexPersistence.OpenIndexReader(context.Transaction.InnerTransaction))
            {
                var query = new IndexQueryServerSide
                {
                    Query = $"{Constants.Documents.Indexing.Fields.ReduceKeyFieldName}:{reduceKeyHash}"
                };

                var fieldsToFetch = new FieldsToFetch(query, index.Definition, null);

                var result = reader.Query(query, fieldsToFetch, new Reference<int>(), new Reference<int>(),
                    new MapReduceQueryResultRetriever(context, fieldsToFetch), CancellationToken.None).ToList();

                if (result.Count != 1)
                    throw new InvalidOperationException("Cannot have multiple reduce results for a single reduce key");

                return result[0].Data;
            }
        }

        public static string[] GetEntriesFields(this Index self)
        {
            switch (self.Type)
            {
                case IndexType.Map:
                    return ((MapIndex)self)._compiled.OutputFields;
                case IndexType.MapReduce:
                    return ((MapReduceIndex)self).Compiled.OutputFields;
                case IndexType.AutoMap:
                    return ((AutoMapIndex)self).Definition.MapFields.Keys.ToArray();
                case IndexType.AutoMapReduce:
                    return ((AutoMapReduceIndex)self).Definition.GroupByFields.Keys.ToArray();

                default:
                    throw new ArgumentException("Unknown index type");
            }
        }
    }
}