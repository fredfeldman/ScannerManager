using System.Text;
using System.Threading.Channels;
using System.IO.Ports;
using SDS200_ControlApp.Domain;
using SDS200_ControlApp.Serial;

namespace SDS200_ControlApp.Serial.Tests;

public class SerialPortTransportTests
{
    [Fact]
    public void GetPortNames_ReturnsSortedNames()
    {
        var ports = SerialPortTransport.GetPortNames();

        Assert.Equal(ports.OrderBy(port => port), ports);
    }

    [Fact]
    public async Task SendCommandAsync_RequiresConnection()
    {
        await using var transport = new SerialPortTransport(new InMemoryLogger());

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendCommandAsync("MDL"));
    }

    [Fact]
    public async Task ConnectAsync_RejectsNonPositiveReadTimeoutBeforeOpeningPort()
    {
        await using var transport = new SerialPortTransport(new InMemoryLogger());
        var settings = new ScannerConnectionSettings("COM_INVALID", 115200, Parity.None, 8, StopBits.One, Handshake.None, 0);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => transport.ConnectAsync(settings));
    }

    [Theory]
    [InlineData("", 115200, 8, 3000)]
    [InlineData("COM_INVALID", 0, 8, 3000)]
    [InlineData("COM_INVALID", 115200, 4, 3000)]
    [InlineData("COM_INVALID", 115200, 9, 3000)]
    [InlineData("COM_INVALID", 115200, 8, 0)]
    public async Task ConnectAsync_RejectsInvalidSettingsBeforeOpeningPort(string portName, int baudRate, int dataBits, int readTimeout)
    {
        await using var transport = new SerialPortTransport(new InMemoryLogger());
        var settings = new ScannerConnectionSettings(portName, baudRate, Parity.None, dataBits, StopBits.One, Handshake.None, readTimeout);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => transport.ConnectAsync(settings));
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_RejectsNullSettings()
    {
        await using var transport = new SerialPortTransport(new InMemoryLogger());

        await Assert.ThrowsAsync<ArgumentNullException>(() => transport.ConnectAsync(null!));
    }

    [Theory]
    [InlineData(999, 1, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 1, 999)]
    public async Task ConnectAsync_RejectsUndefinedSerialEnumValues(int parityValue, int stopBitsValue, int handshakeValue)
    {
        await using var transport = new SerialPortTransport(new InMemoryLogger());
        var settings = new ScannerConnectionSettings(
            "COM_INVALID",
            115200,
            (Parity)parityValue,
            8,
            (StopBits)stopBitsValue,
            (Handshake)handshakeValue);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => transport.ConnectAsync(settings));
    }

    [Fact]
    public async Task ConnectAsync_HonorsCancellationBeforeOpeningPort()
    {
        await using var transport = new SerialPortTransport(new InMemoryLogger());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var settings = new ScannerConnectionSettings("COM_INVALID", 115200, Parity.None, 8, StopBits.One, Handshake.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ConnectAsync(settings, cancellation.Token));
    }

    [Fact]
    public async Task SendCommandAsync_PreservesAdditionalFramesFromTheSameRead()
    {
        var stream = new ScriptedDuplexStream("FIRST,OK\rSECOND,OK\r"u8.ToArray());
        await using var transport = new SerialPortTransport(new InMemoryLogger(), stream);

        var firstResponse = await transport.SendCommandAsync("FIRST");
        var secondResponse = await transport.SendCommandAsync("SECOND");

        Assert.Equal("FIRST,OK\r", firstResponse);
        Assert.Equal("SECOND,OK\r", secondResponse);
        Assert.Equal("FIRST\rSECOND\r", stream.WrittenText);
    }

    [Fact]
    public async Task SendCommandAsync_ReassemblesFragmentedResponseFrames()
    {
        var stream = new ScriptedDuplexStream("FIRST,"u8.ToArray(), "OK\r"u8.ToArray());
        await using var transport = new SerialPortTransport(new InMemoryLogger(), stream);

        var response = await transport.SendCommandAsync("FIRST");

        Assert.Equal("FIRST,OK\r", response);
    }

    [Fact]
    public async Task SendCommandAsync_SerializesConcurrentRequestResponsePairs()
    {
        var stream = new ScriptedDuplexStream();
        stream.OnCommandWritten = command => stream.EnqueueResponse(Encoding.ASCII.GetBytes($"{command.TrimEnd('\r')},OK\r"));
        await using var transport = new SerialPortTransport(new InMemoryLogger(), stream);

        var responses = await Task.WhenAll(
            transport.SendCommandAsync("FIRST"),
            transport.SendCommandAsync("SECOND"));

        Assert.Contains("FIRST,OK\r", responses);
        Assert.Contains("SECOND,OK\r", responses);
        Assert.True(stream.WrittenText is "FIRST\rSECOND\r" or "SECOND\rFIRST\r");
    }

    [Fact]
    public async Task SendCommandAsync_RejectsCommandsAfterDisconnect()
    {
        var stream = new ScriptedDuplexStream();
        await using var transport = new SerialPortTransport(new InMemoryLogger(), stream);

        await transport.DisconnectAsync();

        Assert.False(transport.IsConnected);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendCommandAsync("MDL"));
    }

    [Fact]
    public async Task DisconnectAsync_WaitsForInFlightCommandBeforeDisposingStream()
    {
        var stream = new ScriptedDuplexStream("FIRST,OK\r"u8.ToArray()) { BlockReads = true };
        await using var transport = new SerialPortTransport(new InMemoryLogger(), stream);

        var sendTask = transport.SendCommandAsync("FIRST");
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var disconnectTask = transport.DisconnectAsync();

        Assert.False(disconnectTask.IsCompleted);
        stream.ContinueRead.TrySetResult();
        Assert.Equal("FIRST,OK\r", await sendTask);
        await disconnectTask;

        Assert.True(stream.IsDisposed);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task DisconnectAsync_StopsPushStartedWhileDisconnectIsWaiting()
    {
        var stream = new ScriptedDuplexStream { BlockWrites = true };
        await using var transport = new SerialPortTransport(new InMemoryLogger(), stream);

        var startPushTask = transport.StartPushAsync("PSI,1000", _ => { });
        await stream.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var disconnectTask = transport.DisconnectAsync();
        Assert.False(disconnectTask.IsCompleted);

        stream.ContinueWrite.TrySetResult();
        await startPushTask;
        await disconnectTask;

        Assert.False(transport.IsPushActive);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task DisconnectAsync_StopsPushReaderAndIsIdempotent()
    {
        var stream = new ScriptedDuplexStream();
        await using var transport = new SerialPortTransport(new InMemoryLogger(), stream);
        await transport.StartPushAsync("PSI,1000", _ => { });

        await transport.DisconnectAsync();
        await transport.DisconnectAsync();

        Assert.False(transport.IsPushActive);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task StartPushAsync_ReportsResponseHandlerFailure()
    {
        var logger = new InMemoryLogger();
        var entries = new List<LogEntry>();
        logger.EntryWritten += entries.Add;
        var stream = new ScriptedDuplexStream();
        await using var transport = new SerialPortTransport(logger, stream);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.PushStreamFailed += exception => failure.TrySetResult(exception);

        await transport.StartPushAsync("PSI,1000", _ => throw new InvalidOperationException("consumer failed"));
        stream.EnqueueResponse("<ScannerInfo />\r"u8.ToArray());

        var exception = await failure.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains(entries, entry => entry.Level == LogLevel.Error && entry.Command == "PSI");
        await transport.StopPushAsync();
    }

    [Fact]
    public async Task SendCommandAsync_TimesOutWhenScannerDoesNotRespond()
    {
        var stream = new ScriptedDuplexStream();
        await using var transport = new SerialPortTransport(new InMemoryLogger(), stream, readTimeoutMilliseconds: 50);

        await Assert.ThrowsAsync<TimeoutException>(() => transport.SendCommandAsync("MDL"));
    }

    [Fact]
    public async Task SendCommandAsync_RejectsOversizedResponseFrame()
    {
        var oversizedResponse = Encoding.ASCII.GetBytes(new string('A', 65 * 1024) + "\r");
        var stream = new ScriptedDuplexStream(oversizedResponse);
        await using var transport = new SerialPortTransport(new InMemoryLogger(), stream);

        await Assert.ThrowsAsync<InvalidDataException>(() => transport.SendCommandAsync("MDL"));
    }

    [Fact]
    public async Task SendCommandAsync_AcceptsMaximumAllowedResponseFrame()
    {
        var maximumResponse = Encoding.ASCII.GetBytes(new string('A', (64 * 1024) - 1) + "\r");
        var stream = new ScriptedDuplexStream(maximumResponse);
        await using var transport = new SerialPortTransport(new InMemoryLogger(), stream);

        var response = await transport.SendCommandAsync("MDL");

        Assert.Equal(maximumResponse.Length, Encoding.ASCII.GetByteCount(response));
        Assert.EndsWith("\r", response);
    }

    [Fact]
    public async Task SendCommandDuringPushAndWaitAsync_ResolvesMatchingAcknowledgement()
    {
        var stream = new ScriptedDuplexStream();
        await using var transport = new SerialPortTransport(new InMemoryLogger(), stream);
        await transport.StartPushAsync("PSI,1000", _ => { });

        stream.OnCommandWritten = command =>
        {
            if (command == "POF\r")
            {
                stream.EnqueueResponse("POF,OK\r"u8.ToArray());
            }
        };
        var response = await transport.SendCommandDuringPushAndWaitAsync(
            "POF",
            value => value.TrimEnd('\r', '\n') == "POF,OK",
            500);

        Assert.Equal("POF,OK\r", response);
        await transport.StopPushAsync();
    }

    [Fact]
    public async Task SendCommandDuringPushAndWaitAsync_ReportsMatcherFailureWithoutStoppingPush()
    {
        var stream = new ScriptedDuplexStream();
        var logger = new InMemoryLogger();
        var entries = new List<LogEntry>();
        logger.EntryWritten += entries.Add;
        await using var transport = new SerialPortTransport(logger, stream);
        await transport.StartPushAsync("PSI,1000", _ => { });
        var commandTask = transport.SendCommandDuringPushAndWaitAsync(
            "HLD,CHANNEL,1,2",
            _ => throw new InvalidOperationException("matcher failed"),
            1000);
        stream.EnqueueResponse("HLD,OK\r"u8.ToArray());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => commandTask);

        Assert.Equal("matcher failed", exception.Message);
        Assert.True(transport.IsPushActive);
        Assert.Contains(entries, entry => entry.Direction == "ERR" && entry.Command == "HLD");
        await transport.StopPushAsync();
    }

    [Fact]
    public async Task SendCommandDuringPushAndWaitAsync_TimesOutWithoutMatchingAcknowledgement()
    {
        var stream = new ScriptedDuplexStream();
        var logger = new InMemoryLogger();
        var entries = new List<LogEntry>();
        logger.EntryWritten += entries.Add;
        await using var transport = new SerialPortTransport(logger, stream);
        await transport.StartPushAsync("PSI,1000", _ => { });

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => transport.SendCommandDuringPushAndWaitAsync(
                "POF",
                response => response.TrimEnd('\r', '\n') == "POF,OK",
                30));
            var timeoutEntry = Assert.Single(entries, entry => entry.Direction == "ERR");
            Assert.Equal("POF", timeoutEntry.Command);
            Assert.NotNull(timeoutEntry.Elapsed);
        }
        finally
        {
            await transport.StopPushAsync();
        }
    }

    [Fact]
    public async Task StopPushAsync_CancelsPendingAcknowledgementWaits()
    {
        var stream = new ScriptedDuplexStream();
        await using var transport = new SerialPortTransport(new InMemoryLogger(), stream);
        await transport.StartPushAsync("PSI,1000", _ => { });
        var commandTask = transport.SendCommandDuringPushAndWaitAsync(
            "HLD,CHANNEL,1,2",
            response => response.TrimEnd('\r', '\n') == "HLD,OK",
            5000);

        await transport.StopPushAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commandTask);
    }

    private sealed class ScriptedDuplexStream : Stream
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        private readonly MemoryStream _written = new();
        private bool _disposed;

        public ScriptedDuplexStream(params byte[][] initialResponses)
        {
            foreach (var response in initialResponses)
            {
                _responses.Writer.TryWrite(response);
            }
        }

        public string WrittenText => Encoding.ASCII.GetString(_written.ToArray());
        public Action<string>? OnCommandWritten { get; set; }
        public bool BlockReads { get; set; }
        public bool BlockWrites { get; set; }
        public bool IsDisposed => _disposed;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void EnqueueResponse(byte[] response)
        {
            _responses.Writer.TryWrite(response);
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (BlockReads)
            {
                ReadStarted.TrySetResult();
                await ContinueRead.Task.WaitAsync(cancellationToken);
            }

            var response = await _responses.Reader.ReadAsync(cancellationToken);

            var bytesToCopy = Math.Min(buffer.Length, response.Length);
            response.AsMemory(0, bytesToCopy).CopyTo(buffer);
            if (bytesToCopy < response.Length)
            {
                _responses.Writer.TryWrite(response.AsSpan(bytesToCopy).ToArray());
            }

            return bytesToCopy;
        }

        public override void Write(byte[] buffer, int offset, int count) => _written.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (BlockWrites)
            {
                WriteStarted.TrySetResult();
                await ContinueWrite.Task.WaitAsync(cancellationToken);
            }

            _written.Write(buffer.Span);
            OnCommandWritten?.Invoke(Encoding.ASCII.GetString(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            if (disposing)
            {
                _written.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
