using System;
using System.Collections.ObjectModel;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.OpenLibrary.Providers
{
    /// <summary>
    /// Book details looked up from OpenLibrary, independent of the target Jellyfin item type.
    /// </summary>
    public class OpenLibraryBookDetails
    {
        /// <summary>
        /// Gets or sets the overview/description.
        /// </summary>
        public string? Overview { get; set; }

        /// <summary>
        /// Gets or sets the series name.
        /// </summary>
        public string? SeriesName { get; set; }

        /// <summary>
        /// Gets or sets the production year.
        /// </summary>
        public int? ProductionYear { get; set; }

        /// <summary>
        /// Gets or sets the premiere date.
        /// </summary>
        public DateTime? PremiereDate { get; set; }

        /// <summary>
        /// Gets the genres.
        /// </summary>
        public Collection<string> Genres { get; } = new Collection<string>();

        /// <summary>
        /// Gets or sets the OpenLibrary edition key, used by the image providers to look up cover art.
        /// </summary>
        public string? EditionKey { get; set; }

        /// <summary>
        /// Gets the author information.
        /// </summary>
        public Collection<PersonInfo> People { get; } = new Collection<PersonInfo>();
    }
}
