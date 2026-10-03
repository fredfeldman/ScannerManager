using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SDS200_ControlApp.Domain;
using SDS200_ControlApp.Protocol;
using SDS200_ControlApp.Serial;

namespace SDS200_ControlApp;

public partial class MainWindow : Window
{
    private const int MaxLogViewEntries = 2000;
    private readonly RollingFileLogger _logger = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SDS200_ControlApp",
        "Logs",
        "scanner.jsonl"));
    private readonly List<LogEntry> _logEntries = [];
    private SerialPortTransport? _transport;
    private bool _updatingLevelsFromScanner;
    private readonly int[] _favoritesListQuickKeyStatuses = new int[ScannerCommands.FavoritesListQuickKeyCount];
    private bool _hasFavoritesListQuickKeyStatuses;
    private readonly int[] _hierarchicalQuickKeyStatuses = new int[ScannerCommands.QuickKeyStatusCount];
    private bool _hasHierarchicalQuickKeyStatuses;
    private int? _loadedHierarchicalFavoritesListIndex;
    private int? _loadedHierarchicalSystemIndex;
    private string? _loadedHierarchicalQuickKeyType;
    private int? _lastDatabaseCounter;
    private int? _recordingStatus;
    private bool _isClosing;
    private bool _isConnecting;
    private CancellationTokenSource? _connectCancellation;
    private TaskCompletionSource? _connectionAttemptCompletion;

    public MainWindow()
    {
        InitializeComponent();
        _logger.EntryWritten += Logger_EntryWritten;
        HierarchicalQuickKeySlotComboBox.ItemsSource = Enumerable.Range(0, ScannerCommands.QuickKeyStatusCount);
        HierarchicalQuickKeySlotComboBox.SelectedIndex = 0;
        RefreshPorts();
    }

    private void RefreshPortsButton_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnecting || _isClosing)
        {
            return;
        }

        if (_transport?.IsConnected == true)
        {
            await DisconnectAsync();
            return;
        }

        if (PortComboBox.SelectedItem is not string portName)
        {
            MessageBox.Show(this, "Select a COM port first.", "SDS200 Connection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!int.TryParse(BaudTextBox.Text, out var baudRate) || baudRate <= 0)
        {
            MessageBox.Show(this, "Enter a valid baud rate.", "SDS200 Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(ReadTimeoutTextBox.Text, out var readTimeoutMilliseconds) || readTimeoutMilliseconds <= 0)
        {
            MessageBox.Show(this, "Enter a positive read timeout in milliseconds.", "SDS200 Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryGetSelectedEnum(ParityComboBox, out Parity parity) ||
            !TryGetSelectedInt(DataBitsComboBox, out var dataBits) ||
            !TryGetSelectedEnum(StopBitsComboBox, out StopBits stopBits) ||
            !TryGetSelectedEnum(HandshakeComboBox, out Handshake handshake))
        {
            MessageBox.Show(this, "Select valid serial settings.", "SDS200 Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isConnecting = true;
        ConnectButton.IsEnabled = false;
        _connectCancellation = new CancellationTokenSource();
        var cancellationToken = _connectCancellation.Token;
        var attemptCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectionAttemptCompletion = attemptCompletion;
        try
        {
            SetConnectionUi(true, "Connecting...");
            _transport = new SerialPortTransport(_logger);
            var transport = _transport;
            transport.PushStreamFailed += exception => HandlePushStreamFailure(transport, exception);
            var settings = new ScannerConnectionSettings(portName, baudRate, parity, dataBits, stopBits, handshake, readTimeoutMilliseconds);
            await _transport.ConnectAsync(settings, cancellationToken);

            var modelResponse = await _transport.SendCommandAsync("MDL", cancellationToken);
            if (!ScannerCommands.TryParseModel(modelResponse, out var model) || model != "SDS200")
            {
                _logger.Log(LogLevel.Error, $"Rejected scanner model response: {modelResponse.Trim()}", direction: "RX", command: "MDL");
                throw new InvalidOperationException($"Connected device is not an SDS200. Response: {modelResponse.Trim()}");
            }

            ModelTextBlock.Text = model;
            _logger.Log(LogLevel.Info, $"Scanner model confirmed: {model}.", command: "MDL");
            var firmwareResponse = await _transport.SendCommandAsync("VER", cancellationToken);
            FirmwareTextBlock.Text = ScannerCommands.TryParseFirmware(firmwareResponse, out var firmware)
                ? firmware
                : "Unknown";
            _logger.Log(LogLevel.Info, $"Scanner firmware: {FirmwareTextBlock.Text}.", command: "VER");
            var volumeResponse = await _transport.SendCommandAsync("VOL", cancellationToken);
            if (ScannerCommands.TryParseVolumeLevel(volumeResponse, out var volume))
            {
                VolumeSlider.Value = volume;
                VolumeValueTextBlock.Text = volume.ToString();
            }

            var squelchResponse = await _transport.SendCommandAsync("SQL", cancellationToken);
            if (ScannerCommands.TryParseSquelchLevel(squelchResponse, out var squelch))
            {
                SquelchSlider.Value = squelch;
                SquelchValueTextBlock.Text = squelch.ToString();
            }

            await UpdateStatusAsync(cancellationToken);
            await _transport.StartPushAsync(ScannerCommands.BuildStatusPush(1000), HandleStatusResponse, cancellationToken);
            SetConnectionUi(true, "Connected");
            await RefreshFavoritesListQuickKeysAsync();
            await RefreshRecordingStatusAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisconnectAsync();
        }
        catch (Exception exception)
        {
            _logger.Log(LogLevel.Error, exception.Message);
            await DisconnectAsync();
            MessageBox.Show(this, exception.Message, "SDS200 Connection Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isConnecting = false;
            _connectCancellation?.Dispose();
            _connectCancellation = null;
            _connectionAttemptCompletion = null;
            attemptCompletion.TrySetResult();
            if (!_isClosing)
            {
                ConnectButton.IsEnabled = true;
            }
        }
    }

    private void RefreshPorts()
    {
        var selectedPort = PortComboBox.SelectedItem as string;
        PortComboBox.ItemsSource = SerialPortTransport.GetPortNames();
        if (selectedPort is not null && PortComboBox.Items.Contains(selectedPort))
        {
            PortComboBox.SelectedItem = selectedPort;
        }
        else if (PortComboBox.Items.Count > 0)
        {
            PortComboBox.SelectedIndex = 0;
        }
    }

    private static bool TryGetSelectedEnum<TEnum>(ComboBox comboBox, out TEnum value)
        where TEnum : struct, Enum
    {
        var selectedTag = (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse(selectedTag, out value);
    }

    private static bool TryGetSelectedInt(ComboBox comboBox, out int value)
    {
        var selectedTag = (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return int.TryParse(selectedTag, out value);
    }

    private async Task DisconnectAsync()
    {
        if (_transport is not null)
        {
            await _transport.DisposeAsync();
            _transport = null;
        }

        ModelTextBlock.Text = "Unknown";
        FirmwareTextBlock.Text = "Unknown";
        ModeTextBlock.Text = "Unknown";
        FrequencyTextBlock.Text = "Unknown";
        SystemTextBlock.Text = "Unknown";
        DepartmentTextBlock.Text = "Unknown";
        SiteTextBlock.Text = "Unknown";
        ChannelTextBlock.Text = "Unknown";
        DatabaseCounterTextBlock.Text = "Unknown";
        VolumeValueTextBlock.Text = "Unknown";
        SquelchValueTextBlock.Text = "Unknown";
        FavoritesListsTreeView.Items.Clear();
        FavoritesListsStatusTextBlock.Text = "Not loaded";
        _hasFavoritesListQuickKeyStatuses = false;
        _hasHierarchicalQuickKeyStatuses = false;
        _loadedHierarchicalFavoritesListIndex = null;
        _loadedHierarchicalSystemIndex = null;
        _loadedHierarchicalQuickKeyType = null;
        HierarchicalQuickKeyStatusTextBlock.Text = "Not loaded";
        _lastDatabaseCounter = null;
        _recordingStatus = null;
        RecordingStatusTextBlock.Text = "Unknown";
        FavoritesListQuickKeyStatusComboBox.SelectedIndex = 0;
        SetConnectionUi(false, "Disconnected");
    }

    private async Task UpdateStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_transport?.IsConnected != true)
        {
            return;
        }

        try
        {
            var response = await _transport.SendCommandAsync("GSI", cancellationToken);
            var status = ScannerInfoParser.Parse(response);
            DisplayStatus(status);
            LogParsedStatus(status, "GSI");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or System.Xml.XmlException)
        {
            _logger.Log(LogLevel.Warning, $"Status update failed: {exception.Message}", direction: "ERR", command: "GSI");
        }
    }

    private void HandleStatusResponse(string response)
    {
        if (!response.TrimStart().StartsWith('<'))
        {
            if (ScannerCommands.TryParseRecordingResponse(response, out var recordingResponse))
            {
                _logger.Log(LogLevel.Debug, $"Parsed URC response: status={recordingResponse!.Status?.ToString() ?? "n/a"}, error={recordingResponse.ErrorCode ?? "none"}, acknowledged={recordingResponse.IsAcknowledgement}.", command: "URC");
                Dispatcher.Invoke(() => ApplyRecordingResponse(recordingResponse!));
                return;
            }

            if (ScannerCommands.TryParseFavoritesListQuickKeys(response, out var quickKeyStatuses))
            {
                _logger.Log(LogLevel.Debug, $"Parsed {quickKeyStatuses.Length} Favorites List Quick Key statuses.", command: "FQK");
                Dispatcher.Invoke(() => UpdateFavoritesListQuickKeys(quickKeyStatuses));
                return;
            }

            if (response.StartsWith("FQK,OK", StringComparison.Ordinal))
            {
                _logger.Log(LogLevel.Info, "Favorites List Quick Keys updated.", command: "FQK");
                return;
            }

            if (response.StartsWith("SQK,", StringComparison.Ordinal) || response.StartsWith("DQK,", StringComparison.Ordinal))
            {
                _logger.Log(LogLevel.Debug, $"Received {response.Trim()}.", command: response.StartsWith("SQK,", StringComparison.Ordinal) ? "SQK" : "DQK");
                return;
            }

            if (response.TrimEnd('\r', '\n') == "AVD,OK")
            {
                _logger.Log(LogLevel.Info, "Avoid setting accepted.", command: "AVD");
                return;
            }

            if (response.StartsWith("GLT,OK", StringComparison.Ordinal))
            {
                _logger.Log(LogLevel.Info, "Favorites Lists request accepted.", command: "GLT");
                return;
            }

            if (ScannerCommands.TryParseVolumeLevel(response, out var volume))
            {
                _logger.Log(LogLevel.Debug, $"Parsed volume level {volume}.", command: "VOL");
                Dispatcher.Invoke(() => SetVolumeFromScanner(volume));
                return;
            }

            if (ScannerCommands.TryParseSquelchLevel(response, out var squelch))
            {
                _logger.Log(LogLevel.Debug, $"Parsed squelch level {squelch}.", command: "SQL");
                Dispatcher.Invoke(() => SetSquelchFromScanner(squelch));
                return;
            }

            _logger.Log(LogLevel.Debug, response.Trim(), command: "PSI");
            return;
        }

        try
        {
            var document = System.Xml.Linq.XDocument.Parse(response.TrimEnd('\r', '\n'));
            if (string.Equals(document.Root?.Name.LocalName, "ScannerInfo", StringComparison.OrdinalIgnoreCase))
            {
                var status = ScannerInfoParser.Parse(response);
                Dispatcher.Invoke(() =>
                {
                    DisplayStatus(status);
                    LogParsedStatus(status, "PSI");
                });
                return;
            }

            var tree = ScannerXmlTreeParser.Parse(response);
            Dispatcher.Invoke(() =>
            {
                DisplayFavoritesListTree(tree);
                _logger.Log(LogLevel.Info, $"Parsed GLT XML root {tree.Label}.", command: "GLT");
            });
        }
        catch (System.Xml.XmlException exception)
        {
            _logger.Log(LogLevel.Warning, $"XML response parse failed: {exception.Message}", direction: "RX", command: "XML");
        }
    }

    private void DisplayFavoritesListTree(ScannerXmlNode root)
    {
        FavoritesListsTreeView.Items.Clear();
        FavoritesListsTreeView.Items.Add(CreateTreeViewItem(root));
        FavoritesListsStatusTextBlock.Text = "Loaded";
    }

    private static TreeViewItem CreateTreeViewItem(ScannerXmlNode node)
    {
        var item = new TreeViewItem { Header = node.Label };
        foreach (var child in node.Children)
        {
            item.Items.Add(CreateTreeViewItem(child));
        }

        item.IsExpanded = item.Items.Count > 0;
        return item;
    }

    private async void RefreshFavoritesListsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transport?.IsPushActive != true)
        {
            return;
        }

        RefreshFavoritesListsButton.IsEnabled = false;
        FavoritesListsStatusTextBlock.Text = "Requesting lists...";
        try
        {
            var command = BuildSelectedGltCommand();
            await _transport.SendCommandDuringPushAsync(command);
            _logger.Log(LogLevel.Info, $"Requested GLT {((ComboBoxItem)GltTypeComboBox.SelectedItem).Tag}.", command: "GLT");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or TimeoutException)
        {
            FavoritesListsStatusTextBlock.Text = "Request failed";
            _logger.Log(LogLevel.Warning, $"Could not retrieve Favorites Lists: {exception.Message}", command: "GLT");
        }
        finally
        {
            RefreshFavoritesListsButton.IsEnabled = _transport?.IsConnected == true;
        }
    }

    private void GltTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GltParentIndexTextBox is not null)
        {
            GltParentIndexTextBox.IsEnabled = GltTypeComboBox.SelectedIndex > 0;
        }
    }

    private string BuildSelectedGltCommand()
    {
        var queryType = (GltTypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (queryType == "FL")
        {
            return ScannerCommands.BuildGetFavoritesListsCommand();
        }

        if (!int.TryParse(GltParentIndexTextBox.Text, out var parentIndex) || parentIndex < 0)
        {
            throw new ArgumentException("Enter a nonnegative runtime index for the selected GLT query.");
        }

        return queryType switch
        {
            "SYS" => ScannerCommands.BuildGetSystemsCommand(parentIndex),
            "DEPT" => ScannerCommands.BuildGetDepartmentsCommand(parentIndex),
            "SITE" => ScannerCommands.BuildGetSitesCommand(parentIndex),
            "CFREQ" => ScannerCommands.BuildGetConventionalFrequenciesCommand(parentIndex),
            "TGID" => ScannerCommands.BuildGetTalkgroupsCommand(parentIndex),
            "SFREQ" => ScannerCommands.BuildGetSiteFrequenciesCommand(parentIndex),
            _ => throw new InvalidOperationException("Select a supported GLT query type.")
        };
    }

    private void DisplayStatus(ScannerInfo status)
    {
        if (status.DatabaseCounter is int databaseCounter)
        {
            if (_lastDatabaseCounter is int previousCounter && previousCounter != databaseCounter)
            {
                FavoritesListsTreeView.Items.Clear();
                FavoritesListsStatusTextBlock.Text = "Database changed; refresh lists";
                Array.Clear(_favoritesListQuickKeyStatuses);
                _hasFavoritesListQuickKeyStatuses = false;
                FavoritesListQuickKeyStatusComboBox.IsEnabled = false;
                ApplyQuickKeyButton.IsEnabled = false;
                Array.Clear(_hierarchicalQuickKeyStatuses);
                _hasHierarchicalQuickKeyStatuses = false;
                _loadedHierarchicalFavoritesListIndex = null;
                _loadedHierarchicalSystemIndex = null;
                _loadedHierarchicalQuickKeyType = null;
                HierarchicalQuickKeySlotComboBox.IsEnabled = false;
                HierarchicalQuickKeyStatusComboBox.IsEnabled = false;
                ApplyHierarchicalQuickKeyButton.IsEnabled = false;
                HierarchicalQuickKeyStatusTextBlock.Text = "Database changed; refresh Quick Keys";
                _logger.Log(LogLevel.Info, "Database counter changed; cleared GLT data because runtime indexes are no longer valid.", command: "DB_Counter");
            }

            _lastDatabaseCounter = databaseCounter;
        }

        ModeTextBlock.Text = status.Mode ?? "Unknown";
        FrequencyTextBlock.Text = status.Frequency ?? "Unknown";
        SystemTextBlock.Text = status.System ?? "Unknown";
        DepartmentTextBlock.Text = status.Department ?? "Unknown";
        SiteTextBlock.Text = status.Site ?? "Unknown";
        ChannelTextBlock.Text = status.Channel ?? "Unknown";
        DatabaseCounterTextBlock.Text = status.DatabaseCounter?.ToString() ?? "Unknown";
    }

    private void LogParsedStatus(ScannerInfo status, string command)
    {
        _logger.Log(
            LogLevel.Debug,
            $"Parsed status: mode={status.Mode ?? "unknown"}, frequency={status.Frequency ?? "unknown"}, system={status.System ?? "unknown"}, department={status.Department ?? "unknown"}, site={status.Site ?? "unknown"}, channel={status.Channel ?? "unknown"}, databaseCounter={status.DatabaseCounter?.ToString() ?? "unknown"}.",
            command: command);
    }

    private void SetConnectionUi(bool connected, string status)
    {
        ConnectionStatusTextBlock.Text = status;
        ConnectButton.Content = connected ? "Disconnect" : "Connect";
        PortComboBox.IsEnabled = !connected;
        BaudTextBox.IsEnabled = !connected;
        ReadTimeoutTextBox.IsEnabled = !connected;
        ParityComboBox.IsEnabled = !connected;
        DataBitsComboBox.IsEnabled = !connected;
        StopBitsComboBox.IsEnabled = !connected;
        HandshakeComboBox.IsEnabled = !connected;
        StartScanButton.IsEnabled = connected;
        HoldNavigationButton.IsEnabled = connected;
        NextNavigationButton.IsEnabled = connected;
        PreviousNavigationButton.IsEnabled = connected;
        JumpNavigationButton.IsEnabled = connected;
        PowerOffButton.IsEnabled = connected;
        VolumeSlider.IsEnabled = connected;
        SquelchSlider.IsEnabled = connected;
        FavoritesListQuickKeyComboBox.IsEnabled = connected;
        FavoritesListQuickKeyStatusComboBox.IsEnabled = connected && _hasFavoritesListQuickKeyStatuses;
        RefreshQuickKeysButton.IsEnabled = connected;
        ApplyQuickKeyButton.IsEnabled = connected && _hasFavoritesListQuickKeyStatuses;
        HierarchicalQuickKeyTypeComboBox.IsEnabled = connected;
        HierarchicalFavoritesListIndexTextBox.IsEnabled = connected;
        HierarchicalSystemIndexTextBox.IsEnabled = connected && HierarchicalQuickKeyTypeComboBox.SelectedIndex == 1;
        HierarchicalQuickKeySlotComboBox.IsEnabled = connected && _hasHierarchicalQuickKeyStatuses;
        HierarchicalQuickKeyStatusComboBox.IsEnabled = connected && _hasHierarchicalQuickKeyStatuses;
        RefreshHierarchicalQuickKeysButton.IsEnabled = connected;
        ApplyHierarchicalQuickKeyButton.IsEnabled = connected && _hasHierarchicalQuickKeyStatuses;
        RefreshFavoritesListsButton.IsEnabled = connected;
        RefreshRecordingButton.IsEnabled = connected;
        StartRecordingButton.IsEnabled = connected;
        StopRecordingButton.IsEnabled = connected;
        ApplyAvoidButton.IsEnabled = connected;
    }

    private async void RefreshRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRecordingStatusAsync();
    }

    private async Task RefreshRecordingStatusAsync()
    {
        await SendRecordingCommandAsync(ScannerCommands.BuildGetRecordingStatusCommand());
    }

    private async void StartRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        await SendRecordingCommandAsync(ScannerCommands.BuildSetRecordingStatusCommand(true));
    }

    private async void StopRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        await SendRecordingCommandAsync(ScannerCommands.BuildSetRecordingStatusCommand(false));
    }

    private async Task SendRecordingCommandAsync(string command)
    {
        if (_transport?.IsPushActive != true)
        {
            return;
        }

        try
        {
            await _transport.SendCommandDuringPushAsync(command);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            RecordingStatusTextBlock.Text = "Command failed";
            _logger.Log(LogLevel.Warning, $"Recording command failed: {exception.Message}", command: "URC");
        }
    }

    private void ApplyRecordingResponse(ScannerRecordingResponse response)
    {
        if (response.Status is int status)
        {
            _recordingStatus = status;
            RecordingStatusTextBlock.Text = status == 1 ? "Recording" : "Stopped";
            return;
        }

        if (response.ErrorCode is string errorCode)
        {
            RecordingStatusTextBlock.Text = errorCode switch
            {
                "0001" => "Error: file access",
                "0002" => "Error: low battery",
                "0003" => "Error: session limit",
                "0004" => "Error: clock lost",
                _ => $"Error: {errorCode}"
            };
            _logger.Log(LogLevel.Warning, $"Recording failed with error {errorCode}.", command: "URC");
            return;
        }

        RecordingStatusTextBlock.Text = _recordingStatus is int currentStatus
            ? $"{(currentStatus == 1 ? "Recording" : "Stopped")} (command accepted)"
            : "Command accepted; refresh status";
    }

    private async void RefreshQuickKeysButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshFavoritesListQuickKeysAsync();
    }

    private async Task RefreshFavoritesListQuickKeysAsync()
    {
        if (_transport?.IsPushActive != true)
        {
            return;
        }

        try
        {
            await _transport.SendCommandDuringPushAsync(ScannerCommands.BuildGetFavoritesListQuickKeysCommand());
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            _logger.Log(LogLevel.Warning, $"Could not retrieve Favorites List Quick Keys: {exception.Message}", command: "FQK");
        }
    }

    private void UpdateFavoritesListQuickKeys(int[] statuses)
    {
        Array.Copy(statuses, _favoritesListQuickKeyStatuses, statuses.Length);
        _hasFavoritesListQuickKeyStatuses = true;
        if (FavoritesListQuickKeyComboBox.Items.Count == 0)
        {
            FavoritesListQuickKeyComboBox.ItemsSource = Enumerable.Range(0, ScannerCommands.FavoritesListQuickKeyCount);
            FavoritesListQuickKeyComboBox.SelectedIndex = 0;
        }

        UpdateSelectedFavoritesListQuickKey();
        FavoritesListQuickKeyStatusComboBox.IsEnabled = _transport?.IsConnected == true;
        ApplyQuickKeyButton.IsEnabled = _transport?.IsConnected == true;
    }

    private void FavoritesListQuickKeyComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateSelectedFavoritesListQuickKey();
    }

    private void UpdateSelectedFavoritesListQuickKey()
    {
        if (_hasFavoritesListQuickKeyStatuses && FavoritesListQuickKeyComboBox.SelectedItem is int index)
        {
            FavoritesListQuickKeyStatusComboBox.SelectedIndex = _favoritesListQuickKeyStatuses[index];
        }
    }

    private void HierarchicalQuickKeyTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HierarchicalSystemIndexTextBox is null)
        {
            return;
        }

        var isDepartmentKey = HierarchicalQuickKeyTypeComboBox.SelectedIndex == 1;
        HierarchicalSystemIndexTextBox.IsEnabled = isDepartmentKey && _transport?.IsConnected == true;
        _hasHierarchicalQuickKeyStatuses = false;
        HierarchicalQuickKeySlotComboBox.IsEnabled = false;
        HierarchicalQuickKeyStatusComboBox.IsEnabled = false;
        ApplyHierarchicalQuickKeyButton.IsEnabled = false;
        HierarchicalQuickKeyStatusTextBlock.Text = "Refresh required";
    }

    private void HierarchicalQuickKeySlotComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_hasHierarchicalQuickKeyStatuses && HierarchicalQuickKeySlotComboBox.SelectedItem is int slot)
        {
            HierarchicalQuickKeyStatusComboBox.SelectedIndex = _hierarchicalQuickKeyStatuses[slot];
        }
    }

    private async void RefreshHierarchicalQuickKeysButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transport?.IsPushActive != true || !TryGetHierarchicalQuickKeyContext(out var type, out var favoritesListIndex, out var systemIndex))
        {
            HierarchicalQuickKeyStatusTextBlock.Text = "Enter valid parent Quick Keys";
            return;
        }

        var command = type == "SQK"
            ? ScannerCommands.BuildGetSystemQuickKeysCommand(favoritesListIndex)
            : ScannerCommands.BuildGetDepartmentQuickKeysCommand(favoritesListIndex, systemIndex!.Value);
        HierarchicalQuickKeyStatusTextBlock.Text = "Requesting statuses...";
        RefreshHierarchicalQuickKeysButton.IsEnabled = false;
        try
        {
            var response = await _transport.SendCommandDuringPushAndWaitAsync(
                command,
                value => type == "SQK"
                    ? ScannerCommands.TryParseSystemQuickKeys(value, favoritesListIndex, out _)
                    : ScannerCommands.TryParseDepartmentQuickKeys(value, favoritesListIndex, systemIndex!.Value, out _),
                2000);
            var parsed = type == "SQK"
                ? ScannerCommands.TryParseSystemQuickKeys(response, favoritesListIndex, out var statuses)
                : ScannerCommands.TryParseDepartmentQuickKeys(response, favoritesListIndex, systemIndex!.Value, out statuses);
            if (!parsed)
            {
                throw new InvalidDataException("The scanner returned an invalid Quick Key status list.");
            }

            Array.Copy(statuses, _hierarchicalQuickKeyStatuses, statuses.Length);
            _loadedHierarchicalQuickKeyType = type;
            _loadedHierarchicalFavoritesListIndex = favoritesListIndex;
            _loadedHierarchicalSystemIndex = systemIndex;
            _hasHierarchicalQuickKeyStatuses = true;
            HierarchicalQuickKeySlotComboBox.SelectedIndex = 0;
            HierarchicalQuickKeySlotComboBox.IsEnabled = true;
            HierarchicalQuickKeyStatusComboBox.IsEnabled = true;
            ApplyHierarchicalQuickKeyButton.IsEnabled = true;
            HierarchicalQuickKeyStatusComboBox.SelectedIndex = _hierarchicalQuickKeyStatuses[0];
            HierarchicalQuickKeyStatusTextBlock.Text = "Loaded";
            _logger.Log(LogLevel.Info, $"Loaded {type} statuses for Favorites List QK {favoritesListIndex}{(systemIndex is null ? string.Empty : $", System QK {systemIndex}")}.", command: type);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or TimeoutException or InvalidDataException)
        {
            HierarchicalQuickKeyStatusTextBlock.Text = "Request failed";
            _logger.Log(LogLevel.Warning, $"Could not retrieve {type} statuses: {exception.Message}", command: type);
        }
        finally
        {
            RefreshHierarchicalQuickKeysButton.IsEnabled = _transport?.IsConnected == true;
        }
    }

    private bool TryGetHierarchicalQuickKeyContext(out string type, out int favoritesListIndex, out int? systemIndex)
    {
        type = (HierarchicalQuickKeyTypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        systemIndex = null;
        if (type is not ("SQK" or "DQK") ||
            !int.TryParse(HierarchicalFavoritesListIndexTextBox.Text, out favoritesListIndex) ||
            favoritesListIndex is < 0 or >= ScannerCommands.QuickKeyStatusCount)
        {
            favoritesListIndex = 0;
            return false;
        }

        if (type == "DQK")
        {
            if (!int.TryParse(HierarchicalSystemIndexTextBox.Text, out var parsedSystemIndex) ||
                parsedSystemIndex is < 0 or >= ScannerCommands.QuickKeyStatusCount)
            {
                return false;
            }

            systemIndex = parsedSystemIndex;
        }

        return true;
    }

    private async void ApplyHierarchicalQuickKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasHierarchicalQuickKeyStatuses ||
            !TryGetHierarchicalQuickKeyContext(out var type, out var favoritesListIndex, out var systemIndex) ||
            _loadedHierarchicalQuickKeyType != type ||
            _loadedHierarchicalFavoritesListIndex != favoritesListIndex ||
            _loadedHierarchicalSystemIndex != systemIndex ||
            HierarchicalQuickKeySlotComboBox.SelectedItem is not int slot ||
            HierarchicalQuickKeyStatusComboBox.SelectedIndex is < 0 or > 2)
        {
            HierarchicalQuickKeyStatusTextBlock.Text = "Refresh for selected parents";
            return;
        }

        var status = HierarchicalQuickKeyStatusComboBox.SelectedIndex;
        var updatedStatuses = (int[])_hierarchicalQuickKeyStatuses.Clone();
        updatedStatuses[slot] = status;
        ApplyHierarchicalQuickKeyButton.IsEnabled = false;
        try
        {
            var command = type == "SQK"
                ? ScannerCommands.BuildSetSystemQuickKeysCommand(favoritesListIndex, updatedStatuses)
                : ScannerCommands.BuildSetDepartmentQuickKeysCommand(favoritesListIndex, systemIndex!.Value, updatedStatuses);
            await _transport!.SendCommandDuringPushAndWaitAsync(
                command,
                response => response.TrimEnd('\r', '\n') == $"{type},OK",
                2000);
            Array.Copy(updatedStatuses, _hierarchicalQuickKeyStatuses, updatedStatuses.Length);
            HierarchicalQuickKeyStatusTextBlock.Text = "Applied";
            _logger.Log(LogLevel.Info, $"Set {type} status slot {slot} to {status}.", command: type);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or TimeoutException)
        {
            HierarchicalQuickKeyStatusTextBlock.Text = "Apply failed";
            _logger.Log(LogLevel.Warning, $"Could not apply {type} statuses: {exception.Message}", command: type);
        }
        finally
        {
            ApplyHierarchicalQuickKeyButton.IsEnabled = _transport?.IsConnected == true && _hasHierarchicalQuickKeyStatuses;
        }
    }

    private async void ApplyAvoidButton_Click(object sender, RoutedEventArgs e)
    {
        if (_transport?.IsPushActive != true || AvoidActionComboBox.SelectedIndex is < 0 or > 2)
        {
            return;
        }

        var status = AvoidActionComboBox.SelectedIndex + 1;
        if (status == 1 && MessageBox.Show(
                this,
                "Apply a permanent avoid to this target?",
                "Confirm Permanent Avoid",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        ApplyAvoidButton.IsEnabled = false;
        try
        {
            var command = ScannerCommands.BuildSetAvoidCommand(
                AvoidTargetTextBox.Text,
                AvoidFirstTagTextBox.Text,
                AvoidSecondTagTextBox.Text,
                status);
            await _transport.SendCommandDuringPushAndWaitAsync(
                command,
                response => response.TrimEnd('\r', '\n') == "AVD,OK",
                2000);
            AvoidStatusTextBlock.Text = status switch
            {
                1 => "Permanent avoid applied",
                2 => "Temporary avoid applied",
                _ => "Avoid removed"
            };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or TimeoutException)
        {
            AvoidStatusTextBlock.Text = "Action failed";
            _logger.Log(LogLevel.Warning, $"Avoid command failed: {exception.Message}", command: "AVD");
        }
        finally
        {
            ApplyAvoidButton.IsEnabled = _transport?.IsConnected == true;
        }
    }

    private async void ApplyQuickKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasFavoritesListQuickKeyStatuses || FavoritesListQuickKeyComboBox.SelectedItem is not int index ||
            FavoritesListQuickKeyStatusComboBox.SelectedIndex is < 0 or > 2)
        {
            return;
        }

        var status = FavoritesListQuickKeyStatusComboBox.SelectedIndex;
        var updatedStatuses = (int[])_favoritesListQuickKeyStatuses.Clone();
        updatedStatuses[index] = status;
        ApplyQuickKeyButton.IsEnabled = false;
        try
        {
            await _transport!.SendCommandDuringPushAndWaitAsync(
                ScannerCommands.BuildSetFavoritesListQuickKeysCommand(updatedStatuses),
                response => response.TrimEnd('\r', '\n') == "FQK,OK",
                2000);
            Array.Copy(updatedStatuses, _favoritesListQuickKeyStatuses, updatedStatuses.Length);
            _logger.Log(LogLevel.Info, $"Favorites List Quick Key {index} set to status {status}.", command: "FQK");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            _logger.Log(LogLevel.Warning, $"Could not update Favorites List Quick Keys: {exception.Message}", command: "FQK");
        }
        finally
        {
            ApplyQuickKeyButton.IsEnabled = _transport?.IsConnected == true && _hasFavoritesListQuickKeyStatuses;
        }
    }

    private async void StartScanButton_Click(object sender, RoutedEventArgs e)
    {
        StartScanButton.IsEnabled = false;
        try
        {
            await _transport!.SendCommandDuringPushAndWaitAsync(
                ScannerCommands.BuildStartScanCommand(),
                response => response.TrimEnd('\r', '\n') == "JPM,OK",
                2000);
            NavigationStatusTextBlock.Text = "Scan started";
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            NavigationStatusTextBlock.Text = "Start scan failed";
            _logger.Log(LogLevel.Warning, $"Could not start scanning: {exception.Message}", command: "JPM");
        }
        finally
        {
            StartScanButton.IsEnabled = _transport?.IsConnected == true;
        }
    }

    private async void HoldNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        await SendNavigationCommandAsync(
            () => ScannerCommands.BuildHoldCommand(
                NavigationTargetTextBox.Text,
                NavigationFirstTagTextBox.Text,
                NavigationSecondTagTextBox.Text),
            "HLD,OK",
            "HLD");
    }

    private async void NextNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        await SendNavigationCommandAsync(
            () => ScannerCommands.BuildNextCommand(
                NavigationTargetTextBox.Text,
                NavigationFirstTagTextBox.Text,
                NavigationSecondTagTextBox.Text,
                ParseNavigationCount()),
            "NXT,OK",
            "NXT");
    }

    private async void PreviousNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        await SendNavigationCommandAsync(
            () => ScannerCommands.BuildPreviousCommand(
                NavigationTargetTextBox.Text,
                NavigationFirstTagTextBox.Text,
                NavigationSecondTagTextBox.Text,
                ParseNavigationCount()),
            "PRV,OK",
            "PRV");
    }

    private async void JumpNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        await SendNavigationCommandAsync(
            () => ScannerCommands.BuildJumpToTagCommand(
                NavigationTargetTextBox.Text,
                NavigationFirstTagTextBox.Text,
                NavigationSecondTagTextBox.Text),
            "JNT,OK",
            "JNT");
    }

    private int ParseNavigationCount()
    {
        if (!int.TryParse(NavigationCountTextBox.Text, out var count) || count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(NavigationCountTextBox), "Count must be a positive integer.");
        }

        return count;
    }

    private async Task SendNavigationCommandAsync(Func<string> commandFactory, string expectedResponse, string commandName)
    {
        if (_transport?.IsPushActive != true)
        {
            NavigationStatusTextBlock.Text = "Not connected";
            return;
        }

        try
        {
            var command = commandFactory();
            NavigationStatusTextBlock.Text = $"Sending {commandName}...";
            await _transport.SendCommandDuringPushAndWaitAsync(
                command,
                response => response.TrimEnd('\r', '\n') == expectedResponse,
                2000);
            NavigationStatusTextBlock.Text = $"{commandName} accepted";
            _logger.Log(LogLevel.Info, $"Navigation command accepted: {commandName}.", command: commandName);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or TimeoutException)
        {
            NavigationStatusTextBlock.Text = $"{commandName} failed";
            _logger.Log(LogLevel.Warning, $"Navigation command failed: {exception.Message}", command: commandName);
        }
    }

    private async void PowerOffButton_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            this,
            "Power off the SDS200 now? This command requires scanner firmware V1.03 (2024-12) or later.",
            "Confirm Scanner Power Off",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes || _transport?.IsPushActive != true)
        {
            return;
        }

        PowerOffButton.IsEnabled = false;
        try
        {
            await _transport.SendCommandDuringPushAndWaitAsync(
                ScannerCommands.BuildPowerOffCommand(),
                response => response.TrimEnd('\r', '\n') == "POF,OK",
                3000);
            await DisconnectAsync();
            SetConnectionUi(false, "Scanner powered off");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            _logger.Log(LogLevel.Error, $"Power-off command failed: {exception.Message}", command: "POF");
            SetConnectionUi(_transport?.IsConnected == true, "Power-off command failed");
        }
    }

    private async void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        VolumeValueTextBlock.Text = ((int)VolumeSlider.Value).ToString();
        if (!_updatingLevelsFromScanner && _transport?.IsPushActive == true)
        {
            await SendLevelCommandAsync(ScannerCommands.BuildVolumeCommand((int)VolumeSlider.Value));
        }
    }

    private async void SquelchSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SquelchValueTextBlock.Text = ((int)SquelchSlider.Value).ToString();
        if (!_updatingLevelsFromScanner && _transport?.IsPushActive == true)
        {
            await SendLevelCommandAsync(ScannerCommands.BuildSquelchCommand((int)SquelchSlider.Value));
        }
    }

    private void SetVolumeFromScanner(int level)
    {
        _updatingLevelsFromScanner = true;
        try
        {
            VolumeSlider.Value = level;
            VolumeValueTextBlock.Text = level.ToString();
        }
        finally
        {
            _updatingLevelsFromScanner = false;
        }
    }

    private void SetSquelchFromScanner(int level)
    {
        _updatingLevelsFromScanner = true;
        try
        {
            SquelchSlider.Value = level;
            SquelchValueTextBlock.Text = level.ToString();
        }
        finally
        {
            _updatingLevelsFromScanner = false;
        }
    }

    private async Task SendLevelCommandAsync(string command)
    {
        try
        {
            await _transport!.SendCommandDuringPushAsync(command);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            _logger.Log(LogLevel.Warning, $"Level change failed: {exception.Message}", command: command.TrimEnd('\r').Split(',')[0]);
        }
    }

    private void HandlePushStreamFailure(SerialPortTransport transport, Exception exception)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (!ReferenceEquals(_transport, transport))
            {
                return;
            }

            await DisconnectAsync();
            SetConnectionUi(false, $"Communication failed: {exception.Message}");
        }));
    }

    private void Logger_EntryWritten(LogEntry entry)
    {
        Dispatcher.Invoke(() =>
        {
            _logEntries.Add(entry);
            if (_logEntries.Count > MaxLogViewEntries)
            {
                var removedEntry = _logEntries[0];
                _logEntries.RemoveAt(0);
                if (MatchesCurrentLogFilter(removedEntry) && LogListBox.Items.Count > 0)
                {
                    LogListBox.Items.RemoveAt(0);
                }
            }

            if (MatchesCurrentLogFilter(entry))
            {
                LogListBox.Items.Add(FormatLogEntry(entry));
                LogListBox.ScrollIntoView(LogListBox.Items[^1]);
            }
        });
    }

    private void LogFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogListBox is not null)
        {
            RefreshLogView();
        }
    }

    private void LogCaptureLevelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedLevel = (LogCaptureLevelComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        _logger.MinimumLevel = Enum.TryParse<LogLevel>(selectedLevel, out var level) ? level : null;
    }

    private void RefreshLogView()
    {
        LogListBox.Items.Clear();
        foreach (var entry in _logEntries.Where(MatchesCurrentLogFilter))
        {
            LogListBox.Items.Add(FormatLogEntry(entry));
        }

        if (LogListBox.Items.Count > 0)
        {
            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
        }
    }

    private bool MatchesCurrentLogFilter(LogEntry entry)
    {
        var selectedFilter = (LogFilterComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        return selectedFilter == "All levels" || entry.Level.ToString() == selectedFilter;
    }

    private static string FormatLogEntry(LogEntry entry)
    {
        var direction = entry.Direction is null ? string.Empty : $" [{entry.Direction}]";
        var command = entry.Command is null ? string.Empty : $" {entry.Command}";
        var elapsed = entry.Elapsed is null ? string.Empty : $" ({entry.Elapsed.Value.TotalMilliseconds:0} ms)";
        return $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} {entry.Level}{direction}{command}{elapsed} {entry.Message}";
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        var text = string.Join(Environment.NewLine, LogListBox.Items.Cast<string>());
        if (text.Length > 0)
        {
            Clipboard.SetText(text);
        }
    }

    private void ExportLogButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".txt",
            FileName = "sds200-log.txt",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllLines(dialog.FileName, LogListBox.Items.Cast<string>());
        }
    }

    private void RawTrafficCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _logger.RawTrafficEnabled = RawTrafficCheckBox.IsChecked == true;
    }

    private void FileLoggingCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _logger.FileLoggingEnabled = FileLoggingCheckBox.IsChecked == true;
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        e.Cancel = true;
        _isClosing = true;
        ConnectButton.IsEnabled = false;
        try
        {
            _connectCancellation?.Cancel();
            if (_connectionAttemptCompletion is not null)
            {
                await _connectionAttemptCompletion.Task;
            }

            await DisconnectAsync();
        }
        finally
        {
            _logger.Dispose();
            Closing -= Window_Closing;
            Close();
        }
    }
}