using ThorMeter.Infrastructure;

namespace ThorMeter.Tests;
public class TcpReassemblerTests
{
    // Framing fixtures only: length varint (body length + 4), then a known opcode.
    // They are deliberately not presented as valid damage payloads.
    private static readonly byte[] Frame = [8, 0x04, 0x38, 0, 0];

    [Fact]
    public void FragmentedFrameReassemblesOnceAndRetransmissionIsIgnored()
    {
        var tcp = new TcpReassembler();
        Assert.Empty(tcp.Push(100, Frame.AsSpan(0, 2)));
        Assert.Equal(Frame[1..], Assert.Single(tcp.Push(102, Frame.AsSpan(2))));
        Assert.Empty(tcp.Push(100, Frame));
        Assert.Equal(1, tcp.Frames);
        Assert.True(tcp.Retransmissions > 0);
    }

    [Fact]
    public void GapWaitsForMissingSegmentThenDrains()
    {
        var tcp = new TcpReassembler();
        Assert.Empty(tcp.Push(100, Frame.AsSpan(0, 2)));
        Assert.Empty(tcp.Push(104, Frame.AsSpan(4)));
        Assert.Equal(Frame[1..], Assert.Single(tcp.Push(102, Frame.AsSpan(2, 2))));
    }

    [Fact]
    public void MultiplePendingSegmentsDrainAcrossSequenceWrap()
    {
        var tcp = new TcpReassembler();
        Assert.Empty(tcp.Push(uint.MaxValue - 4, new byte[] { 10 }));
        Assert.Empty(tcp.Push(1, new byte[] { 0 }));
        Assert.Empty(tcp.Push(uint.MaxValue - 1, new byte[] { 0, 0, 0 }));
        Assert.Equal(new byte[] { 0x04, 0x38, 0, 0, 0, 0 }, Assert.Single(tcp.Push(uint.MaxValue - 3, new byte[] { 0x04, 0x38 })));
    }

    [Fact]
    public void OverlappingRetransmissionOnlyAppendsUnseenSuffix()
    {
        var tcp = new TcpReassembler();
        Assert.Empty(tcp.Push(100, Frame.AsSpan(0, 3)));
        Assert.Equal(Frame[1..], Assert.Single(tcp.Push(102, Frame.AsSpan(2))));
    }

    [Fact]
    public void MalformedDataIsBoundedAndResetAllowsRecovery()
    {
        var tcp = new TcpReassembler();
        tcp.Push(100, new byte[300_000]);
        Assert.True(tcp.BufferedBytes <= 256 * 1024);
        tcp.Reset();
        Assert.Equal(Frame[1..], Assert.Single(tcp.Push(1, Frame)));
    }
}

