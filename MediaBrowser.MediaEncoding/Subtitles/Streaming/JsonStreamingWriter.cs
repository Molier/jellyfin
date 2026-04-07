#pragma warning disable CS1591

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.MediaInfo;

namespace MediaBrowser.MediaEncoding.Subtitles.Streaming
{
    /// <summary>
    /// Streaming JSON writer for the {"TrackEvents":[...]} shape that
    /// Jellyfin Web's htmlVideoPlayer consumes. Flushes the underlying
    /// stream after each cue so bytes reach the HTTP response body
    /// progressively.
    /// </summary>
    public class JsonStreamingWriter : IStreamingSubtitleWriter
    {
        /// <inheritdoc />
        public async Task WriteAsync(
            IAsyncEnumerable<SubtitleTrackEvent> events,
            Stream destination,
            CancellationToken cancellationToken)
        {
            var options = new JsonWriterOptions { SkipValidation = false };
            await using var writer = new Utf8JsonWriter(destination, options);

            writer.WriteStartObject();
            writer.WriteStartArray("TrackEvents");

            await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                writer.WriteStartObject();
                writer.WriteString("Id", evt.Id);
                writer.WriteString("Text", evt.Text);
                writer.WriteNumber("StartPositionTicks", evt.StartPositionTicks);
                writer.WriteNumber("EndPositionTicks", evt.EndPositionTicks);
                writer.WriteEndObject();

                // Flush the Utf8JsonWriter's internal buffer to the destination
                // stream AND then flush the destination so any Pipe/network
                // layer below sees the bytes.
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
