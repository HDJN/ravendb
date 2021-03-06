using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Raven.Abstractions.Data;
using Raven.Abstractions.Database.Smuggler.Database;
using Raven.Abstractions.Exceptions;
using Raven.Abstractions.Indexing;
using Raven.Abstractions.Util;
using Raven.Json.Linq;

namespace Raven.Smuggler.Database
{
    public interface IDatabaseSmugglerSource : IDisposable
    {
        string DisplayName { get; }

        bool SupportsMultipleSources { get; }

        IReadOnlyList<IDatabaseSmugglerSource> Sources { get; }

        Task InitializeAsync(DatabaseSmugglerOptions options, CancellationToken cancellationToken);

        Task<List<IndexDefinition>> ReadIndexesAsync(int start, int pageSize, CancellationToken cancellationToken);

        Task<DatabaseLastEtagsInfo> FetchCurrentMaxEtagsAsync(CancellationToken cancellationToken);

        Task<IAsyncEnumerator<RavenJObject>> ReadDocumentsAfterAsync(Etag afterEtag, int pageSize, CancellationToken cancellationToken);

        Task<RavenJObject> ReadDocumentAsync(string key, CancellationToken cancellationToken);

        bool SupportsReadingHiLoDocuments { get; }

        bool SupportsDocumentDeletions { get; }

        bool SupportsPaging { get; }

        bool SupportsRetries { get; }

        Task<List<TransformerDefinition>> ReadTransformersAsync(int start, int pageSize, CancellationToken cancellationToken);

        Task<List<KeyValuePair<string, Etag>>> ReadDocumentDeletionsAsync(Etag fromEtag, Etag maxEtag, CancellationToken cancellationToken);

        Task<List<KeyValuePair<string, long>>> ReadIdentitiesAsync(CancellationToken cancellationToken);

        Task<SmuggleType> GetNextSmuggleTypeAsync(CancellationToken cancellationToken);

        Task SkipDocumentsAsync(CancellationToken cancellationToken);

        Task SkipIndexesAsync(CancellationToken cancellationToken);

        Task SkipTransformersAsync(CancellationToken cancellationToken);

        Task SkipDocumentDeletionsAsync(CancellationToken cancellationToken);

        Task SkipIdentitiesAsync(CancellationToken cancellationToken);

        Task SkipAttachmentsAsync(CancellationToken cancellationToken);

        Task SkipAttachmentDeletionsAsync(CancellationToken cancellationToken);

        Task AfterExecuteAsync(DatabaseSmugglerOperationState state);

        void OnException(SmugglerException exception);
    }
}
