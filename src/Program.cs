#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging.StructuredLogger;

Console.WriteLine("MSBuild Binlog to Perfetto Viewer loaded");

await System.Threading.Tasks.Task.Delay(-1); // Keep runtime alive for JS calls

[SupportedOSPlatform("browser")]
public partial class BinlogConverter
{
    private static CancellationTokenSource? _cancellationTokenSource;
    private static readonly object _lock = new();

    [JSImport("globalThis.postProgress")]
    private static partial void PostProgressInternal(string message, int current, int total);

    private static void PostProgress(string message, int current, int total)
    {
        try
        {
            PostProgressInternal(message, current, total);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PostProgress failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Cancels any in-progress conversion.
    /// </summary>
    [JSExport]
    public static void Cancel()
    {
        lock (_lock)
        {
            _cancellationTokenSource?.Cancel();
        }
    }

    /// <summary>
    /// Converts an MSBuild binary log to Perfetto protobuf trace format.
    /// Throws OperationCanceledException if cancelled.
    /// </summary>
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
        CancellationTokenSource cts;
        lock (_lock)
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            cts = _cancellationTokenSource;
        }

        var token = cts.Token;
        var writer = new PerfettoTraceWriter();
        var nodeTrackWritten = new HashSet<int>();
        var trackUuids = new Dictionary<int, ulong>();
        ulong nextTrackUuid = 1;

        using var stream = new MemoryStream(binlogData);
        var reader = new BinLogReader();

        long? buildStartTime = null;

        long totalBytes = stream.Length;
        int lastProgressPercent = 0;
        int recordCount = 0;

        PostProgress("Reading binlog records...", 0, 100);

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
            token.ThrowIfCancellationRequested();

            var args = record.Args;
            if (args == null) continue;

            recordCount++;

            int progressPercent = (int)((stream.Position * 100) / totalBytes);
            if (progressPercent >= lastProgressPercent + 5)
            {
                lastProgressPercent = progressPercent;
                PostProgress($"Processing records ({recordCount:N0} read)...", progressPercent, 100);
            }

            var ctx = args.BuildEventContext;
            long timestamp = args.Timestamp.Ticks * 100; // Convert to nanoseconds

            if (buildStartTime == null)
                buildStartTime = timestamp;

            long relativeTimeNs = timestamp - buildStartTime.Value;

            int nodeId = ctx?.NodeId ?? 0;

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
                        var trackUuid = GetTrackUuid(-1);
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

        PostProgress($"Writing protobuf trace ({recordCount:N0} records)...", 95, 100);

        var result = writer.ToArray();

        PostProgress("Complete!", 100, 100);
        return result;
    }

    private static string TruncateMessage(string? message, int maxLength)
    {
        if (string.IsNullOrEmpty(message)) return "Message";
        if (message.Length <= maxLength) return message;
        return string.Concat(message.AsSpan(0, maxLength - 3), "...");
    }
}

// PerfettoTraceWriter is in PerfettoTraceWriter.cs
