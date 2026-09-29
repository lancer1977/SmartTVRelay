namespace SmartTVRelay.Core.Tests.Ingest;

using PolyhydraGames.CaptionExtractor.Ts;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Test-only encoder for a synthetic MPEG-TS byte stream carrying CEA-608 captions embedded in SEI
/// NAL units (PAT + PMT + one H.264 video PES per supplied frame), ported from
/// PolyhydraGames.CaptionExtractor's own test fixtures (TsFixtureBuilder/AnnexBFixtureBuilder) since
/// those are internal to that repo's test assembly and not referenceable from here.
/// </summary>
internal static class EmbeddedCaptionFixtureBuilder
{
    private const int PayloadCapacity = 184;
    private const int PmtPid = 0x100;
    private const int VideoPid = 0x101;

    /// <summary>Builds a single CEA-608 caption's triplets: RCL, one character pair, EOC.</summary>
    public static (bool CcValid, byte CcType, byte Data1, byte Data2)[] Cea608Caption(byte char1, byte char2) =>
    [
        (true, 0, 0x14, 0x20), // RCL
        (true, 0, char1, char2),
        (true, 0, 0x14, 0x2F), // EOC
    ];

    public static byte[] BuildTransportStreamWithCaptions(IReadOnlyList<(long Pts, (bool CcValid, byte CcType, byte Data1, byte Data2)[] Triplets)> frames)
    {
        var tsFrames = frames
            .Select(f => ((long?)f.Pts, BuildGa94SeiNal(f.Triplets)))
            .ToList();
        return BuildTransportStream(tsFrames);
    }

    private static byte[] BuildTransportStream(IReadOnlyList<(long? Pts, byte[] AnnexB)> frames)
    {
        var packets = new List<byte[]> { BuildPatPacket(), BuildPmtPacket() };

        var continuityCounter = 0;
        foreach (var (pts, annexB) in frames)
        {
            packets.AddRange(BuildPesPackets(pts, annexB, ref continuityCounter));
        }

        return packets.SelectMany(static p => p).ToArray();
    }

    private static byte[] BuildTsHeader(int pid, bool payloadUnitStart, int adaptationFieldControl, int continuityCounter) =>
    [
        0x47,
        (byte)(((payloadUnitStart ? 1 : 0) << 6) | ((pid >> 8) & 0x1F)),
        (byte)(pid & 0xFF),
        (byte)((adaptationFieldControl << 4) | (continuityCounter & 0x0F)),
    ];

    private static byte[] BuildSinglePacket(int pid, byte[] psiPayload)
    {
        var payload = new byte[PayloadCapacity];
        Array.Copy(psiPayload, payload, psiPayload.Length);
        Array.Fill(payload, (byte)0xFF, psiPayload.Length, PayloadCapacity - psiPayload.Length);

        return [.. BuildTsHeader(pid, payloadUnitStart: true, adaptationFieldControl: 1, continuityCounter: 0), .. payload];
    }

    private static byte[] BuildPatPacket()
    {
        var section = BuildSection(tableId: 0x00,
        [
            0x00, 0x01,
            0xC1,
            0x00,
            0x00,
            0x00, 0x01,
            (byte)(0xE0 | ((PmtPid >> 8) & 0x1F)), (byte)(PmtPid & 0xFF),
        ]);

        return BuildSinglePacket(PatParser.Pid, [0x00, .. section]);
    }

    private static byte[] BuildPmtPacket()
    {
        var section = BuildSection(tableId: 0x02,
        [
            0x00, 0x01,
            0xC1,
            0x00,
            0x00,
            (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), (byte)(VideoPid & 0xFF),
            0xF0, 0x00,
            PmtParser.H264VideoStreamType,
            (byte)(0xE0 | ((VideoPid >> 8) & 0x1F)), (byte)(VideoPid & 0xFF),
            0xF0, 0x00,
        ]);

        return BuildSinglePacket(PmtPid, [0x00, .. section]);
    }

    private static byte[] BuildSection(byte tableId, byte[] body)
    {
        var sectionLength = body.Length + 4;
        return
        [
            tableId,
            (byte)(0xB0 | ((sectionLength >> 8) & 0x0F)),
            (byte)(sectionLength & 0xFF),
            .. body,
            0x00, 0x00, 0x00, 0x00,
        ];
    }

    private static IEnumerable<byte[]> BuildPesPackets(long? pts, byte[] annexB, ref int continuityCounter)
    {
        var pesBytes = BuildPesHeader(pts).Concat(annexB).ToArray();
        var packets = new List<byte[]>();

        var offset = 0;
        var payloadUnitStart = true;
        while (offset < pesBytes.Length)
        {
            var remaining = pesBytes.Length - offset;
            var chunkSize = Math.Min(PayloadCapacity, remaining);
            var chunk = pesBytes.AsSpan(offset, chunkSize).ToArray();
            offset += chunkSize;

            byte[] payload;
            int adaptationFieldControl;
            if (chunkSize < PayloadCapacity)
            {
                var stuffingLength = PayloadCapacity - chunkSize - 1;
                var adaptationField = new byte[1 + stuffingLength];
                adaptationField[0] = (byte)stuffingLength;
                if (stuffingLength > 0)
                {
                    adaptationField[1] = 0x00;
                    Array.Fill(adaptationField, (byte)0xFF, 2, stuffingLength - 1);
                }

                payload = [.. adaptationField, .. chunk];
                adaptationFieldControl = 3;
            }
            else
            {
                payload = chunk;
                adaptationFieldControl = 1;
            }

            var header = BuildTsHeader(VideoPid, payloadUnitStart, adaptationFieldControl, continuityCounter);
            packets.Add([.. header, .. payload]);

            payloadUnitStart = false;
            continuityCounter = (continuityCounter + 1) & 0x0F;
        }

        return packets;
    }

    private static byte[] BuildPesHeader(long? pts)
    {
        byte ptsDtsFlags = 0;
        byte[] optionalFields = [];
        if (pts.HasValue)
        {
            ptsDtsFlags = 0b10;
            optionalFields = EncodeTimestamp(pts.Value, marker: 0b0010);
        }

        return
        [
            0x00, 0x00, 0x01,
            0xE0,
            0x00, 0x00,
            0x80,
            (byte)(ptsDtsFlags << 6),
            (byte)optionalFields.Length,
            .. optionalFields,
        ];
    }

    private static byte[] EncodeTimestamp(long value, byte marker)
    {
        var bits32To30 = (byte)((value >> 30) & 0x07);
        var bits29To22 = (byte)((value >> 22) & 0xFF);
        var bits21To15 = (byte)((value >> 15) & 0x7F);
        var bits14To7 = (byte)((value >> 7) & 0xFF);
        var bits6To0 = (byte)(value & 0x7F);

        return
        [
            (byte)((marker << 4) | (bits32To30 << 1) | 0x01),
            bits29To22,
            (byte)((bits21To15 << 1) | 0x01),
            bits14To7,
            (byte)((bits6To0 << 1) | 0x01),
        ];
    }

    private static byte[] BuildGa94SeiNal(IReadOnlyList<(bool CcValid, byte CcType, byte Data1, byte Data2)> triplets)
    {
        var atscPayload = new List<byte> { 0xB5, 0x00, 0x31 };
        atscPayload.AddRange("GA94"u8.ToArray());
        atscPayload.Add(0x03);
        atscPayload.Add((byte)(0xA0 | 0x40 | (triplets.Count & 0x1F)));
        atscPayload.Add(0xFF);
        foreach (var (ccValid, ccType, data1, data2) in triplets)
        {
            atscPayload.Add((byte)(0xF8 | (ccValid ? 0x04 : 0) | (ccType & 0x03)));
            atscPayload.Add(data1);
            atscPayload.Add(data2);
        }

        return BuildSeiNalWithPayload(payloadType: 4, atscPayload.ToArray());
    }

    private static byte[] BuildSeiNalWithPayload(int payloadType, byte[] payload)
    {
        var sei = new List<byte>();
        AppendContinuationValue(sei, payloadType);
        AppendContinuationValue(sei, payload.Length);
        sei.AddRange(payload);
        sei.Add(0x80);

        var rbspWithEmulation = InsertEmulationPrevention(sei);

        var nal = new List<byte> { 0x00, 0x00, 0x00, 0x01 };
        nal.Add(0x06);
        nal.AddRange(rbspWithEmulation);
        return nal.ToArray();
    }

    private static void AppendContinuationValue(List<byte> destination, int value)
    {
        while (value >= 255)
        {
            destination.Add(0xFF);
            value -= 255;
        }

        destination.Add((byte)value);
    }

    private static byte[] InsertEmulationPrevention(IReadOnlyList<byte> rbsp)
    {
        var result = new List<byte>(rbsp.Count + 4);
        var zeroRun = 0;
        foreach (var b in rbsp)
        {
            if (zeroRun >= 2 && b <= 0x03)
            {
                result.Add(0x03);
                zeroRun = 0;
            }

            result.Add(b);
            zeroRun = b == 0x00 ? zeroRun + 1 : 0;
        }

        return result.ToArray();
    }
}
