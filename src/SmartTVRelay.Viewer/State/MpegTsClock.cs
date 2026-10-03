namespace SmartTVRelay.Viewer.State;

using System;
using System.IO;
using System.Threading;

/// <summary>Helpers for correlating MPEG-TS PCR and SCTE PTS clock values.</summary>
public static class MpegTsClock
{
    private const int PacketLength = 188;
    private const long ClockModulus = 1L << 33;
    private const long ClockMask = ClockModulus - 1;
    private const long HalfClockRange = ClockModulus / 2;
    private const int PtsFrequency = 90_000;

    /// <summary>Finds the last PCR base in a well-formed MPEG-TS file.</summary>
    /// <returns>The 33-bit PCR base in 90 kHz ticks, or <see langword="null"/> if no valid PCR is present.</returns>
    public static long? FindLastPcrBase(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileLength = new FileInfo(filePath).Length;
            if (fileLength == 0 || fileLength % PacketLength != 0)
            {
                return null;
            }

            var packet = new byte[PacketLength];
            long? lastPcrBase = null;
            using var stream = File.OpenRead(filePath);
            for (long offset = 0; offset < fileLength; offset += PacketLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stream.Read(packet, 0, packet.Length) != packet.Length || packet[0] != 0x47)
                {
                    return null;
                }

                var adaptationControl = (packet[3] >> 4) & 0x03;
                if (adaptationControl == 0)
                {
                    return null;
                }

                if (adaptationControl is not (2 or 3))
                {
                    continue;
                }

                var adaptationLength = packet[4];
                if (adaptationLength > PacketLength - 5)
                {
                    return null;
                }

                if (adaptationLength == 0)
                {
                    continue;
                }

                if ((packet[5] & 0x10) == 0)
                {
                    continue;
                }

                if (adaptationLength < 7)
                {
                    return null;
                }

                lastPcrBase = ((long)packet[6] << 25)
                    | ((long)packet[7] << 17)
                    | ((long)packet[8] << 9)
                    | ((long)packet[9] << 1)
                    | ((long)packet[10] >> 7);
            }

            return lastPcrBase;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Returns whether a current 33-bit clock value has reached a target PTS.</summary>
    public static bool HasReached(long currentPcrBase, long targetPts)
    {
        var distance = (currentPcrBase - targetPts) & ClockMask;
        return distance < HalfClockRange;
    }

    /// <summary>Converts the SCTE extractor's UnixEpoch-relative marker time to a 33-bit PTS.</summary>
    public static long ToPts(DateTimeOffset markerObservedAt)
    {
        var elapsedTicks = (decimal)(markerObservedAt - DateTimeOffset.UnixEpoch).Ticks;
        var pts = decimal.Truncate(elapsedTicks * PtsFrequency / TimeSpan.TicksPerSecond);
        var wrapped = pts % ClockModulus;
        return (long)(wrapped < 0 ? wrapped + ClockModulus : wrapped);
    }
}
