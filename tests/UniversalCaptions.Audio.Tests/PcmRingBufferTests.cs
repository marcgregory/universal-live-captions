using UniversalCaptions.Audio.Buffering;

namespace UniversalCaptions.Audio.Tests;

public sealed class PcmRingBufferTests
{
    [Fact]
    public void Write_Then_Read_ReturnsSamplesInOrder()
    {
        var buffer = new PcmRingBuffer(16);
        buffer.Write([1f, 2f, 3f, 4f]);

        var destination = new float[4];
        int read = buffer.Read(destination);

        Assert.Equal(4, read);
        Assert.Equal([1f, 2f, 3f, 4f], destination);
        Assert.Equal(0, buffer.ReadableCount);
    }

    [Fact]
    public void Partial_Read_PreservesRemainingOrder()
    {
        var buffer = new PcmRingBuffer(16);
        buffer.Write([1f, 2f, 3f, 4f]);

        var first = new float[2];
        buffer.Read(first);
        var second = new float[2];
        buffer.Read(second);

        Assert.Equal([1f, 2f], first);
        Assert.Equal([3f, 4f], second);
    }

    [Fact]
    public void WrapAround_ReadsInFifoOrder()
    {
        var buffer = new PcmRingBuffer(4);
        buffer.Write([1f, 2f, 3f, 4f]);

        var consumed = new float[2];
        buffer.Read(consumed);

        buffer.Write([5f, 6f]);

        var rest = new float[4];
        int read = buffer.Read(rest);

        Assert.Equal(4, read);
        Assert.Equal([3f, 4f, 5f, 6f], rest);
    }

    [Fact]
    public void Overflow_DropsOldestSamples()
    {
        var buffer = new PcmRingBuffer(4);
        buffer.Write([1f, 2f, 3f, 4f, 5f, 6f]);

        Assert.Equal(4, buffer.ReadableCount);

        var destination = new float[4];
        buffer.Read(destination);

        Assert.Equal([3f, 4f, 5f, 6f], destination);
    }

    [Fact]
    public void Read_MoreThanAvailable_ReturnsAvailable()
    {
        var buffer = new PcmRingBuffer(8);
        buffer.Write([1f, 2f]);

        var destination = new float[8];
        int read = buffer.Read(destination);

        Assert.Equal(2, read);
        Assert.Equal([1f, 2f], destination[..read]);
    }

    [Fact]
    public void Clear_ResetsBuffer()
    {
        var buffer = new PcmRingBuffer(8);
        buffer.Write([1f, 2f, 3f]);

        buffer.Clear();

        Assert.Equal(0, buffer.ReadableCount);
        Assert.Equal(8, buffer.CapacityInSamples);
    }

    [Fact]
    public void Capacity_ReturnsConfiguredValue()
    {
        var buffer = new PcmRingBuffer(123);
        Assert.Equal(123, buffer.CapacityInSamples);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_RejectsNonPositiveCapacity(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmRingBuffer(capacity));
    }
}
