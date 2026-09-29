namespace SmartTVRelay.Core.Ingest;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

/// <summary>
/// Extracts SCTE-35 splice_insert commands from MPEG-TS transport stream files.
/// Parses the PAT to locate the PMT, then the PMT to find SCTE-35 streams (stream_type 0x86),
/// and finally extracts splice_insert commands as timestamped ExplicitMarkerSignal values.
///
/// Supports splice_insert (command_type 0x05) only. Other splice_command_types are safely ignored.
/// No CRC validation, no encrypted_packet support, no component-splice-per-PID variants, no
/// duration/avail descriptor fields. Immediate splices (splice_immediate_flag=1), and any
/// splice_time() with time_specified_flag=0, carry no reliable timestamp in the section at all.
/// Since this class only parses recorded files, DateTimeOffset.UtcNow at parse time has no
/// relationship to broadcast time, so these cases emit no signal rather than a fabricated one
/// (see docs/scte35-findings.md).
///
/// Every PSI-style section here (PAT, PMT, and the private SCTE-35 section) is preceded, on the
/// packet where payload_unit_start_indicator=1, by a mandatory single-byte pointer_field before
/// the section itself starts (ISO/IEC 13818-1). This is accounted for; an earlier version of
/// this parser omitted it and only "worked" against its own fixture generator, which made the
/// same omission -- confirmed against our real captured fixture that real MPEG-TS always
/// includes this byte (see docs/scte35-findings.md for the concrete byte trace).
/// </summary>
public sealed class Scte35MarkerExtractor
{
    private const byte SYNC_BYTE = 0x47;
    private const int TS_PACKET_SIZE = 188;
    private const int PAT_PID = 0x0000;
    private const byte PAT_TABLE_ID = 0x00;
    private const byte PMT_TABLE_ID = 0x02;
    private const byte SCTE35_TABLE_ID = 0xFC;
    private const byte SCTE35_STREAM_TYPE = 0x86;
    private const byte SPLICE_INSERT_COMMAND = 0x05;

    /// <summary>
    /// Parses a recorded MPEG-TS file, locates the PID carrying SCTE-35 splice_info_section
    /// data (PMT stream_type 0x86), and extracts splice_insert commands as ExplicitMarkerSignal
    /// values consumable directly by SmartTVRelay.Core.ExplicitMarkerDetector.Detect(sourceId, signal).
    /// Returns an empty list (never throws) if no SCTE-35 PID is found, the file is malformed,
    /// or no splice_insert commands are present.
    /// </summary>
    public IReadOnlyList<ExplicitMarkerSignal> Extract(string filePath, CancellationToken cancellationToken = default)
    {
        var signals = new List<ExplicitMarkerSignal>();

        try
        {
            if (!File.Exists(filePath))
            {
                return signals;
            }

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(fs);

            int? pmtPid = FindPmtPidFromPat(reader, cancellationToken);
            if (!pmtPid.HasValue)
            {
                return signals;
            }

            int? scte35Pid = FindScte35PidFromPmt(reader, pmtPid.Value, cancellationToken);
            if (!scte35Pid.HasValue)
            {
                return signals;
            }

            ExtractSpliceInserts(reader, scte35Pid.Value, signals, cancellationToken);

            return signals;
        }
        catch (Exception)
        {
            // Any parsing error (malformed file, truncated packet, out-of-range index, etc.):
            // return whatever was successfully extracted so far without throwing.
            return signals;
        }
    }

    /// <summary>
    /// Scans TS packets for a given PID, accumulating each PSI-style section's payload while
    /// correctly skipping the pointer_field byte at the start of every payload_unit_start_indicator
    /// packet, and invokes <paramref name="onSection"/> once a full section has been accumulated
    /// (either because a new section starts, or because the stream ends).
    /// </summary>
    private static void ScanSectionsForPid(
        BinaryReader reader,
        int targetPid,
        int bufferSize,
        Action<byte[], int> onSection,
        CancellationToken cancellationToken)
    {
        reader.BaseStream.Seek(0, SeekOrigin.Begin);

        byte[] sectionData = new byte[bufferSize];
        int sectionDataLength = 0;
        byte[] packet = new byte[TS_PACKET_SIZE];

        while (reader.BaseStream.Position <= reader.BaseStream.Length - TS_PACKET_SIZE)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.Read(packet, 0, TS_PACKET_SIZE) != TS_PACKET_SIZE)
            {
                break;
            }

            if (packet[0] != SYNC_BYTE)
            {
                // Resync: back up to just past the byte we started scanning from.
                reader.BaseStream.Seek(-TS_PACKET_SIZE + 1, SeekOrigin.Current);
                continue;
            }

            ushort pidField = (ushort)((packet[1] << 8) | packet[2]);
            ushort pid = (ushort)(pidField & 0x1FFF);
            bool payloadUnitStart = (pidField & 0x4000) != 0;

            if (pid != targetPid)
            {
                continue;
            }

            byte adaptationFieldControl = (byte)((packet[3] >> 4) & 0x03);
            int payloadStart = 4;
            if (adaptationFieldControl is 0x02 or 0x03)
            {
                byte adaptationFieldLength = packet[4];
                payloadStart = 5 + adaptationFieldLength;
                if (payloadStart >= TS_PACKET_SIZE)
                {
                    continue; // No payload in this packet.
                }
            }

            int copyFrom = payloadStart;

            if (payloadUnitStart)
            {
                // The first byte of a payload_unit_start packet's payload is always a
                // pointer_field: the number of bytes, immediately following it, that finish the
                // section already in progress (almost always 0 in practice, but must still be
                // read, not assumed) -- ISO/IEC 13818-1. Those continuation bytes belong to the
                // PRIOR section, not the new one that starts right after them, so they must be
                // appended before that section is finalized.
                if (copyFrom >= TS_PACKET_SIZE)
                {
                    continue;
                }

                byte pointerField = packet[copyFrom];
                int afterPointer = copyFrom + 1;

                if (pointerField > 0 && sectionDataLength > 0 && afterPointer < TS_PACKET_SIZE)
                {
                    int continueSize = Math.Min(pointerField, Math.Min(TS_PACKET_SIZE - afterPointer, sectionData.Length - sectionDataLength));
                    if (continueSize > 0)
                    {
                        Array.Copy(packet, afterPointer, sectionData, sectionDataLength, continueSize);
                        sectionDataLength += continueSize;
                    }
                }

                if (sectionDataLength > 0)
                {
                    onSection(sectionData, sectionDataLength);
                    sectionDataLength = 0;
                }

                copyFrom = afterPointer + pointerField;
            }

            if (copyFrom >= TS_PACKET_SIZE)
            {
                continue;
            }

            int payloadSize = TS_PACKET_SIZE - copyFrom;
            int copySize = Math.Min(payloadSize, sectionData.Length - sectionDataLength);
            if (copySize > 0)
            {
                Array.Copy(packet, copyFrom, sectionData, sectionDataLength, copySize);
                sectionDataLength += copySize;
            }
        }

        if (sectionDataLength > 0)
        {
            onSection(sectionData, sectionDataLength);
        }
    }

    private int? FindPmtPidFromPat(BinaryReader reader, CancellationToken cancellationToken)
    {
        int? result = null;

        ScanSectionsForPid(reader, PAT_PID, bufferSize: 2048, (data, length) =>
        {
            if (result.HasValue)
            {
                return;
            }

            result = ParsePatSection(data, length);
        }, cancellationToken);

        return result;
    }

    private static int? ParsePatSection(byte[] data, int length)
    {
        if (length < 12)
        {
            return null; // Too short for a PAT section with at least one program entry + CRC.
        }

        byte tableId = data[0];
        if (tableId != PAT_TABLE_ID)
        {
            return null;
        }

        // PAT format (after the mandatory pointer_field, already stripped by the caller):
        // table_id(1) | section_syntax_indicator+reserved+section_length(2) |
        // transport_stream_id(2) | reserved+version+current_next_indicator(1) |
        // section_number(1) | last_section_number(1) |
        // [ program_number(2) | reserved+PID(2) ]... | CRC_32(4)
        const int headerLength = 8;
        int i = headerLength;
        while (i + 4 <= length - 4) // 4 bytes remaining for CRC.
        {
            ushort programNumber = (ushort)((data[i] << 8) | data[i + 1]);
            ushort pidField = (ushort)((data[i + 2] << 8) | data[i + 3]);
            ushort pid = (ushort)(pidField & 0x1FFF);

            if (programNumber != 0) // Skip the network_PID entry (program_number == 0).
            {
                return pid; // Return the first program's PMT PID.
            }

            i += 4;
        }

        return null;
    }

    private int? FindScte35PidFromPmt(BinaryReader reader, int pmtPid, CancellationToken cancellationToken)
    {
        int? result = null;

        ScanSectionsForPid(reader, pmtPid, bufferSize: 4096, (data, length) =>
        {
            if (result.HasValue)
            {
                return;
            }

            result = ParsePmtSection(data, length);
        }, cancellationToken);

        return result;
    }

    private static int? ParsePmtSection(byte[] data, int length)
    {
        if (length < 13)
        {
            return null; // Too short for a PMT section header + CRC.
        }

        byte tableId = data[0];
        if (tableId != PMT_TABLE_ID)
        {
            return null;
        }

        // PMT format: table_id(1) | section_syntax_indicator+reserved+section_length(2) |
        // program_number(2) | reserved+version+current_next_indicator(1) | section_number(1) |
        // last_section_number(1) | reserved+PCR_PID(2) | reserved+program_info_length(2) |
        // descriptor... | [ stream_type(1) | reserved+PID(2) | reserved+ES_info_length(2) |
        // descriptor... ]... | CRC_32(4)
        // Bytes consumed before the program_info_length field: table_id(1) + section_length(2)
        // + program_number(2) + version/current_next(1) + section_number(1) +
        // last_section_number(1) + PCR_PID(2) = 10.
        const int headerLength = 10;
        int i = headerLength;
        if (i + 2 > length)
        {
            return null;
        }

        int programInfoLength = ((data[i] & 0x0F) << 8) | data[i + 1];
        i += 2 + programInfoLength;

        while (i + 5 <= length - 4)
        {
            byte streamType = data[i];
            ushort pidField = (ushort)((data[i + 1] << 8) | data[i + 2]);
            ushort streamPid = (ushort)(pidField & 0x1FFF);
            int esInfoLength = ((data[i + 3] & 0x0F) << 8) | data[i + 4];

            if (streamType == SCTE35_STREAM_TYPE)
            {
                return streamPid;
            }

            i += 5 + esInfoLength;
        }

        return null;
    }

    private void ExtractSpliceInserts(BinaryReader reader, int scte35Pid, List<ExplicitMarkerSignal> signals, CancellationToken cancellationToken)
    {
        ScanSectionsForPid(reader, scte35Pid, bufferSize: 4096, (data, length) =>
        {
            TryParseScte35Section(data, length, signals);
        }, cancellationToken);
    }

    private static void TryParseScte35Section(byte[] data, int length, List<ExplicitMarkerSignal> signals)
    {
        // splice_info_section() fixed header, byte-exact (ANSI/SCTE 35):
        //   table_id                                          8 bits  -> data[0]
        //   section_syntax_indicator+private_indicator+
        //     reserved+section_length                        16 bits  -> data[1..2]
        //   protocol_version                                  8 bits  -> data[3]
        //   encrypted_packet+encryption_algorithm+
        //     pts_adjustment                                 40 bits  -> data[4..8]  (5 bytes)
        //   cw_index                                           8 bits  -> data[9]
        //   tier+splice_command_length                       24 bits  -> data[10..12] (3 bytes)
        //   splice_command_type                                8 bits  -> data[13]
        //   splice_command()                                   ...     -> data[14..]
        // 14 bytes consumed before splice_command() begins.
        const int commandTypeIndex = 13;
        const int minSectionHeaderLength = commandTypeIndex + 1;

        if (length < minSectionHeaderLength)
        {
            return; // Too short to even contain a command type.
        }

        byte tableId = data[0];
        if (tableId != SCTE35_TABLE_ID)
        {
            return;
        }

        int sectionLength = ((data[1] & 0x0F) << 8) | data[2];
        if (sectionLength <= 0 || 3 + sectionLength > length)
        {
            return; // Section header claims more data than we have -- truncated/malformed.
        }

        int sectionEnd = 3 + sectionLength; // Exclusive end of this section's declared content.

        byte encryptedPacket = (byte)((data[4] >> 7) & 0x01);
        if (encryptedPacket != 0)
        {
            return; // Encrypted packets are out of scope for this prototype.
        }

        // pts_adjustment: a plain 33-bit field (no marker bits, same layout as splice_time()'s
        // pts_time) spanning the low bit of data[4] through data[8]. The effective splice time
        // is (pts_time + pts_adjustment) mod 2^33 -- applying it here, not just reading
        // encrypted_packet out of the same 5 bytes, per ANSI/SCTE 35.
        ulong ptsAdjustment = ExtractPtsTime33(data, 4);

        byte spliceCommandType = data[commandTypeIndex];
        if (spliceCommandType != SPLICE_INSERT_COMMAND)
        {
            return; // Only splice_insert is handled; other command types are safely ignored.
        }

        // tier(12 bits) + splice_command_length(12 bits): data[10..12]. 0xFFF is the spec's
        // "length not specified" sentinel; fall back to the section's own declared bound then.
        const int UnknownCommandLength = 0xFFF;
        int spliceCommandLength = ((data[11] & 0x0F) << 8) | data[12];
        int commandStart = commandTypeIndex + 1;
        int commandEnd = Math.Min(length, sectionEnd);
        if (spliceCommandLength != UnknownCommandLength)
        {
            commandEnd = Math.Min(commandEnd, commandStart + spliceCommandLength);
        }

        TryParseSpliceInsert(data, commandStart, commandEnd, ptsAdjustment, signals);
    }

    private static void TryParseSpliceInsert(byte[] data, int startIndex, int commandEnd, ulong ptsAdjustment, List<ExplicitMarkerSignal> signals)
    {
        // splice_insert() (fields actually needed for CueOut/CueIn detection; duration,
        // component-splice-per-PID, and avail fields are intentionally not parsed):
        //   splice_event_id                                   32 bits (4 bytes)
        //   splice_event_cancel_indicator                      1 bit  ) byte A: bit7 + reserved(7)
        //   reserved                                           7 bits )
        //   -- the rest is only present when splice_event_cancel_indicator == 0 --
        //   out_of_network_indicator                           1 bit  ) byte B: bit7..bit4
        //   program_splice_flag                                1 bit  )        + reserved(4)
        //   duration_flag                                      1 bit  )
        //   splice_immediate_flag                              1 bit  )
        //   reserved                                           4 bits )
        //   if (program_splice_flag && !splice_immediate_flag) splice_time()
        int i = startIndex;
        if (i + 4 > commandEnd)
        {
            return; // Too short for splice_event_id.
        }

        i += 4; // Skip splice_event_id.

        if (i >= commandEnd)
        {
            return; // Truncated before the cancel-indicator byte.
        }

        byte cancelByte = data[i];
        i++;

        bool spliceEventCancelIndicator = (cancelByte & 0x80) != 0;
        if (spliceEventCancelIndicator)
        {
            return; // Event cancelled: nothing to signal.
        }

        if (i >= commandEnd)
        {
            return; // Truncated before the flags byte.
        }

        byte flagsByte = data[i];
        i++;

        bool outOfNetworkIndicator = (flagsByte & 0x80) != 0;
        bool programSpliceFlag = (flagsByte & 0x40) != 0;
        bool spliceImmediateFlag = (flagsByte & 0x10) != 0;

        ExplicitMarkerKind kind = outOfNetworkIndicator ? ExplicitMarkerKind.CueOut : ExplicitMarkerKind.CueIn;

        if (!programSpliceFlag || spliceImmediateFlag)
        {
            // No splice_time() is present for this case (component-splice-per-PID, out of
            // scope; or an immediate splice, which is defined as having no scheduled time at
            // all). This extractor only parses recorded files, so wall-clock "now" at parse
            // time has no relationship to broadcast time -- emit nothing rather than a
            // fabricated, wrongly-timed signal (see docs/scte35-findings.md).
            return;
        }

        // splice_time() is present: 1 bit time_specified_flag, then either a 33-bit
        // pts_time (sharing the remainder of the first byte) or 7 reserved bits.
        if (i >= commandEnd)
        {
            return; // Truncated before splice_time().
        }

        byte firstTimeByte = data[i];
        bool timeSpecified = (firstTimeByte & 0x80) != 0;

        if (!timeSpecified)
        {
            // No pts_time in this section either: same rationale as above, emit nothing.
            return;
        }

        if (i + 4 >= commandEnd)
        {
            return; // Truncated mid-pts_time.
        }

        ulong ptsTime = ExtractPtsTime33(data, i);
        ulong adjustedPtsTime = (ptsTime + ptsAdjustment) & 0x1FFFFFFFFUL; // mod 2^33, per spec.
        var observedAt = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(adjustedPtsTime / 90000.0);

        signals.Add(new ExplicitMarkerSignal(kind, observedAt));
    }

    /// <summary>
    /// Extracts a plain 33-bit unsigned big-endian value from 5 bytes starting at
    /// <paramref name="startIndex"/>: bit 0 of the first byte is the value's MSB (bit 32), and
    /// the following 4 bytes are its remaining 32 bits. Used for both splice_time()'s pts_time
    /// (caller must confirm time_specified_flag first) and pts_adjustment, which share this
    /// exact layout in ANSI/SCTE 35. Unlike the PES optional-PTS header field, neither has any
    /// marker bits interspersed.
    /// </summary>
    private static ulong ExtractPtsTime33(byte[] data, int startIndex)
    {
        ulong pts = (ulong)(data[startIndex] & 0x01) << 32;
        pts |= (ulong)data[startIndex + 1] << 24;
        pts |= (ulong)data[startIndex + 2] << 16;
        pts |= (ulong)data[startIndex + 3] << 8;
        pts |= data[startIndex + 4];
        return pts;
    }
}
