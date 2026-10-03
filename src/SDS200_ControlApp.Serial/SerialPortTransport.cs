using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using SDS200_ControlApp.Domain;
using SDS200_ControlApp.Protocol;

namespace SDS200_ControlApp.Serial;

public sealed record ScannerConnectionSettings(
    string PortName,
    int BaudRate,
    Parity Parity,
    int DataBits,
    StopBits StopBits,
    Handshake Handshake,
    int ReadTimeoutMilliseconds = 3000);

public sealed class SerialPortTransport : IAsyncDisposable
{
    private readonly IScannerLogger _logger;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly object _pendingResponsesLock = new();
    private readonly List<PendingResponse> _pendingResponses = [];
    private SerialPort? _serialPort;
    private Stream? _stream;
    private CarriageReturnFrameReader? _frameReader;
    private int _readTimeoutMilliseconds = 3000;
    private CancellationTokenSource? _pushCancellation;
    private Task? _pushTask;

    public SerialPortTransport(IScannerLogger logger)
    {
        _logger = logger;
    }

    public SerialPortTransport(IScannerLogger logger, Stream stream, int readTimeoutMilliseconds = 3000)
        : this(logger)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readTimeoutMilliseconds);
        if (!stream.CanRead || !stream.CanWrite)
        {
            throw new ArgumentException("The scanner stream must support reading and writing.", nameof(stream));
        }

        _stream = stream;
        _frameReader = new CarriageReturnFrameReader();
        _readTimeoutMilliseconds = readTimeoutMilliseconds;
    }

    public bool IsConnected => _stream?.CanRead == true && _stream.CanWrite;

    public bool IsPushActive => _pushTask is not null && !_pushTask.IsCompleted;

    public event Action<Exception>? PushStreamFailed;

    public static string[] GetPortNames() => SerialPort.GetPortNames().OrderBy(name => name).ToArray();

    public async Task ConnectAsync(ScannerConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateConnectionSettings(settings);
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
            {
                throw new InvalidOperationException("The scanner is already connected.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var port = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits)
            {
                Handshake = settings.Handshake,
                ReadTimeout = settings.ReadTimeoutMilliseconds,
                WriteTimeout = settings.ReadTimeoutMilliseconds,
                NewLine = "\r"
            };

            port.Open();
            _serialPort = port;
            _stream = port.BaseStream;
            _frameReader = new CarriageReturnFrameReader();
            _readTimeoutMilliseconds = settings.ReadTimeoutMilliseconds;
            _logger.Log(
                LogLevel.Info,
                $"Opened {settings.PortName}; baud={settings.BaudRate}, parity={settings.Parity}, dataBits={settings.DataBits}, stopBits={settings.StopBits}, handshake={settings.Handshake}.");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _commandLock.WaitAsync();
        try
        {
            await StopPushAsync();
            var port = Interlocked.Exchange(ref _serialPort, null);
            var stream = Interlocked.Exchange(ref _stream, null);
            _frameReader = null;
            if (port is null && stream is null)
            {
                return;
            }

            if (port is not null)
            {
                await Task.Run(port.Dispose);
            }
            else if (stream is not null)
            {
                await stream.DisposeAsync();
            }

            _logger.Log(LogLevel.Info, "Closed scanner connection.");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<string> SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        var stream = _stream;
        if (stream?.CanWrite != true)
        {
            throw new InvalidOperationException("The scanner is not connected.");
        }

        if (IsPushActive)
        {
            throw new InvalidOperationException("A push stream is active; stop it before sending a request/response command.");
        }

        var framedCommand = ScannerCommands.Frame(command);
        var commandName = framedCommand.Split(',', 2)[0].TrimEnd('\r');
        var stopwatch = Stopwatch.StartNew();

        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            if (!ReferenceEquals(_stream, stream) || stream.CanWrite != true)
            {
                throw new InvalidOperationException("The scanner connection is no longer active.");
            }

            _logger.Log(LogLevel.Trace, framedCommand.TrimEnd('\r'), "TX", commandName);
            await WriteCommandAsync(stream, framedCommand, cancellationToken);
            var response = await ReadResponseAsync(stream, cancellationToken);
            stopwatch.Stop();
            _logger.Log(LogLevel.Trace, response, "RX", commandName, stopwatch.Elapsed);
            return response;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
        {
            stopwatch.Stop();
            _logger.Log(LogLevel.Error, exception.Message, "ERR", commandName, stopwatch.Elapsed);
            throw;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task SendCommandDuringPushAsync(string command, CancellationToken cancellationToken = default)
    {
        var stream = _stream;
        if (stream?.CanWrite != true || !IsPushActive)
        {
            throw new InvalidOperationException("A connected scanner status push must be active to send a command this way.");
        }

        var framedCommand = ScannerCommands.Frame(command);
        var commandName = framedCommand.Split(',', 2)[0].TrimEnd('\r');
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            if (!ReferenceEquals(_stream, stream) || stream.CanWrite != true || !IsPushActive)
            {
                throw new InvalidOperationException("The scanner status push is no longer active.");
            }

            _logger.Log(LogLevel.Trace, framedCommand.TrimEnd('\r'), "TX", commandName);
            await WriteCommandAsync(stream, framedCommand, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
        {
            _logger.Log(LogLevel.Error, exception.Message, "ERR", commandName);
            throw;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task<string> SendCommandDuringPushAndWaitAsync(
        string command,
        Func<string, bool> responseMatcher,
        int timeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(responseMatcher);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMilliseconds);

        var pendingCommandName = command.TrimEnd('\r').Split(',', 2)[0];
        var pendingResponse = new PendingResponse(pendingCommandName, responseMatcher);
        lock (_pendingResponsesLock)
        {
            _pendingResponses.Add(pendingResponse);
        }

        try
        {
            await SendCommandDuringPushAsync(command, cancellationToken);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await pendingResponse.Completion.Task.WaitAsync(
                    TimeSpan.FromMilliseconds(timeoutMilliseconds),
                    cancellationToken);
                stopwatch.Stop();
                return response;
            }
            catch (TimeoutException exception)
            {
                stopwatch.Stop();
                var commandName = command.TrimEnd('\r').Split(',', 2)[0];
                _logger.Log(LogLevel.Error, exception.Message, "ERR", commandName, stopwatch.Elapsed);
                throw new TimeoutException($"Timed out waiting for a response to {command.TrimEnd('\r')}.", exception);
            }
        }
        finally
        {
            lock (_pendingResponsesLock)
            {
                _pendingResponses.Remove(pendingResponse);
            }
        }
    }

    public async Task StartPushAsync(
        string command,
        Action<string> responseHandler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(responseHandler);

        var stream = _stream;
        if (stream?.CanWrite != true)
        {
            throw new InvalidOperationException("The scanner is not connected.");
        }

        if (IsPushActive)
        {
            throw new InvalidOperationException("A push stream is already active.");
        }

        var framedCommand = ScannerCommands.Frame(command);
        var commandName = framedCommand.Split(',', 2)[0].TrimEnd('\r');
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            if (!ReferenceEquals(_stream, stream) || stream.CanWrite != true || IsPushActive)
            {
                throw new InvalidOperationException("The scanner connection is no longer available for a status push.");
            }

            _logger.Log(LogLevel.Trace, framedCommand.TrimEnd('\r'), "TX", commandName);
            await WriteCommandAsync(stream, framedCommand, cancellationToken);

            var pushCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pushCancellation = pushCancellation;
            _pushTask = ReadPushResponsesAsync(stream, commandName, responseHandler, pushCancellation.Token);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task StopPushAsync()
    {
        var cancellation = Interlocked.Exchange(ref _pushCancellation, null);
        var task = Interlocked.Exchange(ref _pushTask, null);
        if (cancellation is null && task is null)
        {
            return;
        }

        cancellation?.Cancel();
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        CancelPendingResponses();
        cancellation?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _commandLock.Dispose();
    }

    private async Task ReadPushResponsesAsync(
        Stream stream,
        string commandName,
        Action<string> responseHandler,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var stopwatch = Stopwatch.StartNew();
                var response = await ReadResponseAsync(stream, cancellationToken);
                stopwatch.Stop();
                _logger.Log(LogLevel.Trace, response, "RX", GetResponseCommandName(response, commandName), stopwatch.Elapsed);
                CompletePendingResponse(response);
                try
                {
                    responseHandler(response);
                }
                catch (Exception exception)
                {
                    _logger.Log(LogLevel.Error, $"Response handler failed: {exception.Message}", "ERR", commandName);
                    PushStreamFailed?.Invoke(exception);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
        {
            _logger.Log(LogLevel.Error, exception.Message, "ERR", commandName);
            CancelPendingResponses();
            PushStreamFailed?.Invoke(exception);
        }
    }

    private void CancelPendingResponses()
    {
        lock (_pendingResponsesLock)
        {
            foreach (var pendingResponse in _pendingResponses)
            {
                pendingResponse.Completion.TrySetCanceled();
            }
        }
    }

    private static string GetResponseCommandName(string response, string fallbackCommand)
    {
        var trimmed = response.TrimStart();
        if (!trimmed.StartsWith('<'))
        {
            return trimmed.Split(',', 2)[0].TrimEnd('\r', '\n');
        }

        try
        {
            var rootName = System.Xml.Linq.XDocument.Parse(trimmed).Root?.Name.LocalName;
            return string.Equals(rootName, "ScannerInfo", StringComparison.OrdinalIgnoreCase)
                ? fallbackCommand
                : "GLT";
        }
        catch (System.Xml.XmlException)
        {
            return "XML";
        }
    }

    private void CompletePendingResponse(string response)
    {
        lock (_pendingResponsesLock)
        {
            for (var index = 0; index < _pendingResponses.Count; index++)
            {
                var pendingResponse = _pendingResponses[index];
                bool matches;
                try
                {
                    matches = pendingResponse.Matcher(response);
                }
                catch (Exception exception)
                {
                    _pendingResponses.RemoveAt(index);
                    _logger.Log(LogLevel.Error, $"Response matcher failed: {exception.Message}", "ERR", pendingResponse.CommandName);
                    pendingResponse.Completion.TrySetException(exception);
                    index--;
                    continue;
                }

                if (matches)
                {
                    _pendingResponses.RemoveAt(index);
                    pendingResponse.Completion.TrySetResult(response);
                    return;
                }
            }
        }
    }

    private sealed class PendingResponse(string commandName, Func<string, bool> matcher)
    {
        public string CommandName { get; } = commandName;
        public Func<string, bool> Matcher { get; } = matcher;
        public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task WriteCommandAsync(Stream stream, string command, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(command);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static void ValidateConnectionSettings(ScannerConnectionSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.PortName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.BaudRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.ReadTimeoutMilliseconds);
        if (settings.DataBits is < 5 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Serial data bits must be between 5 and 8.");
        }

        if (!Enum.IsDefined(settings.Parity) || !Enum.IsDefined(settings.StopBits) ||
            settings.StopBits == StopBits.None || !Enum.IsDefined(settings.Handshake))
        {
            throw new ArgumentException("The serial parity, stop bits, or handshake setting is invalid.", nameof(settings));
        }
    }

    private async Task<string> ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_readTimeoutMilliseconds);
        try
        {
            return await _frameReader!.ReadFrameAsync(stream, timeoutCancellation.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for a scanner response after {_readTimeoutMilliseconds} ms.", exception);
        }
    }

    private sealed class CarriageReturnFrameReader
    {
        private const int MaximumFrameLength = 64 * 1024;
        private readonly StringBuilder _pending = new();
        private readonly byte[] _readBuffer = new byte[256];

        public async Task<string> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            while (true)
            {
                var terminatorIndex = _pending.ToString().IndexOf('\r');
                if (terminatorIndex >= 0)
                {
                    if (terminatorIndex + 1 > MaximumFrameLength)
                    {
                        throw new InvalidDataException($"Scanner response exceeded the maximum frame length of {MaximumFrameLength} characters.");
                    }

                    var frame = _pending.ToString(0, terminatorIndex + 1);
                    _pending.Remove(0, terminatorIndex + 1);
                    return frame;
                }

                var bytesRead = await stream.ReadAsync(_readBuffer.AsMemory(), cancellationToken);
                if (bytesRead == 0)
                {
                    throw new IOException("The scanner closed the serial stream.");
                }

                _pending.Append(Encoding.ASCII.GetString(_readBuffer, 0, bytesRead));
                if (_pending.Length > MaximumFrameLength)
                {
                    throw new InvalidDataException($"Scanner response exceeded the maximum frame length of {MaximumFrameLength} characters.");
                }
            }
        }
    }
}
