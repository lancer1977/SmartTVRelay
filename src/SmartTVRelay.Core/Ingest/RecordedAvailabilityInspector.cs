namespace SmartTVRelay.Core.Ingest;

/// <summary>Runs the complete-file availability checks used by recorded sources.</summary>
internal static class RecordedAvailabilityInspector
{
    public static async Task<(bool? CaptionsAvailable, bool? MarkersAvailable)> InspectAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        if (!IsCompleteMediaFile(filePath, cancellationToken))
        {
            return (null, null);
        }

        bool? captions = null;
        try
        {
            captions = (await new CaptionExtractor()
                .ExtractAsync(filePath, cancellationToken)
                .ConfigureAwait(false)).Count > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // An extractor failure is unknown, not proof that captions are absent.
        }

        bool? markers = null;
        try
        {
            markers = new Scte35MarkerExtractor().Extract(filePath, cancellationToken).Count > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // An extractor failure is unknown, not proof that markers are absent.
        }

        return (captions, markers);
    }

    private static bool IsCompleteMediaFile(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var length = new FileInfo(filePath).Length;
            if (length == 0)
            {
                return false;
            }

            using var stream = File.OpenRead(filePath);
            var first = stream.ReadByte();
            if (first != 0x47)
            {
                // CaptionExtractor supports non-TS containers such as MP4. Require a
                // recognizable MP4/ISO-BMFF signature so arbitrary bytes cannot become a
                // verified "no captions/markers" result.
                stream.Position = 0;
                var header = new byte[8];
                return stream.Read(header, 0, header.Length) == header.Length
                    && header[4] == (byte)'f'
                    && header[5] == (byte)'t'
                    && header[6] == (byte)'y'
                    && header[7] == (byte)'p';
            }

            const int packetSize = 188;
            if (length < packetSize || length % packetSize != 0)
            {
                return false;
            }

            stream.Position = 0;
            var packet = new byte[packetSize];
            for (long offset = 0; offset < length; offset += packetSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stream.Read(packet, 0, packet.Length) != packet.Length || packet[0] != 0x47)
                {
                    return false;
                }
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
