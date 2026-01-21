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

    // Special node IDs
    private const int BuildNodeId = -1;
    private const int EvaluationNodeId = -2;

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
        bool includeErrors,
        bool includeEvaluation = false)
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

        // Track process descriptors (one per node)
        var processTrackWritten = new HashSet<int>();
        var processTrackUuids = new Dictionary<int, ulong>();

        // Track thread descriptors (one per project instance within a node)
        var threadTrackWritten = new HashSet<(int nodeId, int projectInstanceId)>();
        var threadTrackUuids = new Dictionary<(int nodeId, int projectInstanceId), ulong>();

        // Track project names by (nodeId, projectInstanceId) for use in later events
        var projectNames = new Dictionary<(int nodeId, int projectInstanceId), string>();

        // Track MSBuild task invocations for P2P flow arrows
        var msbuildTaskStarts = new Dictionary<int, (long timestamp, ulong trackUuid, ulong flowId)>();

        ulong nextTrackUuid = 1;
        ulong nextFlowId = 1;

        using var stream = new MemoryStream(binlogData);
        var reader = new BinLogReader();

        long? buildStartTime = null;

        long totalBytes = stream.Length;
        int lastProgressPercent = 0;
        int recordCount = 0;

        PostProgress("Reading binlog records...", 0, 100);

        ulong GetProcessTrackUuid(int nodeId)
        {
            if (!processTrackUuids.TryGetValue(nodeId, out var uuid))
            {
                uuid = nextTrackUuid++;
                processTrackUuids[nodeId] = uuid;
            }
            return uuid;
        }

        ulong GetThreadTrackUuid(int nodeId, int projectInstanceId)
        {
            var key = (nodeId, projectInstanceId);
            if (!threadTrackUuids.TryGetValue(key, out var uuid))
            {
                uuid = nextTrackUuid++;
                threadTrackUuids[key] = uuid;
            }
            return uuid;
        }

        void EnsureProcessTrack(int nodeId, string? name = null)
        {
            if (!processTrackWritten.Contains(nodeId))
            {
                processTrackWritten.Add(nodeId);
                var trackUuid = GetProcessTrackUuid(nodeId);
                var trackName = name ?? (nodeId >= 0 ? $"Node {nodeId}" : "Build");
                var pid = nodeId >= 0 ? (uint)nodeId : 0u;
                writer.WriteProcessTrackDescriptor(trackUuid, pid, trackName);
            }
        }

        void EnsureThreadTrack(int nodeId, int projectInstanceId, string projectName)
        {
            var key = (nodeId, projectInstanceId);
            // Store project name for later events (warnings, errors, messages)
            if (!projectNames.ContainsKey(key) && !string.IsNullOrEmpty(projectName))
            {
                projectNames[key] = projectName;
            }
            if (!threadTrackWritten.Contains(key))
            {
                EnsureProcessTrack(nodeId);
                threadTrackWritten.Add(key);
                var threadUuid = GetThreadTrackUuid(nodeId, projectInstanceId);
                var processUuid = GetProcessTrackUuid(nodeId);
                var pid = nodeId >= 0 ? nodeId : 0;
                writer.WriteThreadTrackDescriptor(threadUuid, processUuid, pid, projectInstanceId, projectName);
            }
        }

        // Ensure thread track exists, using stored project name if available
        void EnsureThreadTrackWithFallback(int nodeId, int projectInstanceId)
        {
            var key = (nodeId, projectInstanceId);
            if (!threadTrackWritten.Contains(key))
            {
                var name = projectNames.TryGetValue(key, out var storedName) ? storedName : $"Project {projectInstanceId}";
                EnsureThreadTrack(nodeId, projectInstanceId, name);
            }
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
            int projectInstanceId = ctx?.ProjectInstanceId ?? 0;

            switch (args)
            {
                case BuildStartedEventArgs:
                    {
                        EnsureProcessTrack(BuildNodeId, "Build");
                        var trackUuid = GetProcessTrackUuid(BuildNodeId);
                        writer.WriteSliceBegin(trackUuid, relativeTimeNs, "Build", "build");
                    }
                    break;

                case BuildFinishedEventArgs:
                    {
                        var trackUuid = GetProcessTrackUuid(BuildNodeId);
                        writer.WriteSliceEnd(trackUuid, relativeTimeNs);
                    }
                    break;

                case ProjectEvaluationStartedEventArgs evalStarted when includeEvaluation:
                    {
                        var projectName = Path.GetFileName(evalStarted.ProjectFile) ?? "Project";
                        // Evaluation events may have InvalidNodeId, use special Evaluation process
                        var evalNodeId = nodeId == BuildEventContext.InvalidNodeId ? EvaluationNodeId : nodeId;
                        var evalProjectInstanceId = ctx?.ProjectInstanceId ?? 0;

                        if (evalNodeId == EvaluationNodeId)
                        {
                            EnsureProcessTrack(EvaluationNodeId, "Evaluation");
                            EnsureThreadTrack(EvaluationNodeId, evalProjectInstanceId, projectName);
                        }
                        else
                        {
                            EnsureThreadTrack(evalNodeId, evalProjectInstanceId, projectName);
                        }

                        var trackUuid = GetThreadTrackUuid(evalNodeId, evalProjectInstanceId);
                        writer.WriteSliceBegin(trackUuid, relativeTimeNs, $"{projectName} (evaluation)", "evaluation");
                    }
                    break;

                case ProjectEvaluationFinishedEventArgs evalFinished when includeEvaluation:
                    {
                        var evalNodeId = nodeId == BuildEventContext.InvalidNodeId ? EvaluationNodeId : nodeId;
                        var evalProjectInstanceId = ctx?.ProjectInstanceId ?? 0;
                        var trackUuid = GetThreadTrackUuid(evalNodeId, evalProjectInstanceId);
                        writer.WriteSliceEnd(trackUuid, relativeTimeNs);
                    }
                    break;

                case ProjectStartedEventArgs projectStarted when includeProjects:
                    {
                        var projectName = Path.GetFileName(projectStarted.ProjectFile) ?? "Project";
                        EnsureThreadTrack(nodeId, projectInstanceId, projectName);
                        var trackUuid = GetThreadTrackUuid(nodeId, projectInstanceId);

                        // Check if this project was triggered by an MSBuild task (P2P)
                        var parentCtx = projectStarted.ParentProjectBuildEventContext;
                        if (parentCtx != null &&
                            parentCtx.ProjectInstanceId != BuildEventContext.InvalidProjectInstanceId &&
                            msbuildTaskStarts.TryGetValue(parentCtx.ProjectInstanceId, out var parentTask))
                        {
                            // Write project start with terminating flow from parent MSBuild task
                            writer.WriteSliceBeginWithTerminatingFlow(trackUuid, relativeTimeNs, projectName, "project", parentTask.flowId);
                        }
                        else
                        {
                            writer.WriteSliceBegin(trackUuid, relativeTimeNs, projectName, "project");
                        }
                    }
                    break;

                case ProjectFinishedEventArgs when includeProjects:
                    {
                        var trackUuid = GetThreadTrackUuid(nodeId, projectInstanceId);
                        writer.WriteSliceEnd(trackUuid, relativeTimeNs);
                    }
                    break;

                case TargetStartedEventArgs targetStarted when includeTargets:
                    {
                        EnsureThreadTrackWithFallback(nodeId, projectInstanceId);
                        var trackUuid = GetThreadTrackUuid(nodeId, projectInstanceId);
                        writer.WriteSliceBegin(trackUuid, relativeTimeNs, targetStarted.TargetName, "target");
                    }
                    break;

                case TargetFinishedEventArgs when includeTargets:
                    {
                        var trackUuid = GetThreadTrackUuid(nodeId, projectInstanceId);
                        writer.WriteSliceEnd(trackUuid, relativeTimeNs);
                    }
                    break;

                case TaskStartedEventArgs taskStarted when includeTasks:
                    {
                        EnsureThreadTrackWithFallback(nodeId, projectInstanceId);
                        var trackUuid = GetThreadTrackUuid(nodeId, projectInstanceId);

                        // Track MSBuild task invocations for P2P flow arrows
                        if (taskStarted.TaskName.EndsWith("MSBuild"))
                        {
                            var flowId = nextFlowId++;
                            msbuildTaskStarts[projectInstanceId] = (relativeTimeNs, trackUuid, flowId);
                            writer.WriteSliceBegin(trackUuid, relativeTimeNs, $"{taskStarted.TaskName} (yielded)", "task", flowId);
                        }
                        else
                        {
                            writer.WriteSliceBegin(trackUuid, relativeTimeNs, taskStarted.TaskName, "task");
                        }
                    }
                    break;

                case TaskFinishedEventArgs taskFinished when includeTasks:
                    {
                        var trackUuid = GetThreadTrackUuid(nodeId, projectInstanceId);
                        writer.WriteSliceEnd(trackUuid, relativeTimeNs);

                        // Clean up MSBuild task tracking
                        if (taskFinished.TaskName.EndsWith("MSBuild"))
                        {
                            msbuildTaskStarts.Remove(projectInstanceId);
                        }
                    }
                    break;

                case BuildWarningEventArgs warning when includeWarnings:
                    {
                        EnsureThreadTrackWithFallback(nodeId, projectInstanceId);
                        var trackUuid = GetThreadTrackUuid(nodeId, projectInstanceId);
                        writer.WriteInstantEvent(trackUuid, relativeTimeNs, $"Warning: {warning.Code}", "warning");
                    }
                    break;

                case BuildErrorEventArgs error when includeErrors:
                    {
                        EnsureThreadTrackWithFallback(nodeId, projectInstanceId);
                        var trackUuid = GetThreadTrackUuid(nodeId, projectInstanceId);
                        writer.WriteInstantEvent(trackUuid, relativeTimeNs, $"Error: {error.Code}", "error");
                    }
                    break;

                case BuildMessageEventArgs message when includeMessages && message.Importance == MessageImportance.High:
                    {
                        EnsureThreadTrackWithFallback(nodeId, projectInstanceId);
                        var trackUuid = GetThreadTrackUuid(nodeId, projectInstanceId);
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
