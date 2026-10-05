using AutoEnvPlus.Core.Networking;

namespace AutoEnvPlus.Core.Tests;

public sealed class BoundedHttpResponseReaderTests
{
    [Fact]
    public async Task ReadBytesAsync_RejectsChunkedBodyThatExceedsActualByteLimit()
    {
        await using NonSeekableReadStream stream = new(new byte[81_920]);
        using StreamContent content = new(stream);
        Assert.Null(content.Headers.ContentLength);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BoundedHttpResponseReader.ReadBytesAsync(
                content,
                maximumBytes: 32,
                bodyTimeout: TimeSpan.FromSeconds(1),
                description: "test metadata"));

        Assert.Contains("32-byte limit", exception.Message, StringComparison.Ordinal);
        Assert.Equal(33, stream.BytesRead);
    }

    [Fact]
    public async Task ReadBytesAsync_RejectsDeclaredBodyOverByteLimit()
    {
        using ByteArrayContent content = new([0]);
        content.Headers.ContentLength = 33;

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BoundedHttpResponseReader.ReadBytesAsync(
                content,
                maximumBytes: 32,
                bodyTimeout: TimeSpan.FromSeconds(1),
                description: "test metadata"));

        Assert.Contains("32-byte limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadBytesAsync_AcceptsBodyAtExactByteLimit()
    {
        byte[] expected = new byte[32];
        await using NonSeekableReadStream stream = new(expected);
        using StreamContent content = new(stream);

        byte[] actual = await BoundedHttpResponseReader.ReadBytesAsync(
            content,
            maximumBytes: 32,
            bodyTimeout: TimeSpan.FromSeconds(1),
            description: "test metadata");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ReadBytesAsync_AppliesBodyTimeoutAfterHeaders()
    {
        await using NeverEndingReadStream stream = new();
        using StreamContent content = new(stream);

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            BoundedHttpResponseReader.ReadBytesAsync(
                content,
                maximumBytes: 32,
                bodyTimeout: TimeSpan.FromMilliseconds(50),
                description: "test metadata"));

        Assert.Contains("did not complete", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadBytesAsync_PropagatesCallerCancellationWithoutConvertingItToTimeout()
    {
        await using NeverEndingReadStream stream = new();
        using StreamContent content = new(stream);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedHttpResponseReader.ReadBytesAsync(
                content,
                maximumBytes: 32,
                bodyTimeout: TimeSpan.FromSeconds(5),
                description: "test metadata",
                cancellationToken: cancellation.Token));
    }

    private sealed class NonSeekableReadStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        private int _bytesRead;

        public int BytesRead => _bytesRead;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            _bytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int read = await _inner.ReadAsync(buffer, cancellationToken);
            _bytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    private sealed class NeverEndingReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
