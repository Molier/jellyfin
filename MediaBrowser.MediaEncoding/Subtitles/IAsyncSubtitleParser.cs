#pragma warning disable CS1591

using System.Collections.Generic;
using System.IO;
using System.Threading;
using MediaBrowser.Model.MediaInfo;

namespace MediaBrowser.MediaEncoding.Subtitles
{
    /// <summary>
    /// Asynchronous incremental subtitle parser. Implementations emit
    /// <see cref="SubtitleTrackEvent"/> instances as they are parsed from the
    /// input stream, allowing downstream writers to start producing output
    /// before the whole input has been read.
    /// </summary>
    public interface IAsyncSubtitleParser
    {
        /// <summary>
        /// Parses the specified stream incrementally.
        /// </summary>
        /// <param name="stream">Input subtitle stream.</param>
        /// <param name="fileExtension">File extension / format identifier.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An async enumerable of subtitle track events.</returns>
        IAsyncEnumerable<SubtitleTrackEvent> ParseAsync(
            Stream stream,
            string fileExtension,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Determines whether the file extension is supported by this parser.
        /// </summary>
        /// <param name="fileExtension">File extension / format identifier.</param>
        /// <returns>True if supported.</returns>
        bool SupportsFileExtension(string fileExtension);
    }
}
