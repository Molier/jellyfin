#pragma warning disable CS1591

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.MediaInfo;

namespace MediaBrowser.MediaEncoding.Subtitles.Streaming
{
    /// <summary>
    /// Streaming subtitle writer. Consumes an async enumerable of
    /// <see cref="SubtitleTrackEvent"/> and writes output to a destination
    /// stream, flushing after each cue so that progressive HTTP chunked
    /// transfer encoding delivers cues to the client as soon as they are
    /// produced by the upstream parser.
    /// </summary>
    public interface IStreamingSubtitleWriter
    {
        /// <summary>
        /// Writes the events to the destination stream incrementally.
        /// Must flush the destination stream after each cue.
        /// </summary>
        /// <param name="events">Event source.</param>
        /// <param name="destination">Destination stream.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        Task WriteAsync(
            IAsyncEnumerable<SubtitleTrackEvent> events,
            Stream destination,
            CancellationToken cancellationToken);
    }
}
