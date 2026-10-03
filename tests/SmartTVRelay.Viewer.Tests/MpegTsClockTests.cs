using SmartTVRelay.Viewer.State;

namespace SmartTVRelay.Viewer.Tests;

public sealed class MpegTsClockTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "smarttv-mpegts-clock-" + Guid.NewGuid().ToString("N"));

    public MpegTsClockTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void FindsLastPcrBase()
    {
        var path = WritePackets(PacketWithPcr(123), PacketWithPcr((1L << 33) - 7));

        Assert.Equal((1L << 33) - 7, MpegTsClock.FindLastPcrBase(path));
    }

    [Fact]
    public void MalformedAndNoPcrReturnNull()
    {
        Assert.Null(MpegTsClock.FindLastPcrBase(WriteBytes(new byte[187])));
        Assert.Null(MpegTsClock.FindLastPcrBase(WriteBytes(PacketWithoutPcr())));

        var malformed = PacketWithoutPcr();
        malformed[0] = 0;
        Assert.Null(MpegTsClock.FindLastPcrBase(WriteBytes(malformed)));
    }

    [Fact]
    public void CancellationIsObserved()
    {
        var path = WritePackets(PacketWithoutPcr());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => MpegTsClock.FindLastPcrBase(path, cts.Token));
    }

    [Fact]
    public void HasReachedHandlesWrapAround()
    {
        Assert.True(MpegTsClock.HasReached(2, (1L << 33) - 3));
        Assert.False(MpegTsClock.HasReached((1L << 33) - 3, 2));
        Assert.True(MpegTsClock.HasReached(100, 100));
    }

    [Fact]
    public void DistinguishesFutureCueFromDueCue()
    {
        Assert.True(MpegTsClock.HasReached(90_000, 89_000));
        Assert.False(MpegTsClock.HasReached(90_000, 91_000));
    }

    [Fact]
    public void ToPtsRoundTripsUnixEpochRepresentation()
    {
        var marker = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(123.456);

        Assert.Equal(11_111_040, MpegTsClock.ToPts(marker));
        Assert.Equal(marker, DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(MpegTsClock.ToPts(marker) / 90_000d));
    }

    private string WritePackets(params byte[][] packets) => WriteBytes(packets.SelectMany(static p => p).ToArray());

    private string WriteBytes(byte[] bytes)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".ts");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] PacketWithoutPcr()
    {
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[3] = 0x10;
        return packet;
    }

    private static byte[] PacketWithPcr(long pcrBase)
    {
        var packet = PacketWithoutPcr();
        packet[3] = 0x30;
        packet[4] = 7;
        packet[5] = 0x10;
        packet[6] = (byte)(pcrBase >> 25);
        packet[7] = (byte)(pcrBase >> 17);
        packet[8] = (byte)(pcrBase >> 9);
        packet[9] = (byte)(pcrBase >> 1);
        packet[10] = (byte)(pcrBase << 7);
        packet[11] = 0;
        return packet;
    }
}
