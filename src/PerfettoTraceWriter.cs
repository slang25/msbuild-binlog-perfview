#nullable enable
using System;
using System.Buffers;
using System.IO;
using System.Text;

/// <summary>
/// Writes Perfetto trace protobuf format manually.
/// Based on https://perfetto.dev/docs/reference/trace-packet-proto
/// Optimized for minimal allocations using span-based writing.
/// </summary>
public class PerfettoTraceWriter
{
    private readonly MemoryStream _stream = new();
    private readonly uint _sequenceId = 1;
    private bool _firstPacket = true;

    // Protobuf field numbers from Perfetto protos
    internal const int TRACE_PACKET = 1;
    internal const int TIMESTAMP = 8;
    internal const int TRACK_EVENT = 11;
    internal const int TRACK_DESCRIPTOR = 60;
    internal const int TRUSTED_PACKET_SEQ_ID = 10;
    internal const int SEQUENCE_FLAGS = 13;

    // TrackDescriptor fields
    internal const int TD_UUID = 1;
    internal const int TD_NAME = 2;
    internal const int TD_PROCESS = 3;

    // ProcessDescriptor fields
    internal const int PD_PID = 1;
    internal const int PD_PROCESS_NAME = 6;

    // TrackEvent fields
    internal const int TE_NAME = 23;
    internal const int TE_TYPE = 9;
    internal const int TE_TRACK_UUID = 11;
    internal const int TE_CATEGORIES = 22;

    // TrackEvent.Type values
    internal const int TYPE_SLICE_BEGIN = 1;
    internal const int TYPE_SLICE_END = 2;
    internal const int TYPE_INSTANT = 3;

    // Sequence flags
    internal const int SEQ_INCREMENTAL_STATE_CLEARED = 1;
    internal const int SEQ_NEEDS_INCREMENTAL_STATE = 2;

    public void WriteProcessTrackDescriptor(ulong uuid, uint pid, string name)
    {
        int nameByteCount = Encoding.UTF8.GetByteCount(name);

        // Calculate ProcessDescriptor size
        int pdSize = GetTaggedVarintSize(PD_PID, pid)
                   + GetLengthDelimitedSize(PD_PROCESS_NAME, nameByteCount);

        // Calculate TrackDescriptor size
        int tdSize = GetTaggedVarintSize(TD_UUID, uuid)
                   + GetLengthDelimitedSize(TD_NAME, nameByteCount)
                   + GetLengthDelimitedSize(TD_PROCESS, pdSize);

        // Calculate packet size
        int seqFlags = GetSequenceFlagsValue();
        int packetSize = GetLengthDelimitedSize(TRACK_DESCRIPTOR, tdSize)
                       + GetTaggedVarintSize(TRUSTED_PACKET_SEQ_ID, _sequenceId)
                       + GetTaggedVarintSize(SEQUENCE_FLAGS, (ulong)seqFlags);

        // Write everything directly to _stream
        WriteLengthPrefix(TRACE_PACKET, packetSize);
        WriteLengthPrefix(TRACK_DESCRIPTOR, tdSize);
        WriteTaggedVarint(TD_UUID, uuid);
        WriteStringField(TD_NAME, name, nameByteCount);
        WriteLengthPrefix(TD_PROCESS, pdSize);
        WriteTaggedVarint(PD_PID, pid);
        WriteStringField(PD_PROCESS_NAME, name, nameByteCount);
        WriteTaggedVarint(TRUSTED_PACKET_SEQ_ID, _sequenceId);
        WriteTaggedVarint(SEQUENCE_FLAGS, (ulong)seqFlags);
        _firstPacket = false;
    }

    public void WriteSliceBegin(ulong trackUuid, long timestampNs, string name, string category)
    {
        WriteTrackEvent(trackUuid, timestampNs, name, category, TYPE_SLICE_BEGIN);
    }

    public void WriteSliceEnd(ulong trackUuid, long timestampNs)
    {
        WriteTrackEvent(trackUuid, timestampNs, null, null, TYPE_SLICE_END);
    }

    public void WriteInstantEvent(ulong trackUuid, long timestampNs, string name, string category)
    {
        WriteTrackEvent(trackUuid, timestampNs, name, category, TYPE_INSTANT);
    }

    private void WriteTrackEvent(ulong trackUuid, long timestampNs, string? name, string? category, int type)
    {
        int nameByteCount = name != null ? Encoding.UTF8.GetByteCount(name) : 0;
        int categoryByteCount = category != null ? Encoding.UTF8.GetByteCount(category) : 0;

        // Calculate TrackEvent size
        int teSize = GetTaggedVarintSize(TE_TYPE, (ulong)type)
                   + GetTaggedVarintSize(TE_TRACK_UUID, trackUuid);
        if (name != null)
            teSize += GetLengthDelimitedSize(TE_NAME, nameByteCount);
        if (category != null)
            teSize += GetLengthDelimitedSize(TE_CATEGORIES, categoryByteCount);

        // Calculate packet size
        int seqFlags = GetSequenceFlagsValue();
        int packetSize = GetTaggedVarintSize(TIMESTAMP, (ulong)timestampNs)
                       + GetLengthDelimitedSize(TRACK_EVENT, teSize)
                       + GetTaggedVarintSize(TRUSTED_PACKET_SEQ_ID, _sequenceId)
                       + GetTaggedVarintSize(SEQUENCE_FLAGS, (ulong)seqFlags);

        // Write everything directly to _stream
        WriteLengthPrefix(TRACE_PACKET, packetSize);
        WriteTaggedVarint(TIMESTAMP, (ulong)timestampNs);
        WriteLengthPrefix(TRACK_EVENT, teSize);
        WriteTaggedVarint(TE_TYPE, (ulong)type);
        WriteTaggedVarint(TE_TRACK_UUID, trackUuid);
        if (name != null)
            WriteStringField(TE_NAME, name, nameByteCount);
        if (category != null)
            WriteStringField(TE_CATEGORIES, category, categoryByteCount);
        WriteTaggedVarint(TRUSTED_PACKET_SEQ_ID, _sequenceId);
        WriteTaggedVarint(SEQUENCE_FLAGS, (ulong)seqFlags);
        _firstPacket = false;
    }

    private int GetSequenceFlagsValue()
    {
        return _firstPacket
            ? SEQ_INCREMENTAL_STATE_CLEARED | SEQ_NEEDS_INCREMENTAL_STATE
            : SEQ_NEEDS_INCREMENTAL_STATE;
    }

    public byte[] ToArray() => _stream.ToArray();

    // Size calculation helpers
    internal static int GetVarintSize(ulong value)
    {
        int size = 1;
        while (value > 127)
        {
            size++;
            value >>= 7;
        }
        return size;
    }

    internal static int GetTaggedVarintSize(int fieldNumber, ulong value)
        => GetVarintSize((ulong)(fieldNumber << 3)) + GetVarintSize(value);

    internal static int GetLengthDelimitedSize(int fieldNumber, int contentLength)
        => GetVarintSize((ulong)((fieldNumber << 3) | 2)) + GetVarintSize((ulong)contentLength) + contentLength;

    // Span-based writing helpers
    internal static int WriteVarintToSpan(Span<byte> buffer, ulong value)
    {
        int pos = 0;
        while (value > 127)
        {
            buffer[pos++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }
        buffer[pos++] = (byte)value;
        return pos;
    }

    private void WriteTaggedVarint(int fieldNumber, ulong value)
    {
        Span<byte> buffer = stackalloc byte[20];
        int pos = WriteVarintToSpan(buffer, (ulong)(fieldNumber << 3));
        pos += WriteVarintToSpan(buffer[pos..], value);
        _stream.Write(buffer[..pos]);
    }

    private void WriteLengthPrefix(int fieldNumber, int length)
    {
        Span<byte> buffer = stackalloc byte[15];
        int pos = WriteVarintToSpan(buffer, (ulong)((fieldNumber << 3) | 2));
        pos += WriteVarintToSpan(buffer[pos..], (ulong)length);
        _stream.Write(buffer[..pos]);
    }

    private void WriteStringField(int fieldNumber, string value, int byteCount)
    {
        WriteLengthPrefix(fieldNumber, byteCount);

        if (byteCount <= 256)
        {
            Span<byte> buffer = stackalloc byte[256];
            Encoding.UTF8.GetBytes(value.AsSpan(), buffer);
            _stream.Write(buffer[..byteCount]);
        }
        else
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                Encoding.UTF8.GetBytes(value, 0, value.Length, rented, 0);
                _stream.Write(rented, 0, byteCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    // Original static methods kept for backward compatibility and testing
    internal static void WriteVarint(Stream s, int fieldNumber, ulong value)
    {
        WriteRawVarint(s, (ulong)((fieldNumber << 3) | 0));
        WriteRawVarint(s, value);
    }

    internal static void WriteString(Stream s, int fieldNumber, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteLengthDelimited(s, fieldNumber, bytes);
    }

    internal static void WriteLengthDelimited(Stream s, int fieldNumber, byte[] data)
    {
        WriteRawVarint(s, (ulong)((fieldNumber << 3) | 2));
        WriteRawVarint(s, (ulong)data.Length);
        s.Write(data, 0, data.Length);
    }

    internal static void WriteRawVarint(Stream s, ulong value)
    {
        while (value > 127)
        {
            s.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }
}
