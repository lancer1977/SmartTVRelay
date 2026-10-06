namespace SmartTVRelay.Core.Ingest;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

/// <summary>
/// Extracts supported SCTE-35 splice_insert and time_signal commands from MPEG-TS transport stream files.
/// Parses the PAT to locate the PMT, then the PMT to find SCTE-35 streams (stream_type 0x86),
/// and finally extracts supported commands as timestamped ExplicitMarkerSignal values.
///
/// Supports splice_insert (command_type 0x05) and time_signal (0x06) with CUEI segmentation descriptors.
/// Segmentation start/end types explicitly recognized are Break (0x22/0x23), Provider Advertisement
/// (0x30/0x31), Distributor Advertisement (0x32/0x33), Provider Placement Opportunity (0x34/0x35),
/// and Distributor Placement Opportunity (0x36/0x37). Other types and descriptors are ignored.
/// No CRC validation, no encrypted_packet support, no component-splice-per-PID variants, no
/// avail fields. Immediate splices (splice_immediate_flag=1), and any
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
    private const byte TIME_SIGNAL_COMMAND = 0x06;
    private const byte SEGMENTATION_DESCRIPTOR_TAG = 0x02;

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

            ExtractSpliceCommands(reader, scte35Pid.Value, signals, cancellationToken);

            return signals;
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation must be observable, not silently turned into a "successful"
            // (partial) result -- the broad catch below is for genuine parsing errors only.
            throw;
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
    /// packet, and invokes <paramref name="onSection"/> once for every complete section found --
    /// including when two or more small sections (e.g. a CueOut immediately followed by a CueIn)
    /// are packed into the same TS packet's payload, which an earlier version of this method
    /// missed entirely: it accumulated everything up to the next payload_unit_start as a single
    /// blob and invoked onSection only once, silently dropping every section after the first in
    /// that payload.
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
            bool transportErrorIndicator = (packet[1] & 0x80) != 0;

            if (pid != targetPid)
            {
                continue;
            }

            if (transportErrorIndicator)
            {
                // At least one uncorrectable bit error in this packet's payload (ISO/IEC
                // 13818-1). Section CRCs are intentionally not checked anywhere in this class
                // (see class doc), so a corrupted payload has no other integrity check backing
                // it up -- using it at all risks a corrupted bit pattern being decoded as a
                // confident CueOut/CueIn. Discard this packet's contribution entirely, and
                // invalidate whatever section was in progress for this PID: whether this packet
                // was meant to start, continue, or complete it, that section can no longer be
                // trusted.
                sectionDataLength = 0;
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

                // Whatever is now in sectionData should be exactly one complete, finished section
                // (or nothing, for the very first section ever seen on this PID): the byte right
                // after the pointer_field's continuation bytes always starts a brand-new section,
                // per the same rule used to compute those continuation bytes above. Any bytes
                // still left after draining mean the prior section was NOT actually completed by
                // its own declared pointer_field continuation -- a lost packet, a capture that
                // began mid-section, or a corrupted stream, not a genuine multi-packet section
                // (those are only ever continued by payload_unit_start=0 packets, handled below,
                // never by a pointer_field). That stale, never-to-be-completed fragment must be
                // discarded here, before the new section's bytes are appended -- otherwise the
                // new section would be appended onto it and parsed as corrupted evidence, or
                // silently swallowed entirely.
                sectionDataLength = DrainCompleteSections(sectionData, sectionDataLength, onSection);
                sectionDataLength = 0;

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

            // The payload just appended may complete the section in progress AND contain one or
            // more further complete sections packed right after it in the same payload.
            sectionDataLength = DrainCompleteSections(sectionData, sectionDataLength, onSection);
        }

        // EOF: dispatch whatever complete section(s) remain. A genuinely truncated trailing
        // section is simply dropped, matching this class's overall "never throw, return what was
        // successfully parsed" contract.
        DrainCompleteSections(sectionData, sectionDataLength, onSection);
    }

    /// <summary>
    /// Extracts and dispatches every complete PSI-style section currently at the front of
    /// <paramref name="sectionData"/> -- each section's own table_id + section_length determines
    /// its exact length, so two or more small sections packed into the same accumulated bytes
    /// (e.g. a CueOut immediately followed by a CueIn) are each dispatched separately, not
    /// merged into one call. Compacts any leftover, not-yet-complete section to the front of the
    /// buffer and returns its length.
    /// </summary>
    /// <remarks>
    /// Stuffing bytes (table_id=0xFF, ISO/IEC 13818-1 2.4.4.7) fill the rest of a TS packet's
    /// payload after the last real section and are discarded here rather than treated as the
    /// start of a section awaiting more data -- otherwise every later section on this PID would
    /// be swallowed waiting for bytes that never satisfy a bogus declared length.
    /// </remarks>
    private static int DrainCompleteSections(byte[] sectionData, int sectionDataLength, Action<byte[], int> onSection)
    {
        int offset = 0;

        while (true)
        {
            int remaining = sectionDataLength - offset;
            if (remaining < 3)
            {
                break; // Not enough bytes to even read table_id + section_length yet.
            }

            if (sectionData[offset] == 0xFF)
            {
                // Stuffing: nothing here, or after it, is a section in progress.
                offset = sectionDataLength;
                break;
            }

            int declaredSectionLength = ((sectionData[offset + 1] & 0x0F) << 8) | sectionData[offset + 2];
            int totalSectionLength = 3 + declaredSectionLength;

            if (totalSectionLength > sectionData.Length - offset)
            {
                // Malformed: claims to be larger than the scratch buffer could ever hold from
                // here. Drop it rather than wait forever for bytes that would overflow.
                offset = sectionDataLength;
                break;
            }

            if (remaining < totalSectionLength)
            {
                break; // A real section, but not fully accumulated yet -- wait for more bytes.
            }

            // Dispatch a private, exactly-sized copy so a caller reading data[0..length) can
            // never observe bytes belonging to a different section in the shared scratch buffer.
            var section = new byte[totalSectionLength];
            Array.Copy(sectionData, offset, section, 0, totalSectionLength);
            onSection(section, totalSectionLength);

            offset += totalSectionLength;
        }

        int leftover = sectionDataLength - offset;
        if (leftover > 0 && offset > 0)
        {
            Array.Copy(sectionData, offset, sectionData, 0, leftover);
        }

        return leftover;
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
        if ((data[5] & 0x01) == 0)
        {
            // current_next_indicator=0: this table describes a not-yet-active version (e.g.
            // during a version transition). Following it could point at a PMT PID that isn't
            // the currently active one, so ignore it and wait for the currently-applicable
            // table instead.
            return null;
        }

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
        if ((data[5] & 0x01) == 0)
        {
            // current_next_indicator=0: not-yet-active table version (e.g. mid version
            // transition). The stream PID it lists may not be the one actually carrying
            // SCTE-35 data yet, so ignore it and wait for the currently-applicable table.
            return null;
        }

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

    private void ExtractSpliceCommands(BinaryReader reader, int scte35Pid, List<ExplicitMarkerSignal> signals, CancellationToken cancellationToken)
    {
        // splice_event_id is scoped per-PID for the lifetime of this extraction, not per section:
        // a later splice_insert cancelling an earlier one by event_id can arrive in any later
        // section, so a signal can only be finalized once the whole PID has been scanned. Kept
        // separate from `signals` (which the caller already owns) rather than mutating it
        // in-place, so a cancellation can remove an earlier entry regardless of arrival order.
        var pendingSignals = new List<(uint EventId, ExplicitMarkerSignal Signal)>();
        var canceledEventIds = new HashSet<uint>();

        ScanSectionsForPid(reader, scte35Pid, bufferSize: 4096, (data, length) =>
        {
            TryParseScte35Section(data, length, pendingSignals, canceledEventIds);
        }, cancellationToken);

        foreach (var (eventId, signal) in pendingSignals)
        {
            if (!canceledEventIds.Contains(eventId))
            {
                signals.Add(signal);
            }
        }
    }

    private static void TryParseScte35Section(byte[] data, int length, List<(uint EventId, ExplicitMarkerSignal Signal)> pendingSignals, HashSet<uint> canceledEventIds)
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
        if (spliceCommandType is not (SPLICE_INSERT_COMMAND or TIME_SIGNAL_COMMAND))
        {
            return;
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

        if (spliceCommandType == SPLICE_INSERT_COMMAND)
        {
            TryParseSpliceInsert(data, commandStart, commandEnd, ptsAdjustment, pendingSignals, canceledEventIds);
        }
        else
        {
                TryParseTimeSignal(data, commandStart, commandEnd, sectionEnd, ptsAdjustment, pendingSignals, canceledEventIds);
        }
    }

    private static void TryParseTimeSignal(byte[] data, int commandStart, int commandEnd, int sectionEnd, ulong ptsAdjustment, List<(uint EventId, ExplicitMarkerSignal Signal)> pendingSignals, HashSet<uint> canceledEventIds)
    {
        // time_signal() consists of one splice_time(). time_specified_flag=0 has no PTS and
        // therefore cannot safely create timestamped evidence for a recorded stream.
        if (commandStart >= commandEnd || (data[commandStart] & 0x80) == 0 || commandStart + 5 > commandEnd)
        {
            return;
        }

        ulong ptsTime = ExtractPtsTime33(data, commandStart);
        ulong adjustedPtsTime = (ptsTime + ptsAdjustment) & 0x1FFFFFFFFUL;
        var observedAt = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(adjustedPtsTime / 90000.0);

        int descriptorLengthIndex = commandEnd;
        if (descriptorLengthIndex + 2 > sectionEnd - 4)
        {
            return;
        }

        int descriptorLoopLength = (data[descriptorLengthIndex] << 8) | data[descriptorLengthIndex + 1];
        int descriptorStart = descriptorLengthIndex + 2;
        int descriptorEnd = descriptorStart + descriptorLoopLength;
        if (descriptorEnd > sectionEnd - 4)
        {
            return;
        }

        int i = descriptorStart;
        while (i + 2 <= descriptorEnd)
        {
            int tag = data[i++];
            int length = data[i++];
            if (i + length > descriptorEnd) return;
            if (tag == SEGMENTATION_DESCRIPTOR_TAG)
            {
                TryParseSegmentationDescriptor(data, i, i + length, observedAt, pendingSignals, canceledEventIds);
            }
            i += length;
        }
    }

    private static void TryParseSegmentationDescriptor(byte[] data, int start, int end, DateTimeOffset observedAt, List<(uint EventId, ExplicitMarkerSignal Signal)> pendingSignals, HashSet<uint> canceledEventIds)
    {
        // CUEI + segmentation_event_id + cancel byte are the mandatory prefix.
        if (end - start < 9 || data[start] != (byte)'C' || data[start + 1] != (byte)'U' || data[start + 2] != (byte)'E' || data[start + 3] != (byte)'I') return;
        int i = start + 4;
        uint eventId = (uint)((data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3]);
        i += 4;
        bool canceled = (data[i++] & 0x80) != 0;
        if (canceled)
        {
            canceledEventIds.Add(eventId);
            return;
        }
        if (i >= end) return;

        byte flags = data[i++];
        bool programSegmentation = (flags & 0x80) != 0;
        bool durationFlag = (flags & 0x40) != 0;
        bool deliveryNotRestricted = (flags & 0x20) != 0;
        if (!deliveryNotRestricted)
        {
            if (i >= end) return;
            i++; // five delivery restriction flags plus three reserved bits
        }

        if (!programSegmentation)
        {
            if (i >= end) return;
            int componentCount = data[i++];
            if (i + componentCount * 6 > end) return;
            i += componentCount * 6; // component_tag + 40-bit pts_offset
        }
        if (durationFlag)
        {
            if (i + 5 > end) return;
            i += 5; // 40-bit segmentation_duration; not needed for signal timestamping
        }
        if (i + 2 > end) return;
        int upidLength = data[i + 1];
        i += 2;
        if (i + upidLength + 3 > end) return;
        i += upidLength;
        byte type = data[i];
        ExplicitMarkerKind? kind = type switch
        {
            0x22 or 0x30 or 0x32 or 0x34 or 0x36 => ExplicitMarkerKind.CueOut,
            0x23 or 0x31 or 0x33 or 0x35 or 0x37 => ExplicitMarkerKind.CueIn,
            _ => null,
        };
        if (kind.HasValue) pendingSignals.Add((eventId, new ExplicitMarkerSignal(kind.Value, observedAt)));
    }

    private static void TryParseSpliceInsert(byte[] data, int startIndex, int commandEnd, ulong ptsAdjustment, List<(uint EventId, ExplicitMarkerSignal Signal)> pendingSignals, HashSet<uint> canceledEventIds)
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

        uint spliceEventId = (uint)((data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3]);
        i += 4;

        if (i >= commandEnd)
        {
            return; // Truncated before the cancel-indicator byte.
        }

        byte cancelByte = data[i];
        i++;

        bool spliceEventCancelIndicator = (cancelByte & 0x80) != 0;
        if (spliceEventCancelIndicator)
        {
            // A cancellation can arrive after its target event's own splice_insert has already
            // been parsed and queued (the normal case: announce, later cancel). Recording the ID
            // here, rather than trying to find and remove an already-added signal immediately,
            // means the removal is correct regardless of arrival order or which section it's in.
            canceledEventIds.Add(spliceEventId);
            return;
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

        pendingSignals.Add((spliceEventId, new ExplicitMarkerSignal(kind, observedAt)));
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
