#nullable enable
using System.IO;
using System.Text;

/// <summary>
/// Writes Perfetto trace protobuf format manually.
/// Based on https://perfetto.dev/docs/reference/trace-packet-proto
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
        using var packet = new MemoryStream();

        using var td = new MemoryStream();
        WriteVarint(td, TD_UUID, uuid);
        WriteString(td, TD_NAME, name);

        using var pd = new MemoryStream();
        WriteVarint(pd, PD_PID, pid);
        WriteString(pd, PD_PROCESS_NAME, name);
        WriteLengthDelimited(td, TD_PROCESS, pd.ToArray());

        WriteLengthDelimited(packet, TRACK_DESCRIPTOR, td.ToArray());
        WriteVarint(packet, TRUSTED_PACKET_SEQ_ID, _sequenceId);
        WriteSequenceFlags(packet);

        WriteLengthDelimited(_stream, TRACE_PACKET, packet.ToArray());
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
        using var packet = new MemoryStream();

        WriteVarint(packet, TIMESTAMP, (ulong)timestampNs);

        using var te = new MemoryStream();
        WriteVarint(te, TE_TYPE, (ulong)type);
        WriteVarint(te, TE_TRACK_UUID, trackUuid);
        if (name != null)
            WriteString(te, TE_NAME, name);
        if (category != null)
            WriteString(te, TE_CATEGORIES, category);

        WriteLengthDelimited(packet, TRACK_EVENT, te.ToArray());
        WriteVarint(packet, TRUSTED_PACKET_SEQ_ID, _sequenceId);
        WriteSequenceFlags(packet);

        WriteLengthDelimited(_stream, TRACE_PACKET, packet.ToArray());
    }

    private void WriteSequenceFlags(MemoryStream packet)
    {
        if (_firstPacket)
        {
            WriteVarint(packet, SEQUENCE_FLAGS, SEQ_INCREMENTAL_STATE_CLEARED | SEQ_NEEDS_INCREMENTAL_STATE);
            _firstPacket = false;
        }
        else
        {
            WriteVarint(packet, SEQUENCE_FLAGS, SEQ_NEEDS_INCREMENTAL_STATE);
        }
    }

    public byte[] ToArray() => _stream.ToArray();

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
