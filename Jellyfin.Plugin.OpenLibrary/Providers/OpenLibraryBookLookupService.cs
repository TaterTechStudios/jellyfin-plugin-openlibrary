using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenLibrary.Providers
{
    /// <summary>
    /// Looks up book details from OpenLibrary, shared by the book and audiobook metadata providers.
    /// </summary>
    public class OpenLibraryBookLookupService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<OpenLibraryBookLookupService> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenLibraryBookLookupService"/> class.
        /// </summary>
        /// <param name="logger">Instance of the <see cref="ILogger{OpenLibraryBookLookupService}"/> interface.</param>
        /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
        public OpenLibraryBookLookupService(
            ILogger<OpenLibraryBookLookupService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// Looks up book details by title.
        /// </summary>
        /// <param name="title">The title to search for.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The book details, or null if none were found.</returns>
        public async Task<OpenLibraryBookDetails?> LookupAsync(string title, CancellationToken cancellationToken)
        {
            var searchResults = await SearchOpenLibrary(title, cancellationToken).ConfigureAwait(false);
            if (searchResults.Count == 0)
            {
                return null;
            }

            var firstResult = searchResults.First();
            return await GetBookDetails(firstResult, cancellationToken).ConfigureAwait(false);
        }

        private async Task<List<OpenLibrarySearchResult>> SearchOpenLibrary(string title, CancellationToken cancellationToken)
        {
            var searchUrl = $"https://openlibrary.org/search.json?title={HttpUtility.UrlEncode(title)}&limit=5";

            using var httpClient = _httpClientFactory.CreateClient(PluginServiceRegistrator.OpenLibraryHttpClientName);

            try
            {
                var response = await httpClient.GetAsync(searchUrl, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("OpenLibrary search failed with status: {StatusCode} for: {Title}", response.StatusCode, title);
                    return new List<OpenLibrarySearchResult>();
                }

                var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseSearchResults(jsonContent);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogWarning("OpenLibrary search timed out for: {Title}", title);
                return new List<OpenLibrarySearchResult>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching OpenLibrary for: {Title}", title);
                return new List<OpenLibrarySearchResult>();
            }
        }

        private async Task<OpenLibraryBookDetails?> GetBookDetails(OpenLibrarySearchResult searchResult, CancellationToken cancellationToken)
        {
            // Try works endpoint first (what we were using before)
            var worksUrl = $"https://openlibrary.org{searchResult.Key}.json";

            var details = await TryGetBookFromUrl(worksUrl, searchResult.Key, cancellationToken).ConfigureAwait(false);

            // If works didn't have series info and we have a cover edition, try that
            if (details != null && string.IsNullOrEmpty(details.SeriesName) && !string.IsNullOrEmpty(searchResult.CoverEdition))
            {
                var editionUrl = $"https://openlibrary.org/books/{searchResult.CoverEdition}.json";

                var editionDetails = await TryGetBookFromUrl(editionUrl, searchResult.CoverEdition, cancellationToken).ConfigureAwait(false);
                if (editionDetails != null && !string.IsNullOrEmpty(editionDetails.SeriesName))
                {
                    // Merge series info from edition into the works result
                    details.SeriesName = editionDetails.SeriesName;
                }
            }

            // If works failed but we have cover edition, try edition as fallback
            if (details == null && !string.IsNullOrEmpty(searchResult.CoverEdition))
            {
                var editionUrl = $"https://openlibrary.org/books/{searchResult.CoverEdition}.json";
                details = await TryGetBookFromUrl(editionUrl, searchResult.CoverEdition, cancellationToken).ConfigureAwait(false);
            }

            if (details == null)
            {
                return null;
            }

            // Cover images require an edition-level OLID, not a work-level one, so the
            // edition key from the initial search is stored separately for the image providers.
            if (!string.IsNullOrEmpty(searchResult.CoverEdition))
            {
                details.EditionKey = searchResult.CoverEdition;
            }

            // Fetch author bios if we have author keys
            if (searchResult.AuthorKeys.Count > 0)
            {
                foreach (var authorKey in searchResult.AuthorKeys)
                {
                    var authorInfo = await GetAuthorInfo(authorKey, cancellationToken).ConfigureAwait(false);
                    if (authorInfo != null)
                    {
                        details.People.Add(authorInfo);
                    }
                }
            }

            return details;
        }

        private async Task<OpenLibraryBookDetails?> TryGetBookFromUrl(string url, string key, CancellationToken cancellationToken)
        {
            using var httpClient = _httpClientFactory.CreateClient(PluginServiceRegistrator.OpenLibraryHttpClientName);

            try
            {
                var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("OpenLibrary request failed with status: {StatusCode} for: {Key}", response.StatusCode, key);
                    return null;
                }

                var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseBookDetails(jsonContent);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogWarning("OpenLibrary request timed out for: {Key}", key);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting OpenLibrary details for: {Key}", key);
                return null;
            }
        }

        private List<OpenLibrarySearchResult> ParseSearchResults(string jsonContent)
        {
            var results = new List<OpenLibrarySearchResult>();

            try
            {
                using var document = JsonDocument.Parse(jsonContent);
                var root = document.RootElement;

                if (root.TryGetProperty("docs", out var docsElement))
                {
                    foreach (var doc in docsElement.EnumerateArray())
                    {
                        if (doc.TryGetProperty("key", out var keyElement))
                        {
                            var key = keyElement.GetString();
                            if (!string.IsNullOrEmpty(key))
                            {
                                var coverEdition = string.Empty;
                                if (doc.TryGetProperty("cover_edition_key", out var coverEditionElement))
                                {
                                    coverEdition = coverEditionElement.GetString() ?? string.Empty;
                                }

                                var authorKeys = new List<string>();
                                if (doc.TryGetProperty("author_key", out var authorKeyElement))
                                {
                                    if (authorKeyElement.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var authorKey in authorKeyElement.EnumerateArray())
                                        {
                                            var keyValue = authorKey.GetString();
                                            if (!string.IsNullOrEmpty(keyValue))
                                            {
                                                authorKeys.Add(keyValue);
                                            }
                                        }
                                    }
                                }

                                results.Add(new OpenLibrarySearchResult
                                {
                                    Key = key,
                                    CoverEdition = coverEdition,
                                    AuthorKeys = authorKeys
                                });

                                if (results.Count >= 3) // Limit to first 3 results
                                {
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error parsing OpenLibrary search results");
            }

            return results;
        }

        private OpenLibraryBookDetails? ParseBookDetails(string jsonContent)
        {
            try
            {
                using var document = JsonDocument.Parse(jsonContent);
                var root = document.RootElement;

                var details = new OpenLibraryBookDetails();

                // Extract description
                if (root.TryGetProperty("description", out var descElement))
                {
                    var description = ExtractStringOrTextValue(descElement);
                    if (!string.IsNullOrEmpty(description))
                    {
                        details.Overview = description;
                    }
                }

                // Extract series information
                if (root.TryGetProperty("series", out var seriesElement))
                {
                    var series = ExtractStringValue(seriesElement);
                    if (!string.IsNullOrEmpty(series))
                    {
                        details.SeriesName = series;
                    }
                }

                // Extract publication date
                if (root.TryGetProperty("first_publish_date", out var dateElement))
                {
                    var dateStr = dateElement.GetString();
                    if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var date))
                    {
                        details.ProductionYear = date.Year;
                        details.PremiereDate = date;
                    }
                }

                // Extract subjects as genres
                if (root.TryGetProperty("subjects", out var subjectsElement))
                {
                    var genres = ExtractStringArray(subjectsElement);
                    foreach (var genre in genres.Take(5)) // Limit to first 5 genres
                    {
                        details.Genres.Add(genre);
                    }
                }

                return details;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error parsing OpenLibrary book details");
                return null;
            }
        }

        private static string? ExtractStringOrTextValue(JsonElement element)
        {
            // OpenLibrary sometimes stores descriptions as objects with "value" property
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("value", out var valueElement))
                {
                    return valueElement.GetString();
                }
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                return element.GetString();
            }

            return null;
        }

        private static string? ExtractStringValue(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0)
            {
                return element[0].GetString();
            }
            else if (element.ValueKind == JsonValueKind.String)
            {
                return element.GetString();
            }

            return null;
        }

        private static List<string> ExtractStringArray(JsonElement element)
        {
            var result = new List<string>();

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    var value = item.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        result.Add(value);
                    }
                }
            }

            return result;
        }

        private async Task<PersonInfo?> GetAuthorInfo(string authorKey, CancellationToken cancellationToken)
        {
            var authorUrl = $"https://openlibrary.org/authors/{authorKey}.json";
            _logger.LogInformation("Fetching OpenLibrary author info: {Url}", authorUrl);

            using var httpClient = _httpClientFactory.CreateClient(PluginServiceRegistrator.OpenLibraryHttpClientName);

            try
            {
                var response = await httpClient.GetAsync(authorUrl, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("OpenLibrary author request failed with status: {StatusCode} for: {AuthorKey}", response.StatusCode, authorKey);
                    return null;
                }

                var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseAuthorInfo(jsonContent, authorKey);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                _logger.LogWarning("OpenLibrary author request timed out for: {AuthorKey}", authorKey);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting OpenLibrary author info for: {AuthorKey}", authorKey);
                return null;
            }
        }

        private PersonInfo? ParseAuthorInfo(string jsonContent, string authorKey)
        {
            try
            {
                using var document = JsonDocument.Parse(jsonContent);
                var root = document.RootElement;

                var name = string.Empty;
                if (root.TryGetProperty("name", out var nameElement))
                {
                    name = nameElement.GetString() ?? string.Empty;
                }

                if (string.IsNullOrEmpty(name))
                {
                    _logger.LogWarning("No name found for OpenLibrary author: {AuthorKey}", authorKey);
                    return null;
                }

                var personInfo = new PersonInfo
                {
                    Name = name,
                    Type = Jellyfin.Data.Enums.PersonKind.Author
                };

                // Extract biography (PersonInfo doesn't support Overview, but log if found)
                if (root.TryGetProperty("bio", out var bioElement))
                {
                    var bio = ExtractStringOrTextValue(bioElement);
                    if (!string.IsNullOrEmpty(bio))
                    {
                        _logger.LogDebug("Found biography for author {Name} (length: {Length})", name, bio.Length);
                    }
                }

                // Extract birth date
                if (root.TryGetProperty("birth_date", out var birthElement))
                {
                    var birthDate = birthElement.GetString();
                    if (!string.IsNullOrEmpty(birthDate))
                    {
                        _logger.LogDebug("Found birth date for author {Name}: {BirthDate}", name, birthDate);
                    }
                }

                return personInfo;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error parsing OpenLibrary author info for: {AuthorKey}", authorKey);
                return null;
            }
        }

        private sealed class OpenLibrarySearchResult
        {
            public string Key { get; set; } = string.Empty;

            public string CoverEdition { get; set; } = string.Empty;

            public List<string> AuthorKeys { get; set; } = new List<string>();
        }
    }
}
