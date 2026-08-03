using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WsjtxUdpFanout;

internal enum PacketKind
{
    Unknown,
    WsjtxToApps,
    AppToWsjtx,
    Ambiguous
}

internal sealed class RelayTarget(string name, IPEndPoint endpoint)
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; set; } = name;
    public IPEndPoint Endpoint { get; set; } = endpoint;
    public ulong Packets { get; set; }
    public ulong Bytes { get; set; }
    public ulong SendErrors { get; set; }
}

internal sealed record TargetSnapshot(
    Guid Id,
    string Name,
    string Address,
    int Port,
    ulong Packets,
    ulong Bytes,
    ulong SendErrors);

internal sealed record CountersSnapshot(
    ulong WsjtxToAppsPackets,
    ulong WsjtxToAppsBytes,
    ulong AppsToWsjtxPackets,
    ulong AppsToWsjtxBytes,
    ulong DroppedPackets,
    ulong UnknownPackets,
    ulong SendErrors);

internal sealed record RelaySnapshot(
    string ListenAddress,
    int ListenPort,
    bool Bidirectional,
    bool IsRunning,
    string Status,
    string? WsjtxSource,
    string LastPacket,
    string ConfigPath,
    CountersSnapshot Counters,
    IReadOnlyList<TargetSnapshot> Targets,
    IReadOnlyList<string> Events);

internal sealed class RelayService : IDisposable
{
    private const uint WsjtxMagic = 0xADBCCBDA;
    private readonly object _stateLock = new();
    private readonly List<RelayTarget> _targets = [];
    private readonly List<string> _events = [];
    private IPEndPoint _listen = new(IPAddress.Loopback, 2236);
    private bool _bidirectional = true;
    private bool _isRunning;
    private string _status = "Ready";
    private string _lastPacket = "None";
    private IPEndPoint? _learnedWsjtxSource;
    private ulong _wsjtxToAppsPackets;
    private ulong _wsjtxToAppsBytes;
    private ulong _appsToWsjtxPackets;
    private ulong _appsToWsjtxBytes;
    private ulong _droppedPackets;
    private ulong _unknownPackets;
    private ulong _sendErrors;
    private string _configPath;
    private CancellationTokenSource? _runCts;
    private Task? _workerTask;
    private Socket? _socket;
    private bool _disposed;

    public RelayService(string[] args)
    {
        _configPath = DefaultConfigPath();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase))
            {
                if (++i >= args.Length)
                    throw new ArgumentException("--config requires a file path.");
                _configPath = Path.GetFullPath(args[i]);
            }
        }

        if (!LoadConfig())
        {
            AddDefaultTargetsLocked();
            SaveConfig();
        }
        else if (_targets.Count == 0)
        {
            AddDefaultTargetsLocked();
        }

        ApplyCommandLine(args);
    }

    public RelaySnapshot GetSnapshot()
    {
        lock (_stateLock)
        {
            return new RelaySnapshot(
                _listen.Address.ToString(),
                _listen.Port,
                _bidirectional,
                _isRunning,
                _status,
                _learnedWsjtxSource?.ToString(),
                _lastPacket,
                _configPath,
                new CountersSnapshot(
                    _wsjtxToAppsPackets,
                    _wsjtxToAppsBytes,
                    _appsToWsjtxPackets,
                    _appsToWsjtxBytes,
                    _droppedPackets,
                    _unknownPackets,
                    _sendErrors),
                _targets.Select(target => new TargetSnapshot(
                    target.Id,
                    target.Name,
                    target.Endpoint.Address.ToString(),
                    target.Endpoint.Port,
                    target.Packets,
                    target.Bytes,
                    target.SendErrors)).ToList(),
                _events.ToList());
        }
    }

    public Task<bool> StartAsync()
    {
        lock (_stateLock)
        {
            ThrowIfDisposed();
            if (_isRunning)
                return Task.FromResult(true);

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Bind(_listen);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                _status = $"Could not listen on {_listen}: {ex.SocketErrorCode}";
                AddEventLocked(_status);
                return Task.FromResult(false);
            }

            _socket = socket;
            _runCts = new CancellationTokenSource();
            _isRunning = true;
            _status = $"Listening on {_listen}";
            AddEventLocked(_status);
            _workerTask = ReceiveLoopAsync(socket, _runCts.Token);
            return Task.FromResult(true);
        }
    }

    public async Task StopAsync()
    {
        Task? worker;
        lock (_stateLock)
        {
            if (!_isRunning && _workerTask is null)
                return;
            _runCts?.Cancel();
            worker = _workerTask;
        }

        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (_stateLock)
        {
            _socket?.Dispose();
            _socket = null;
            _runCts?.Dispose();
            _runCts = null;
            _workerTask = null;
            _isRunning = false;
            _status = "Stopped";
            AddEventLocked(_status);
        }
    }

    public bool ConfigureListener(string addressText, int port, bool bidirectional, out string error)
    {
        if (!IPAddress.TryParse(addressText.Trim(), out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "Enter a valid IPv4 listen address.";
            return false;
        }

        if (port is < 1 or > 65535)
        {
            error = "The listen port must be between 1 and 65535.";
            return false;
        }

        lock (_stateLock)
        {
            if (_isRunning && (!_listen.Address.Equals(address) || _listen.Port != port))
            {
                error = "Stop the relay before changing its listen address or port.";
                return false;
            }

            _listen = new IPEndPoint(address, port);
            _bidirectional = bidirectional;
            _status = "Settings saved";
            AddEventLocked(_status);
        }

        SaveConfig();
        error = string.Empty;
        return true;
    }

    public bool AddTarget(string name, string addressText, int port, out string error)
    {
        if (!ValidateTarget(name, addressText, port, out IPEndPoint? endpoint, out error))
            return false;

        lock (_stateLock)
        {
            if (_targets.Any(target => target.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                error = $"A destination named '{name.Trim()}' already exists.";
                return false;
            }

            _targets.Add(new RelayTarget(name.Trim(), endpoint));
            _status = $"Added {name.Trim()} → {endpoint}";
            AddEventLocked(_status);
        }

        SaveConfig();
        return true;
    }

    public bool UpdateTarget(Guid id, string name, string addressText, int port, out string error)
    {
        if (!ValidateTarget(name, addressText, port, out IPEndPoint? endpoint, out error))
            return false;

        lock (_stateLock)
        {
            RelayTarget? target = _targets.FirstOrDefault(item => item.Id == id);
            if (target is null)
            {
                error = "The selected destination no longer exists.";
                return false;
            }

            if (_targets.Any(item => item.Id != id && item.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                error = $"A destination named '{name.Trim()}' already exists.";
                return false;
            }

            target.Name = name.Trim();
            target.Endpoint = endpoint;
            _status = $"Updated {target.Name} → {endpoint}";
            AddEventLocked(_status);
        }

        SaveConfig();
        return true;
    }

    public void RemoveTarget(Guid id)
    {
        lock (_stateLock)
        {
            RelayTarget? target = _targets.FirstOrDefault(item => item.Id == id);
            if (target is null)
                return;
            _targets.Remove(target);
            _status = $"Removed {target.Name}";
            AddEventLocked(_status);
        }
        SaveConfig();
    }

    public void ClearStatistics()
    {
        lock (_stateLock)
        {
            _wsjtxToAppsPackets = 0;
            _wsjtxToAppsBytes = 0;
            _appsToWsjtxPackets = 0;
            _appsToWsjtxBytes = 0;
            _droppedPackets = 0;
            _unknownPackets = 0;
            _sendErrors = 0;
            _lastPacket = "None";
            foreach (RelayTarget target in _targets)
            {
                target.Packets = 0;
                target.Bytes = 0;
                target.SendErrors = 0;
            }
            _status = "Statistics cleared";
            AddEventLocked(_status);
        }
    }

    public bool LoadConfig()
    {
        if (!File.Exists(_configPath))
            return false;

        IPEndPoint listen = _listen;
        bool bidirectional = _bidirectional;
        var loadedTargets = new List<RelayTarget>();

        try
        {
            foreach (string rawLine in File.ReadLines(_configPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                int equals = line.IndexOf('=');
                if (equals < 0)
                    continue;

                string key = line[..equals].Trim().ToLowerInvariant();
                string value = line[(equals + 1)..].Trim();
                if (key == "listen" && TryParseEndpoint(value, out IPEndPoint endpoint))
                    listen = endpoint;
                else if (key == "bidirectional")
                    bidirectional = value.ToLowerInvariant() is "true" or "yes" or "1" or "on";
                else if (key == "target")
                {
                    int separator = value.LastIndexOf('|');
                    if (separator > 0 && TryParseEndpoint(value[(separator + 1)..], out IPEndPoint targetEndpoint))
                    {
                        string name = value[..separator].Trim();
                        if (name.Length > 0)
                            loadedTargets.Add(new RelayTarget(name, targetEndpoint));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_stateLock)
            {
                _status = $"Could not load settings: {ex.Message}";
                AddEventLocked(_status);
            }
            return false;
        }

        lock (_stateLock)
        {
            _listen = listen;
            _bidirectional = bidirectional;
            _targets.Clear();
            _targets.AddRange(loadedTargets);
            _status = "Settings loaded";
            AddEventLocked(_status);
        }
        return true;
    }

    public void SaveConfig()
    {
        string contents;
        lock (_stateLock)
        {
            var lines = new List<string>
            {
                "# WSJT-X UDP Fanout configuration",
                $"listen={_listen}",
                $"bidirectional={_bidirectional.ToString().ToLowerInvariant()}",
                "dashboard_ms=1000"
            };
            lines.AddRange(_targets.Select(target => $"target={target.Name}|{target.Endpoint}"));
            contents = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(_configPath, contents, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_stateLock)
            {
                _status = $"Could not save settings: {ex.Message}";
                AddEventLocked(_status);
            }
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[65535];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveFromResult result = await socket.ReceiveFromAsync(
                    buffer,
                    SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0),
                    cancellationToken).ConfigureAwait(false);

                if (result.RemoteEndPoint is not IPEndPoint from)
                    continue;

                ReadOnlyMemory<byte> packet = buffer.AsMemory(0, result.ReceivedBytes);
                PacketKind kind = ClassifyWsjtPacket(packet.Span, out uint schema, out uint type);
                bool bidirectional;
                IPEndPoint? learned;
                lock (_stateLock)
                {
                    bidirectional = _bidirectional;
                    learned = _learnedWsjtxSource;
                }

                if (!bidirectional && kind == PacketKind.AppToWsjtx)
                {
                    RecordDrop($"Dropped app command from {from}; read-only mode is enabled");
                }
                else if (kind == PacketKind.WsjtxToApps)
                {
                    await RouteFromWsjtxAsync(socket, packet, from, kind, schema, type, cancellationToken);
                }
                else if (kind == PacketKind.AppToWsjtx)
                {
                    await RouteToWsjtxAsync(socket, packet, from, kind, schema, type, cancellationToken);
                }
                else if (kind == PacketKind.Ambiguous)
                {
                    if (learned is not null && from.Equals(learned))
                        await RouteFromWsjtxAsync(socket, packet, from, kind, schema, type, cancellationToken);
                    else if (bidirectional && learned is not null)
                        await RouteToWsjtxAsync(socket, packet, from, kind, schema, type, cancellationToken);
                    else
                        await RouteFromWsjtxAsync(socket, packet, from, kind, schema, type, cancellationToken);
                }
                else if (learned is not null && from.Equals(learned))
                {
                    await RouteFromWsjtxAsync(socket, packet, from, kind, schema, type, cancellationToken);
                }
                else if (learned is null)
                {
                    lock (_stateLock)
                        _unknownPackets++;
                    await RouteFromWsjtxAsync(socket, packet, from, kind, schema, type, cancellationToken);
                }
                else
                {
                    lock (_stateLock)
                    {
                        _unknownPackets++;
                        _droppedPackets++;
                        _status = $"Dropped unknown packet from {from}";
                        AddEventLocked(_status);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException ex)
        {
            lock (_stateLock)
            {
                _status = $"Network error: {ex.SocketErrorCode}";
                AddEventLocked(_status);
            }
        }
        finally
        {
            lock (_stateLock)
                _isRunning = false;
        }
    }

    private async Task RouteFromWsjtxAsync(
        Socket socket,
        ReadOnlyMemory<byte> packet,
        IPEndPoint from,
        PacketKind kind,
        uint schema,
        uint type,
        CancellationToken cancellationToken)
    {
        List<(Guid Id, IPEndPoint Endpoint)> destinations;
        lock (_stateLock)
        {
            _learnedWsjtxSource = from;
            _wsjtxToAppsPackets++;
            _wsjtxToAppsBytes += (ulong)packet.Length;
            _lastPacket = $"{PacketKindText(kind)} · type {type} · schema {schema} · {packet.Length:N0} bytes";
            destinations = _targets.Select(target => (target.Id, target.Endpoint)).ToList();
        }

        foreach ((Guid id, IPEndPoint endpoint) in destinations)
        {
            try
            {
                await socket.SendToAsync(packet, SocketFlags.None, endpoint, cancellationToken).ConfigureAwait(false);
                lock (_stateLock)
                {
                    RelayTarget? target = _targets.FirstOrDefault(item => item.Id == id);
                    if (target is not null)
                    {
                        target.Packets++;
                        target.Bytes += (ulong)packet.Length;
                    }
                }
            }
            catch (SocketException)
            {
                lock (_stateLock)
                {
                    RelayTarget? target = _targets.FirstOrDefault(item => item.Id == id);
                    if (target is not null)
                        target.SendErrors++;
                    _sendErrors++;
                }
            }
        }
    }

    private async Task RouteToWsjtxAsync(
        Socket socket,
        ReadOnlyMemory<byte> packet,
        IPEndPoint from,
        PacketKind kind,
        uint schema,
        uint type,
        CancellationToken cancellationToken)
    {
        IPEndPoint? destination;
        lock (_stateLock)
        {
            destination = _learnedWsjtxSource;
            _appsToWsjtxPackets++;
            _appsToWsjtxBytes += (ulong)packet.Length;
            _lastPacket = $"{PacketKindText(kind)} · type {type} · schema {schema} · {packet.Length:N0} bytes";
        }

        if (destination is null)
        {
            RecordDrop("Dropped app command; waiting to learn the WSJT-X source");
            return;
        }

        try
        {
            await socket.SendToAsync(packet, SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            lock (_stateLock)
            {
                _sendErrors++;
                _status = $"Could not send to WSJT-X: {ex.SocketErrorCode}";
                AddEventLocked(_status);
            }
        }
    }

    private void RecordDrop(string message)
    {
        lock (_stateLock)
        {
            _droppedPackets++;
            _status = message;
            AddEventLocked(message);
        }
    }

    private void ApplyCommandLine(string[] args)
    {
        bool customTargets = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].ToLowerInvariant();
            switch (arg)
            {
                case "--config":
                    i++;
                    break;
                case "--listen":
                    if (++i >= args.Length || !TryParseEndpoint(args[i], out IPEndPoint listen))
                        throw new ArgumentException("--listen requires a valid IPv4:port endpoint.");
                    _listen = listen;
                    break;
                case "--target":
                    if (++i >= args.Length || !TryParseTargetArgument(args[i], out string name, out IPEndPoint endpoint))
                        throw new ArgumentException("--target requires name=IPv4:port.");
                    if (!customTargets)
                    {
                        _targets.Clear();
                        customTargets = true;
                    }
                    _targets.Add(new RelayTarget(name, endpoint));
                    break;
                case "--bidirectional":
                    _bidirectional = true;
                    break;
                case "--read-only":
                    _bidirectional = false;
                    break;
                case "--dashboard-ms":
                    if (++i >= args.Length || !int.TryParse(args[i], out _))
                        throw new ArgumentException("--dashboard-ms requires a number.");
                    break;
                case "--help":
                case "-h":
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }
    }

    private static bool ValidateTarget(
        string name,
        string addressText,
        int port,
        out IPEndPoint endpoint,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            endpoint = null!;
            error = "Enter a destination name.";
            return false;
        }
        if (!IPAddress.TryParse(addressText.Trim(), out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            endpoint = null!;
            error = "Enter a valid IPv4 destination address.";
            return false;
        }
        if (port is < 1 or > 65535)
        {
            endpoint = null!;
            error = "The destination port must be between 1 and 65535.";
            return false;
        }
        endpoint = new IPEndPoint(address, port);
        error = string.Empty;
        return true;
    }

    private static bool TryParseEndpoint(string rawValue, out IPEndPoint endpoint)
    {
        string value = rawValue.Trim();
        if (!value.Contains(':'))
            value = $"127.0.0.1:{value}";
        int colon = value.LastIndexOf(':');
        if (colon <= 0 ||
            !IPAddress.TryParse(value[..colon], out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(value[(colon + 1)..], out int port) ||
            port is < 1 or > 65535)
        {
            endpoint = null!;
            return false;
        }
        endpoint = new IPEndPoint(address, port);
        return true;
    }

    private static bool TryParseTargetArgument(string value, out string name, out IPEndPoint endpoint)
    {
        int separator = value.IndexOf('=');
        if (separator < 0)
            separator = value.IndexOf('|');
        if (separator < 0)
            separator = value.LastIndexOf(':');
        if (separator <= 0)
        {
            name = string.Empty;
            endpoint = null!;
            return false;
        }
        name = value[..separator].Trim();
        if (name.Length == 0)
        {
            endpoint = null!;
            return false;
        }
        return TryParseEndpoint(value[(separator + 1)..], out endpoint);
    }

    private static PacketKind ClassifyWsjtPacket(ReadOnlySpan<byte> data, out uint schema, out uint type)
    {
        schema = 0;
        type = 0;
        if (data.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(data) != WsjtxMagic)
            return PacketKind.Unknown;
        schema = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        type = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        return type switch
        {
            1 or 2 or 5 or 10 or 12 => PacketKind.WsjtxToApps,
            4 or 7 or 8 or 9 or 11 or 13 or 14 or 15 => PacketKind.AppToWsjtx,
            0 or 3 or 6 => PacketKind.Ambiguous,
            _ => PacketKind.Unknown
        };
    }

    private static string PacketKindText(PacketKind kind) => kind switch
    {
        PacketKind.WsjtxToApps => "WSJT-X → apps",
        PacketKind.AppToWsjtx => "Apps → WSJT-X",
        PacketKind.Ambiguous => "Ambiguous packet",
        _ => "Unknown packet"
    };

    private void AddDefaultTargetsLocked()
    {
        _targets.Add(new RelayTarget("GridTracker", new IPEndPoint(IPAddress.Loopback, 2237)));
        _targets.Add(new RelayTarget("Hamilton Auto FT8", new IPEndPoint(IPAddress.Loopback, 2238)));
        _targets.Add(new RelayTarget("WRL CAT Control", new IPEndPoint(IPAddress.Loopback, 2239)));
    }

    private void AddEventLocked(string message)
    {
        _events.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        if (_events.Count > 100)
            _events.RemoveAt(_events.Count - 1);
    }

    private static string DefaultConfigPath()
    {
        string basePath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(basePath))
            basePath = Environment.CurrentDirectory;
        return Path.Combine(basePath, "WsjtxUdpFanout", "WsjtxUdpFanout.ini");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        StopAsync().GetAwaiter().GetResult();
        _disposed = true;
    }
}
