#!/usr/bin/env csharp
// Utility to generate a synthetic MPEG-TS file with SCTE-35 splice markers, encoded to match
// the real ANSI/SCTE 35 + ISO/IEC 13818-1 bit layout byte-for-byte (including the mandatory
// pointer_field byte that real MPEG-TS PSI/private sections always carry -- confirmed present
// in our real captured fixture; see docs/scte35-findings.md for the byte trace that caught an
// earlier version of this generator/parser pair omitting it).
// Usage: dotnet script fixtures/synthetic/generate-scte35-fixture.cs [outputPath]

using System;
using System.Collections.Generic;
using System.IO;

public class Scte35FixtureGenerator
{
    private const byte SYNC_BYTE = 0x47;
    private const int TS_PACKET_SIZE = 188;
    private const int PAT_PID = 0x0000;
    private const int PMT_PID = 0x0100;
    private const int SCTE35_PID = 0x0101;

    public static void Main(string[] args)
    {
        var outputPath = args.Length > 0 ? args[0] : "scte35-sample.ts";
        GenerateFixture(outputPath);
        Console.WriteLine($"Generated synthetic SCTE-35 fixture at {outputPath}");
    }

    public static void GenerateFixture(string outputPath)
    {
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        var writer = new BinaryWriter(fs);

        WritePat(writer);
        WritePmt(writer);

        // CueOut: out_of_network_indicator=1, pts_time=90000 (1.0s at 90kHz).
        WriteSpliceInsert(writer, cueOut: true, ptsTime: 90000);
        // CueIn: out_of_network_indicator=0, pts_time=180000 (2.0s at 90kHz).
        WriteSpliceInsert(writer, cueOut: false, ptsTime: 180000);

        writer.Flush();
    }

    /// <summary>
    /// Builds a section_syntax_indicator + section_length pair as the real 2-byte encoding:
    /// byte0 = 1(SSI) 0(reserved/private) 1 1(reserved) + length's top 4 bits;
    /// byte1 = length's low 8 bits. (12-bit section_length total.)
    /// </summary>
    private static (byte high, byte low) EncodeSectionLength(int sectionLength)
    {
        byte high = (byte)(0xB0 | ((sectionLength >> 8) & 0x0F));
        byte low = (byte)(sectionLength & 0xFF);
        return (high, low);
    }

    private static void WritePat(BinaryWriter writer)
    {
        var body = new List<byte>
        {
            0x00, 0x01,             // transport_stream_id
            0xC1,                   // reserved(11) + version(00000) + current_next_indicator(1)
            0x00,                   // section_number
            0x00,                   // last_section_number
            0x00, 0x01,             // program_number = 1
            (byte)(0xE0 | ((PMT_PID >> 8) & 0x1F)), (byte)(PMT_PID & 0xFF), // reserved(111) + PMT PID
            0x00, 0x00, 0x00, 0x00, // CRC_32 (not validated by the parser; left as zero)
        };

        int sectionLength = body.Count + 1; // +1 for the trailing... actually section_length
        // counts everything AFTER the section_length field itself, i.e. body.Count here is
        // exactly that (transport_stream_id..CRC), so no adjustment needed beyond body.Count.
        sectionLength = body.Count;

        var (lenHigh, lenLow) = EncodeSectionLength(sectionLength);

        var section = new List<byte> { 0x00 /* table_id: PAT */, lenHigh, lenLow };
        section.AddRange(body);

        WriteSection(writer, PAT_PID, section.ToArray());
    }

    private static void WritePmt(BinaryWriter writer)
    {
        var body = new List<byte>
        {
            0x00, 0x01,             // program_number = 1
            0xC1,                   // reserved + version + current_next_indicator
            0x00,                   // section_number
            0x00,                   // last_section_number
            0xE0, 0x00,             // reserved(111) + PCR_PID (unused, 0x000)
            0xF0, 0x00,             // reserved(1111) + program_info_length = 0
            // Elementary stream: SCTE-35 (stream_type 0x86)
            0x86,
            (byte)(0xE0 | ((SCTE35_PID >> 8) & 0x1F)), (byte)(SCTE35_PID & 0xFF),
            0xF0, 0x00,             // reserved(1111) + ES_info_length = 0
            0x00, 0x00, 0x00, 0x00, // CRC_32
        };

        int sectionLength = body.Count;
        var (lenHigh, lenLow) = EncodeSectionLength(sectionLength);

        var section = new List<byte> { 0x02 /* table_id: PMT */, lenHigh, lenLow };
        section.AddRange(body);

        WriteSection(writer, PMT_PID, section.ToArray());
    }

    private static void WriteSpliceInsert(BinaryWriter writer, bool cueOut, uint ptsTime)
    {
        // splice_insert() body.
        var spliceInsert = new List<byte>
        {
            0x00, 0x00, 0x00, 0x01, // splice_event_id
        };

        // splice_event_cancel_indicator(0) + reserved(7): its own byte, per ANSI/SCTE 35 --
        // the flags below are a SEPARATE byte that only exists when cancel_indicator=0.
        spliceInsert.Add(0x00);

        // out_of_network_indicator + program_splice_flag(1) + duration_flag(0) +
        // splice_immediate_flag(0) + reserved(0000).
        byte flagsByte = 0x40; // program_splice_flag=1 (bit6)
        if (cueOut)
        {
            flagsByte |= 0x80; // out_of_network_indicator=1 (bit7)
        }
        spliceInsert.Add(flagsByte);

        // splice_time(): time_specified_flag(1) + reserved(6) + pts_time bit32, then 4 more
        // bytes for the remaining 32 bits of pts_time. No marker bits -- plain 33-bit integer.
        byte b0 = (byte)(0x80 | 0x7E | ((ptsTime >> 32) & 0x01)); // flag=1, reserved=111111, bit32
        spliceInsert.Add(b0);
        spliceInsert.Add((byte)((ptsTime >> 24) & 0xFF));
        spliceInsert.Add((byte)((ptsTime >> 16) & 0xFF));
        spliceInsert.Add((byte)((ptsTime >> 8) & 0xFF));
        spliceInsert.Add((byte)(ptsTime & 0xFF));

        var spliceCommand = new List<byte> { 0x05 /* splice_command_type: splice_insert */ };
        spliceCommand.AddRange(spliceInsert);

        // splice_info_section() body after table_id + section_length.
        var body = new List<byte>
        {
            0x00,                         // protocol_version
        };
        // encrypted_packet(0) encryption_algorithm(000000) + pts_adjustment(33 bits) = 5 bytes,
        // all zero for this prototype (no adjustment).
        body.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00 });
        body.Add(0x00); // cw_index
        // tier(12 bits, reserved as 1s) + splice_command_length(12 bits) = 3 bytes.
        int spliceCommandLength = spliceCommand.Count;
        body.Add((byte)(0xFF)); // tier high byte (all 1s, reserved)
        body.Add((byte)(0xF0 | ((spliceCommandLength >> 8) & 0x0F))); // tier low nibble(1s) + cmd_length high nibble
        body.Add((byte)(spliceCommandLength & 0xFF));                 // cmd_length low byte
        body.AddRange(spliceCommand);
        body.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // CRC_32 (not validated)

        int sectionLength = body.Count;
        var (lenHigh, lenLow) = EncodeSectionLength(sectionLength);

        var section = new List<byte> { 0xFC /* table_id: splice_info_section */, lenHigh, lenLow };
        section.AddRange(body);

        WriteSection(writer, SCTE35_PID, section.ToArray());
    }

    /// <summary>
    /// Wraps a fully-built section in the mandatory leading pointer_field byte (0x00: the
    /// section starts immediately) and writes it as one TS packet with
    /// payload_unit_start_indicator set. Assumes the section (plus pointer_field) fits in a
    /// single 184-byte TS payload, which holds for every fixture section built here.
    /// </summary>
    private static void WriteSection(BinaryWriter writer, int pid, byte[] section)
    {
        var payload = new byte[section.Length + 1];
        payload[0] = 0x00; // pointer_field: section begins immediately after this byte.
        Array.Copy(section, 0, payload, 1, section.Length);

        WriteTransportPacket(writer, pid, payload, payloadUnitStart: true);
    }

    private static void WriteTransportPacket(BinaryWriter writer, int pid, byte[] payload, bool payloadUnitStart)
    {
        var packet = new byte[TS_PACKET_SIZE];
        packet[0] = SYNC_BYTE;

        ushort pidField = (ushort)(pid & 0x1FFF);
        if (payloadUnitStart)
        {
            pidField |= 0x4000;
        }

        packet[1] = (byte)((pidField >> 8) & 0xFF);
        packet[2] = (byte)(pidField & 0xFF);
        packet[3] = 0x10; // adaptation_field_control=01 (payload only), continuity_counter=0

        int payloadStart = 4;
        int payloadSize = Math.Min(payload.Length, TS_PACKET_SIZE - payloadStart);
        Array.Copy(payload, 0, packet, payloadStart, payloadSize);
        // Remaining bytes default to 0x00 stuffing, which is fine: the parser only reads
        // exactly payloadSize bytes worth of real content per accumulation call.

        writer.Write(packet);
    }
}

Scte35FixtureGenerator.Main(args);
