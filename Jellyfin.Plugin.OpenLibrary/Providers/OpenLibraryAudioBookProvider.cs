using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenLibrary.Providers
{
    /// <summary>
    /// OpenLibrary metadata provider for audiobooks.
    /// </summary>
    public class OpenLibraryAudioBookProvider : IRemoteMetadataProvider<AudioBook, SongInfo>
    {
        private readonly OpenLibraryBookLookupService _lookupService;
        private readonly ILogger<OpenLibraryAudioBookProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenLibraryAudioBookProvider"/> class.
        /// </summary>
        /// <param name="logger">Instance of the <see cref="ILogger{OpenLibraryAudioBookProvider}"/> interface.</param>
        /// <param name="lookupService">Instance of the <see cref="OpenLibraryBookLookupService"/> class.</param>
        public OpenLibraryAudioBookProvider(
            ILogger<OpenLibraryAudioBookProvider> logger,
            OpenLibraryBookLookupService lookupService)
        {
            _logger = logger;
            _lookupService = lookupService;
        }

        /// <inheritdoc />
        public string Name => "OpenLibrary";

        /// <inheritdoc />
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SongInfo searchInfo, CancellationToken cancellationToken)
        {
            _logger.LogInformation("OpenLibrary search for: {Title}", searchInfo.Name);
            return Task.FromResult(Enumerable.Empty<RemoteSearchResult>());
        }

        /// <inheritdoc />
        public async Task<MetadataResult<AudioBook>> GetMetadata(SongInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<AudioBook>();

            if (string.IsNullOrWhiteSpace(info.Name))
            {
                return result;
            }

            try
            {
                var details = await _lookupService.LookupAsync(info.Name, cancellationToken).ConfigureAwait(false);
                if (details == null)
                {
                    return result;
                }

                var audioBook = new AudioBook
                {
                    Name = info.Name,
                    Overview = details.Overview,
                    SeriesName = details.SeriesName,
                    ProductionYear = details.ProductionYear,
                    PremiereDate = details.PremiereDate
                };

                foreach (var genre in details.Genres)
                {
                    audioBook.AddGenre(genre);
                }

                if (!string.IsNullOrEmpty(details.EditionKey))
                {
                    audioBook.SetProviderId("OpenLibraryEdition", details.EditionKey);
                }

                result.Item = audioBook;
                result.HasMetadata = true;
                result.People = details.People.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting OpenLibrary metadata for {Title}", info.Name);
            }

            return result;
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            throw new NotImplementedException("OpenLibrary provider does not support image retrieval");
        }
    }
}
