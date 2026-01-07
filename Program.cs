#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;

Console.WriteLine("MSBuild Binlog to Perfetto Viewer loaded");

await System.Threading.Tasks.Task.Delay(-1); // Keep runtime alive for JS calls

public partial class BinlogConverter
{
    [JSExport]
    public static string ConvertToTrace(
        byte[] binlogData,
        bool includeProjects,
        bool includeTargets,
        bool includeTasks,
        bool includeMessages,
        bool includeWarnings,
        bool includeErrors)
    {
        try
        {
            var events = new List<TraceEvent>();
            var nodeNames = new HashSet<int>();

            using var stream = new MemoryStream(binlogData);
            var reader = new BinLogReader();

            long? buildStartTime = null;

            foreach (var record in reader.ReadRecords(stream))
            {
                var args = record.Args;
                if (args == null) continue;

                var ctx = args.BuildEventContext;
                long timestamp = args.Timestamp.Ticks / 10;

                if (buildStartTime == null)
                    buildStartTime = timestamp;

                long relativeTime = timestamp - buildStartTime.Value;

                int nodeId = ctx?.NodeId ?? 0;
                // Use nodeId as pid, tid=0 so all events on same node stack in flame graph
                int pid = nodeId + 1;
                int tid = 0;

                if (ctx != null && nodeId >= 0 && !nodeNames.Contains(nodeId))
                {
                    nodeNames.Add(nodeId);
                    events.Add(new TraceEvent
                    {
                        Name = "process_name",
                        Category = "__metadata",
                        Phase = "M",
                        Timestamp = 0,
                        ProcessId = pid,
                        ThreadId = tid,
                        Args = new TraceEventArgs { Name = $"Node {nodeId}" }
                    });
                }

                switch (args)
                {
                    case BuildStartedEventArgs:
                        events.Add(new TraceEvent
                        {
                            Name = "process_name",
                            Category = "__metadata",
                            Phase = "M",
                            Timestamp = 0,
                            ProcessId = 0,
                            ThreadId = 0,
                            Args = new TraceEventArgs { Name = "Build" }
                        });
                        events.Add(new TraceEvent
                        {
                            Name = "Build",
                            Category = "build",
                            Phase = "B",
                            Timestamp = relativeTime,
                            ProcessId = 0,
                            ThreadId = 0
                        });
                        break;

                    case BuildFinishedEventArgs:
                        events.Add(new TraceEvent
                        {
                            Name = "Build",
                            Category = "build",
                            Phase = "E",
                            Timestamp = relativeTime,
                            ProcessId = 0,
                            ThreadId = 0
                        });
                        break;

                    case ProjectStartedEventArgs projectStarted when includeProjects:
                        {
                            var projectName = Path.GetFileName(projectStarted.ProjectFile) ?? "Project";

                            events.Add(new TraceEvent
                            {
                                Name = projectName,
                                Category = "project",
                                Phase = "B",
                                Timestamp = relativeTime,
                                ProcessId = pid,
                                ThreadId = tid,
                                Args = new TraceEventArgs
                                {
                                    NodeId = nodeId
                                }
                            });
                        }
                        break;

                    case ProjectFinishedEventArgs projectFinished when includeProjects:
                        {
                            var projectName = Path.GetFileName(projectFinished.ProjectFile) ?? "Project";

                            events.Add(new TraceEvent
                            {
                                Name = projectName,
                                Category = "project",
                                Phase = "E",
                                Timestamp = relativeTime,
                                ProcessId = pid,
                                ThreadId = tid,
                                Args = new TraceEventArgs
                                {
                                    NodeId = nodeId
                                }
                            });
                        }
                        break;

                    case TargetStartedEventArgs targetStarted when includeTargets:
                        {
                            events.Add(new TraceEvent
                            {
                                Name = targetStarted.TargetName,
                                Category = "target",
                                Phase = "B",
                                Timestamp = relativeTime,
                                ProcessId = pid,
                                ThreadId = tid,
                                Args = new TraceEventArgs
                                {
                                    NodeId = nodeId
                                }
                            });
                        }
                        break;

                    case TargetFinishedEventArgs targetFinished when includeTargets:
                        {
                            events.Add(new TraceEvent
                            {
                                Name = targetFinished.TargetName,
                                Category = "target",
                                Phase = "E",
                                Timestamp = relativeTime,
                                ProcessId = pid,
                                ThreadId = tid,
                                Args = new TraceEventArgs
                                {
                                    NodeId = nodeId
                                }
                            });
                        }
                        break;

                    case TaskStartedEventArgs taskStarted when includeTasks:
                        {
                            events.Add(new TraceEvent
                            {
                                Name = taskStarted.TaskName,
                                Category = "task",
                                Phase = "B",
                                Timestamp = relativeTime,
                                ProcessId = pid,
                                ThreadId = tid,
                                Args = new TraceEventArgs
                                {
                                    NodeId = nodeId
                                }
                            });
                        }
                        break;

                    case TaskFinishedEventArgs taskFinished when includeTasks:
                        {
                            events.Add(new TraceEvent
                            {
                                Name = taskFinished.TaskName,
                                Category = "task",
                                Phase = "E",
                                Timestamp = relativeTime,
                                ProcessId = pid,
                                ThreadId = tid,
                                Args = new TraceEventArgs
                                {
                                    NodeId = nodeId
                                }
                            });
                        }
                        break;

                    case BuildWarningEventArgs warning when includeWarnings:
                        {
                            events.Add(new TraceEvent
                            {
                                Name = $"Warning: {warning.Code}",
                                Category = "warning",
                                Phase = "i",
                                Timestamp = relativeTime,
                                ProcessId = pid,
                                ThreadId = tid,
                                Scope = "t",
                                Args = new TraceEventArgs
                                {
                                    Code = warning.Code,
                                    Message = warning.Message,
                                    File = warning.File,
                                    Line = warning.LineNumber,
                                    NodeId = nodeId
                                }
                            });
                        }
                        break;

                    case BuildErrorEventArgs error when includeErrors:
                        {
                            events.Add(new TraceEvent
                            {
                                Name = $"Error: {error.Code}",
                                Category = "error",
                                Phase = "i",
                                Timestamp = relativeTime,
                                ProcessId = pid,
                                ThreadId = tid,
                                Scope = "t",
                                Args = new TraceEventArgs
                                {
                                    Code = error.Code,
                                    Message = error.Message,
                                    File = error.File,
                                    Line = error.LineNumber,
                                    NodeId = nodeId
                                }
                            });
                        }
                        break;

                    case BuildMessageEventArgs message when includeMessages && message.Importance == MessageImportance.High:
                        {
                            events.Add(new TraceEvent
                            {
                                Name = TruncateMessage(message.Message, 80),
                                Category = "message",
                                Phase = "i",
                                Timestamp = relativeTime,
                                ProcessId = pid,
                                ThreadId = tid,
                                Scope = "t",
                                Args = new TraceEventArgs
                                {
                                    Message = message.Message,
                                    NodeId = nodeId
                                }
                            });
                        }
                        break;
                }
            }

            var trace = new TraceDocument { TraceEvents = events };
            return JsonSerializer.Serialize(trace, TraceJsonContext.Default.TraceDocument);
        }
        catch (Exception ex)
        {
            var error = new ErrorResult { Error = ex.Message, StackTrace = ex.StackTrace };
            return JsonSerializer.Serialize(error, TraceJsonContext.Default.ErrorResult);
        }
    }

    [JSExport]
    public static byte[] ConvertToProtobuf(
        byte[] binlogData,
        bool includeProjects,
        bool includeTargets,
        bool includeTasks,
        bool includeMessages,
        bool includeWarnings,
        bool includeErrors)
    {
        try
        {
            var writer = new PerfettoTraceWriter();
            var nodeTrackWritten = new HashSet<int>();
            var trackUuids = new Dictionary<int, ulong>(); // nodeId -> trackUuid
            ulong nextTrackUuid = 1;

            using var stream = new MemoryStream(binlogData);
            var reader = new BinLogReader();

            long? buildStartTime = null;

            // Helper to get or create track UUID for a node
            // All events on the same node go on the same track for proper flame graph stacking
            ulong GetTrackUuid(int nodeId)
            {
                if (!trackUuids.TryGetValue(nodeId, out var uuid))
                {
                    uuid = nextTrackUuid++;
                    trackUuids[nodeId] = uuid;
                }
                return uuid;
            }

            foreach (var record in reader.ReadRecords(stream))
            {
                var args = record.Args;
                if (args == null) continue;

                var ctx = args.BuildEventContext;
                long timestamp = args.Timestamp.Ticks * 100; // Convert to nanoseconds

                if (buildStartTime == null)
                    buildStartTime = timestamp;

                long relativeTimeNs = timestamp - buildStartTime.Value;

                int nodeId = ctx?.NodeId ?? 0;

                // Add track descriptor for new nodes - one track per node
                if (ctx != null && nodeId >= 0 && !nodeTrackWritten.Contains(nodeId))
                {
                    nodeTrackWritten.Add(nodeId);
                    var trackUuid = GetTrackUuid(nodeId);
                    writer.WriteProcessTrackDescriptor(trackUuid, (uint)(nodeId + 1), $"Node {nodeId}");
                }

                switch (args)
                {
                    case BuildStartedEventArgs:
                        {
                            var trackUuid = GetTrackUuid(-1); // Special track for build
                            writer.WriteProcessTrackDescriptor(trackUuid, 0, "Build");
                            writer.WriteSliceBegin(trackUuid, relativeTimeNs, "Build", "build");
                        }
                        break;

                    case BuildFinishedEventArgs:
                        {
                            var trackUuid = GetTrackUuid(-1);
                            writer.WriteSliceEnd(trackUuid, relativeTimeNs);
                        }
                        break;

                    case ProjectStartedEventArgs projectStarted when includeProjects:
                        {
                            var projectName = Path.GetFileName(projectStarted.ProjectFile) ?? "Project";
                            var trackUuid = GetTrackUuid(nodeId);
                            writer.WriteSliceBegin(trackUuid, relativeTimeNs, projectName, "project");
                        }
                        break;

                    case ProjectFinishedEventArgs when includeProjects:
                        {
                            var trackUuid = GetTrackUuid(nodeId);
                            writer.WriteSliceEnd(trackUuid, relativeTimeNs);
                        }
                        break;

                    case TargetStartedEventArgs targetStarted when includeTargets:
                        {
                            var trackUuid = GetTrackUuid(nodeId);
                            writer.WriteSliceBegin(trackUuid, relativeTimeNs, targetStarted.TargetName, "target");
                        }
                        break;

                    case TargetFinishedEventArgs when includeTargets:
                        {
                            var trackUuid = GetTrackUuid(nodeId);
                            writer.WriteSliceEnd(trackUuid, relativeTimeNs);
                        }
                        break;

                    case TaskStartedEventArgs taskStarted when includeTasks:
                        {
                            var trackUuid = GetTrackUuid(nodeId);
                            writer.WriteSliceBegin(trackUuid, relativeTimeNs, taskStarted.TaskName, "task");
                        }
                        break;

                    case TaskFinishedEventArgs when includeTasks:
                        {
                            var trackUuid = GetTrackUuid(nodeId);
                            writer.WriteSliceEnd(trackUuid, relativeTimeNs);
                        }
                        break;

                    case BuildWarningEventArgs warning when includeWarnings:
                        {
                            var trackUuid = GetTrackUuid(nodeId);
                            writer.WriteInstantEvent(trackUuid, relativeTimeNs, $"Warning: {warning.Code}", "warning");
                        }
                        break;

                    case BuildErrorEventArgs error when includeErrors:
                        {
                            var trackUuid = GetTrackUuid(nodeId);
                            writer.WriteInstantEvent(trackUuid, relativeTimeNs, $"Error: {error.Code}", "error");
                        }
                        break;

                    case BuildMessageEventArgs message when includeMessages && message.Importance == MessageImportance.High:
                        {
                            var trackUuid = GetTrackUuid(nodeId);
                            writer.WriteInstantEvent(trackUuid, relativeTimeNs, TruncateMessage(message.Message, 80), "message");
                        }
                        break;
                }
            }

            return writer.ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<byte>();
        }
    }

    private static string TruncateMessage(string? message, int maxLength)
    {
        if (string.IsNullOrEmpty(message)) return "Message";
        if (message.Length <= maxLength) return message;
        return message.Substring(0, maxLength - 3) + "...";
    }
}

/// <summary>
/// Writes Perfetto trace protobuf format manually.
/// Based on https://perfetto.dev/docs/reference/trace-packet-proto
/// </summary>
public class PerfettoTraceWriter
{
    private readonly MemoryStream _stream = new();
    private uint _sequenceId = 1;
    private bool _firstPacket = true;

    // Protobuf field numbers from Perfetto protos
    private const int TRACE_PACKET = 1;           // Trace.packet
    private const int TIMESTAMP = 8;              // TracePacket.timestamp
    private const int TRACK_EVENT = 11;           // TracePacket.track_event
    private const int TRACK_DESCRIPTOR = 60;      // TracePacket.track_descriptor
    private const int TRUSTED_PACKET_SEQ_ID = 10; // TracePacket.trusted_packet_sequence_id
    private const int SEQUENCE_FLAGS = 13;        // TracePacket.sequence_flags

    // TrackDescriptor fields
    private const int TD_UUID = 1;
    private const int TD_NAME = 2;
    private const int TD_PROCESS = 3;
    private const int TD_THREAD = 4;

    // ProcessDescriptor fields
    private const int PD_PID = 1;
    private const int PD_PROCESS_NAME = 6;

    // ThreadDescriptor fields
    private const int THD_PID = 1;
    private const int THD_TID = 2;
    private const int THD_THREAD_NAME = 5;

    // TrackEvent fields
    private const int TE_NAME = 23;
    private const int TE_TYPE = 9;
    private const int TE_TRACK_UUID = 11;
    private const int TE_CATEGORIES = 22;

    // TrackEvent.Type values
    private const int TYPE_SLICE_BEGIN = 1;
    private const int TYPE_SLICE_END = 2;
    private const int TYPE_INSTANT = 3;

    // Sequence flags
    private const int SEQ_INCREMENTAL_STATE_CLEARED = 1;
    private const int SEQ_NEEDS_INCREMENTAL_STATE = 2;

    public void WriteProcessTrackDescriptor(ulong uuid, uint pid, string name)
    {
        using var packet = new MemoryStream();

        // TrackDescriptor
        using var td = new MemoryStream();
        WriteVarint(td, TD_UUID, uuid);
        WriteString(td, TD_NAME, name);

        // ProcessDescriptor
        using var pd = new MemoryStream();
        WriteVarint(pd, PD_PID, pid);
        WriteString(pd, PD_PROCESS_NAME, name);
        WriteLengthDelimited(td, TD_PROCESS, pd.ToArray());

        WriteLengthDelimited(packet, TRACK_DESCRIPTOR, td.ToArray());
        WriteVarint(packet, TRUSTED_PACKET_SEQ_ID, _sequenceId);
        WriteSequenceFlags(packet);

        WriteLengthDelimited(_stream, TRACE_PACKET, packet.ToArray());
    }

    public void WriteThreadTrackDescriptor(ulong uuid, uint pid, uint tid, string name)
    {
        using var packet = new MemoryStream();

        // TrackDescriptor
        using var td = new MemoryStream();
        WriteVarint(td, TD_UUID, uuid);
        WriteString(td, TD_NAME, name);

        // ThreadDescriptor
        using var thd = new MemoryStream();
        WriteVarint(thd, THD_PID, pid);
        WriteVarint(thd, THD_TID, tid);
        WriteString(thd, THD_THREAD_NAME, name);
        WriteLengthDelimited(td, TD_THREAD, thd.ToArray());

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

        // TrackEvent
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

    // Protobuf encoding helpers
    private static void WriteVarint(Stream s, int fieldNumber, ulong value)
    {
        // Write field tag (field number << 3 | wire type 0 for varint)
        WriteRawVarint(s, (ulong)((fieldNumber << 3) | 0));
        WriteRawVarint(s, value);
    }

    private static void WriteString(Stream s, int fieldNumber, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteLengthDelimited(s, fieldNumber, bytes);
    }

    private static void WriteLengthDelimited(Stream s, int fieldNumber, byte[] data)
    {
        // Write field tag (field number << 3 | wire type 2 for length-delimited)
        WriteRawVarint(s, (ulong)((fieldNumber << 3) | 2));
        WriteRawVarint(s, (ulong)data.Length);
        s.Write(data, 0, data.Length);
    }

    private static void WriteRawVarint(Stream s, ulong value)
    {
        while (value > 127)
        {
            s.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }
}

// JSON serializable types
public class TraceDocument
{
    [JsonPropertyName("traceEvents")]
    public List<TraceEvent> TraceEvents { get; set; } = new();
}

public class TraceEvent
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("cat")]
    public string Category { get; set; } = "";

    [JsonPropertyName("ph")]
    public string Phase { get; set; } = "";

    [JsonPropertyName("ts")]
    public long Timestamp { get; set; }

    [JsonPropertyName("pid")]
    public int ProcessId { get; set; }

    [JsonPropertyName("tid")]
    public int ThreadId { get; set; }

    [JsonPropertyName("s")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; set; }

    [JsonPropertyName("args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TraceEventArgs? Args { get; set; }
}

public class TraceEventArgs
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    [JsonPropertyName("file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? File { get; set; }

    [JsonPropertyName("line")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Line { get; set; }

    [JsonPropertyName("nodeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int NodeId { get; set; }

    [JsonPropertyName("projectContextId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ProjectContextId { get; set; }

    [JsonPropertyName("targetId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TargetId { get; set; }

    [JsonPropertyName("taskId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TaskId { get; set; }
}

public class ErrorResult
{
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; set; }
}

// Source generator context for AOT/trimming compatibility
[JsonSerializable(typeof(TraceDocument))]
[JsonSerializable(typeof(ErrorResult))]
public partial class TraceJsonContext : JsonSerializerContext
{
}
