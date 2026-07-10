using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
    /// OpenLibrary image provider for books.
    /// </summary>
    public class OpenLibraryBookImageProvider : IRemoteImageProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<OpenLibraryBookImageProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenLibraryBookImageProvider"/> class.
        /// </summary>
        /// <param name="logger">Instance of the <see cref="ILogger{OpenLibraryBookImageProvider}"/> interface.</param>
        /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
        public OpenLibraryBookImageProvider(
            ILogger<OpenLibraryBookImageProvider> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        /// <inheritdoc />
        public string Name => "OpenLibrary";

        /// <inheritdoc />
        public bool Supports(BaseItem item) => item is Book;

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[] { ImageType.Primary };

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var editionKey = item.GetProviderId("OpenLibraryEdition");
            if (string.IsNullOrEmpty(editionKey))
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var coverId = await GetCoverId(editionKey, cancellationToken).ConfigureAwait(false);
            if (coverId == null)
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            return new[]
            {
                new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg",
                    Type = ImageType.Primary
                }
            };
        }

        /// <inheritdoc />
        public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            using var httpClient = _httpClientFactory.CreateClient(PluginServiceRegistrator.OpenLibraryHttpClientName);
            return await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }

        private async Task<long?> GetCoverId(string editionKey, CancellationToken cancellationToken)
        {
            // The olid cover endpoint returns a blank placeholder image with a 200 status
            // when an edition has no cover, so the edition record's "covers" array has to
            // be checked directly rather than trusting the cover URL to 404.
            var editionUrl = $"https://openlibrary.org/books/{editionKey}.json";

            using var httpClient = _httpClientFactory.CreateClient(PluginServiceRegistrator.OpenLibraryHttpClientName);

            try
            {
                var response = await httpClient.GetAsync(editionUrl, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("OpenLibrary edition lookup failed with status: {StatusCode} for: {EditionKey}", response.StatusCode, editionKey);
                    return null;
                }

                var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(jsonContent);
                var root = document.RootElement;

                if (root.TryGetProperty("covers", out var coversElement) && coversElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cover in coversElement.EnumerateArray())
                    {
                        if (cover.ValueKind == JsonValueKind.Number && cover.TryGetInt64(out var id) && id > 0)
                        {
                            return id;
                        }
                    }
                }

                return null;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogWarning("OpenLibrary edition cover lookup timed out for: {EditionKey}", editionKey);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting OpenLibrary edition covers for: {EditionKey}", editionKey);
                return null;
            }
        }
    }
}
