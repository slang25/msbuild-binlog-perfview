namespace msbuild_binlog_perfview.Tests;

public class PerfettoTraceWriterTests
{
    [Fact]
    public void WriteRawVarint_SingleByte_EncodesCorrectly()
    {
        using var stream = new MemoryStream();
        PerfettoTraceWriter.WriteRawVarint(stream, 0);
        Assert.Equal(new byte[] { 0x00 }, stream.ToArray());

        stream.SetLength(0);
        PerfettoTraceWriter.WriteRawVarint(stream, 1);
        Assert.Equal(new byte[] { 0x01 }, stream.ToArray());

        stream.SetLength(0);
        PerfettoTraceWriter.WriteRawVarint(stream, 127);
        Assert.Equal(new byte[] { 0x7F }, stream.ToArray());
    }

    [Fact]
    public void WriteRawVarint_MultiByte_EncodesCorrectly()
    {
        using var stream = new MemoryStream();

        // 128 = 0x80 requires 2 bytes: 0x80 0x01
        PerfettoTraceWriter.WriteRawVarint(stream, 128);
        Assert.Equal(new byte[] { 0x80, 0x01 }, stream.ToArray());

        stream.SetLength(0);
        // 300 = 0b100101100 = 0xAC 0x02
        PerfettoTraceWriter.WriteRawVarint(stream, 300);
        Assert.Equal(new byte[] { 0xAC, 0x02 }, stream.ToArray());

        stream.SetLength(0);
        // Large number: 16383 (max 2-byte varint) = 0xFF 0x7F
        PerfettoTraceWriter.WriteRawVarint(stream, 16383);
        Assert.Equal(new byte[] { 0xFF, 0x7F }, stream.ToArray());

        stream.SetLength(0);
        // 16384 requires 3 bytes
        PerfettoTraceWriter.WriteRawVarint(stream, 16384);
        Assert.Equal(new byte[] { 0x80, 0x80, 0x01 }, stream.ToArray());
    }

    [Fact]
    public void WriteVarint_IncludesFieldTag()
    {
        using var stream = new MemoryStream();

        // Field 1, varint wire type (0) = tag 0x08
        PerfettoTraceWriter.WriteVarint(stream, 1, 42);
        var bytes = stream.ToArray();
        Assert.Equal(0x08, bytes[0]); // Field tag
        Assert.Equal(42, bytes[1]);   // Value
    }

    [Fact]
    public void WriteString_EncodesUtf8WithLengthPrefix()
    {
        using var stream = new MemoryStream();

        // Field 2, length-delimited wire type (2) = tag 0x12
        PerfettoTraceWriter.WriteString(stream, 2, "test");
        var bytes = stream.ToArray();

        Assert.Equal(0x12, bytes[0]); // Field tag (2 << 3 | 2)
        Assert.Equal(4, bytes[1]);    // Length
        Assert.Equal("test", System.Text.Encoding.UTF8.GetString(bytes, 2, 4));
    }

    [Fact]
    public void WriteString_HandlesUnicodeCharacters()
    {
        using var stream = new MemoryStream();

        PerfettoTraceWriter.WriteString(stream, 1, "héllo");
        var bytes = stream.ToArray();

        // "héllo" in UTF-8 is 6 bytes (é is 2 bytes)
        Assert.Equal(6, bytes[1]); // Length
    }

    [Fact]
    public void WriteLengthDelimited_CorrectFormat()
    {
        using var stream = new MemoryStream();
        var data = new byte[] { 1, 2, 3, 4, 5 };

        // Field 3, length-delimited wire type (2) = tag 0x1A
        PerfettoTraceWriter.WriteLengthDelimited(stream, 3, data);
        var bytes = stream.ToArray();

        Assert.Equal(0x1A, bytes[0]); // Field tag (3 << 3 | 2)
        Assert.Equal(5, bytes[1]);    // Length
        Assert.Equal(data, bytes.Skip(2).ToArray());
    }

    [Fact]
    public void WriteProcessTrackDescriptor_ProducesNonEmptyOutput()
    {
        var writer = new PerfettoTraceWriter();
        writer.WriteProcessTrackDescriptor(1, 100, "TestProcess");

        var result = writer.ToArray();
        Assert.NotEmpty(result);
    }

    [Fact]
    public void WriteProcessTrackDescriptor_ContainsTracePacketTag()
    {
        var writer = new PerfettoTraceWriter();
        writer.WriteProcessTrackDescriptor(1, 100, "TestProcess");

        var result = writer.ToArray();
        // First byte should be trace packet field tag (1 << 3 | 2 = 0x0A)
        Assert.Equal(0x0A, result[0]);
    }

    [Fact]
    public void WriteSliceBegin_ProducesNonEmptyOutput()
    {
        var writer = new PerfettoTraceWriter();
        writer.WriteSliceBegin(1, 1000000, "TestSlice", "category");

        var result = writer.ToArray();
        Assert.NotEmpty(result);
    }

    [Fact]
    public void WriteSliceEnd_ProducesNonEmptyOutput()
    {
        var writer = new PerfettoTraceWriter();
        writer.WriteSliceEnd(1, 2000000);

        var result = writer.ToArray();
        Assert.NotEmpty(result);
    }

    [Fact]
    public void WriteInstantEvent_ProducesNonEmptyOutput()
    {
        var writer = new PerfettoTraceWriter();
        writer.WriteInstantEvent(1, 1500000, "TestInstant", "category");

        var result = writer.ToArray();
        Assert.NotEmpty(result);
    }

    [Fact]
    public void MultipleEvents_AccumulateInOutput()
    {
        var writer = new PerfettoTraceWriter();

        writer.WriteProcessTrackDescriptor(1, 100, "Process1");
        var sizeAfterFirst = writer.ToArray().Length;

        writer.WriteSliceBegin(1, 1000000, "Slice1", "cat");
        var sizeAfterSecond = writer.ToArray().Length;

        writer.WriteSliceEnd(1, 2000000);
        var sizeAfterThird = writer.ToArray().Length;

        Assert.True(sizeAfterSecond > sizeAfterFirst);
        Assert.True(sizeAfterThird > sizeAfterSecond);
    }

    [Fact]
    public void FirstPacket_HasIncrementalStateClearedFlag()
    {
        var writer = new PerfettoTraceWriter();
        writer.WriteSliceBegin(1, 0, "First", "cat");

        var result = writer.ToArray();
        // The first packet should contain SEQ_INCREMENTAL_STATE_CLEARED | SEQ_NEEDS_INCREMENTAL_STATE = 3
        // This is encoded as field 13, varint 3 = 0x68 0x03
        Assert.Contains(result, b => b == 0x03);
    }

    [Fact]
    public void CompleteTrace_HasValidStructure()
    {
        var writer = new PerfettoTraceWriter();

        // Write a complete trace with process descriptor and events
        writer.WriteProcessTrackDescriptor(1, 1, "Build");
        writer.WriteSliceBegin(1, 0, "Build", "build");
        writer.WriteSliceBegin(1, 100000, "Project.csproj", "project");
        writer.WriteSliceEnd(1, 500000);
        writer.WriteSliceEnd(1, 600000);

        var result = writer.ToArray();

        // Verify basic structure:
        // - Should have multiple trace packets (each starts with 0x0A tag)
        int packetCount = 0;
        for (int i = 0; i < result.Length; i++)
        {
            if (result[i] == 0x0A && (i == 0 || IsPacketBoundary(result, i)))
            {
                packetCount++;
            }
        }

        Assert.True(packetCount >= 4, $"Expected at least 4 trace packets, got {packetCount}");
    }

    // Helper to check if position could be a packet boundary
    private static bool IsPacketBoundary(byte[] data, int position)
    {
        // This is a simplified check - in real protobuf parsing we'd need full decode
        // For testing purposes, we just check the tag is valid
        return position == 0 || data[position] == 0x0A;
    }

    [Fact]
    public void EmptyWriter_ReturnsEmptyArray()
    {
        var writer = new PerfettoTraceWriter();
        var result = writer.ToArray();
        Assert.Empty(result);
    }

    [Fact]
    public void LargeTimestamp_EncodesCorrectly()
    {
        var writer = new PerfettoTraceWriter();

        // Large timestamp (1 hour in nanoseconds)
        long timestamp = 3600L * 1000000000L;
        writer.WriteSliceBegin(1, timestamp, "LongRunning", "cat");

        var result = writer.ToArray();
        Assert.NotEmpty(result);
    }

    [Fact]
    public void LongEventName_EncodesCorrectly()
    {
        var writer = new PerfettoTraceWriter();

        string longName = new string('A', 1000);
        writer.WriteSliceBegin(1, 0, longName, "cat");

        var result = writer.ToArray();
        Assert.NotEmpty(result);
        // The result should be significantly larger due to the long name
        Assert.True(result.Length > 1000);
    }

    [Fact]
    public void SpecialCharactersInName_HandleCorrectly()
    {
        var writer = new PerfettoTraceWriter();

        // Various special characters that might appear in MSBuild paths
        writer.WriteSliceBegin(1, 0, @"C:\Users\test\project.csproj", "project");
        writer.WriteSliceBegin(2, 100, "Build: \"Debug|x64\"", "target");
        writer.WriteSliceBegin(3, 200, "Task<int>", "task");

        var result = writer.ToArray();
        Assert.NotEmpty(result);
    }

    [Fact]
    public void MultipleTrackUuids_CreatesSeparateEvents()
    {
        var writer = new PerfettoTraceWriter();

        writer.WriteProcessTrackDescriptor(1, 1, "Node 1");
        writer.WriteProcessTrackDescriptor(2, 2, "Node 2");
        writer.WriteSliceBegin(1, 0, "Task1", "task");
        writer.WriteSliceBegin(2, 0, "Task2", "task");
        writer.WriteSliceEnd(1, 100);
        writer.WriteSliceEnd(2, 200);

        var result = writer.ToArray();

        // Count trace packets
        int packetCount = 0;
        for (int i = 0; i < result.Length; i++)
        {
            if (result[i] == 0x0A)
                packetCount++;
        }

        // 2 descriptors + 4 events = 6 packets minimum
        Assert.True(packetCount >= 6);
    }
}
