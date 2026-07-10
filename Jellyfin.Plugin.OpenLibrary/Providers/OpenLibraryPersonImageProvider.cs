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
    /// OpenLibrary image provider for authors.
    /// </summary>
    public class OpenLibraryPersonImageProvider : IRemoteImageProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<OpenLibraryPersonImageProvider> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenLibraryPersonImageProvider"/> class.
        /// </summary>
        /// <param name="logger">Instance of the <see cref="ILogger{OpenLibraryPersonImageProvider}"/> interface.</param>
        /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
        public OpenLibraryPersonImageProvider(
            ILogger<OpenLibraryPersonImageProvider> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        /// <inheritdoc />
        public string Name => "OpenLibrary";

        /// <inheritdoc />
        public bool Supports(BaseItem item) => item is Person;

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[] { ImageType.Primary };

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var openLibraryId = item.GetProviderId("OpenLibrary");
            if (string.IsNullOrEmpty(openLibraryId))
            {
                return Enumerable.Empty<RemoteImageInfo>();
            }

            var photoIds = await GetPhotoIds(openLibraryId, cancellationToken).ConfigureAwait(false);

            return photoIds.Select(photoId => new RemoteImageInfo
            {
                ProviderName = Name,
                Url = $"https://covers.openlibrary.org/a/id/{photoId}-L.jpg",
                Type = ImageType.Primary
            });
        }

        /// <inheritdoc />
        public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            using var httpClient = _httpClientFactory.CreateClient(PluginServiceRegistrator.OpenLibraryHttpClientName);
            return await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }

        private async Task<List<long>> GetPhotoIds(string authorKey, CancellationToken cancellationToken)
        {
            // OpenLibrary's olid cover endpoint returns a blank placeholder image with a
            // 200 status when an author has no photo, so the author record's "photos" array
            // has to be checked directly rather than trusting the cover URL to 404.
            var authorUrl = $"https://openlibrary.org/authors/{authorKey}.json";

            using var httpClient = _httpClientFactory.CreateClient(PluginServiceRegistrator.OpenLibraryHttpClientName);
            var photoIds = new List<long>();

            try
            {
                var response = await httpClient.GetAsync(authorUrl, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("OpenLibrary author lookup failed with status: {StatusCode} for: {AuthorKey}", response.StatusCode, authorKey);
                    return photoIds;
                }

                var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(jsonContent);
                var root = document.RootElement;

                if (root.TryGetProperty("photos", out var photosElement) && photosElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var photo in photosElement.EnumerateArray())
                    {
                        if (photo.ValueKind == JsonValueKind.Number && photo.TryGetInt64(out var id) && id > 0)
                        {
                            photoIds.Add(id);
                        }
                    }
                }

                return photoIds;
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogWarning("OpenLibrary author photo lookup timed out for: {AuthorKey}", authorKey);
                return photoIds;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting OpenLibrary author photos for: {AuthorKey}", authorKey);
                return photoIds;
            }
        }
    }
}
