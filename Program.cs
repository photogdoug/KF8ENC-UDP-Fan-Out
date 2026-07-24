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

internal sealed class Target(string name, IPEndPoint endpoint)
{
    public string Name { get; set; } = name;
    public IPEndPoint Endpoint { get; set; } = endpoint;
    public ulong Packets { get; set; }
    public ulong Bytes { get; set; }
    public ulong SendErrors { get; set; }

    public Target Copy() => new(Name, Endpoint)
    {
        Packets = Packets,
        Bytes = Bytes,
        SendErrors = SendErrors
    };
}

internal sealed class Counters
{
    public ulong WsjtxToAppsPackets { get; set; }
    public ulong WsjtxToAppsBytes { get; set; }
    public ulong AppsToWsjtxPackets { get; set; }
    public ulong AppsToWsjtxBytes { get; set; }
    public ulong DroppedPackets { get; set; }
    public ulong UnknownPackets { get; set; }
    public ulong SendErrors { get; set; }

    public Counters Copy() => (Counters)MemberwiseClone();
}

internal sealed class StateSnapshot
{
    public required IPEndPoint Listen { get; init; }
    public required bool Bidirectional { get; init; }
    public required int DashboardMs { get; init; }
    public required List<Target> Targets { get; init; }
    public required Counters Counters { get; init; }
    public required IPEndPoint? LearnedWsjtxSource { get; init; }
    public required bool ShowHelp { get; init; }
    public required string LastPacket { get; init; }
    public required string Status { get; init; }
    public required List<string> Events { get; init; }
    public required string ConfigPath { get; init; }
}

internal static class Program
{
    private const uint WsjtxMagic = 0xADBCCBDA;
    private static readonly object StateLock = new();
    private static readonly CancellationTokenSource Shutdown = new();
    private static IPEndPoint _listen = new(IPAddress.Loopback, 2236);
    private static bool _bidirectional = true;
    private static int _dashboardMs = 1000;
    private static readonly List<Target> Targets = [];
    private static Counters _counters = new();
    private static IPEndPoint? _learnedWsjtxSource;
    private static bool _showHelp;
    private static string _lastPacket = "none";
    private static string _status = "Ready.";
    private static readonly List<string> Events = [];
    private static string _configPath = DefaultConfigPath();

    public static async Task<int> Main(string[] args)
    {
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Shutdown.Cancel();
        };

        AddDefaultTargets();
        if (!LoadConfig(_configPath, keepDefaults: false))
            SaveConfigAndReport();

        int parseResult = ParseArguments(args);
        if (parseResult >= 0)
            return parseResult;

        lock (StateLock)
        {
            if (Targets.Count == 0)
                AddDefaultTargetsLocked();
        }

        Task udp = UdpWorkerAsync(Shutdown.Token);
        Task dashboard = DashboardWorkerAsync(Shutdown.Token);
        await udp.ConfigureAwait(false);
        Shutdown.Cancel();
        await dashboard.ConfigureAwait(false);
        Console.WriteLine("\nStopping.");
        return 0;
    }

    private static int ParseArguments(string[] args)
    {
        bool userProvidedTargets = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;

                case "--listen":
                    if (++i >= args.Length || !TryParseEndpoint(args[i], out IPEndPoint listen))
                        return 1;
                    lock (StateLock)
                        _listen = listen;
                    break;

                case "--target":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("--target requires name=ip:port");
                        return 1;
                    }
                    if (!TryParseTargetArgument(args[i], out Target target))
                        return 1;
                    lock (StateLock)
                    {
                        if (!userProvidedTargets)
                        {
                            Targets.Clear();
                            userProvidedTargets = true;
                        }
                        Targets.Add(target);
                    }
                    break;

                case "--bidirectional":
                    lock (StateLock)
                        _bidirectional = true;
                    break;

                case "--read-only":
                    lock (StateLock)
                        _bidirectional = false;
                    break;

                case "--dashboard-ms":
                    if (++i >= args.Length || !int.TryParse(args[i], out int ms) || ms is < 250 or > 10000)
                    {
                        Console.Error.WriteLine("--dashboard-ms must be between 250 and 10000");
                        return 1;
                    }
                    lock (StateLock)
                        _dashboardMs = ms;
                    break;

                case "--config":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("--config requires a path");
                        return 1;
                    }
                    lock (StateLock)
                        _configPath = Path.GetFullPath(args[i]);
                    LoadConfig(_configPath, keepDefaults: false);
                    break;

                default:
                    Console.Error.WriteLine($"Unknown argument: {arg}\n");
                    PrintUsage();
                    return 1;
            }
        }

        return -1;
    }

    private static async Task UdpWorkerAsync(CancellationToken cancellationToken)
    {
        IPEndPoint listen;
        lock (StateLock)
            listen = _listen;

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(listen);
        }
        catch (SocketException ex)
        {
            SetStatus($"bind failed, socket error {ex.SocketErrorCode}. Another program may already be listening on {listen}.", true);
            Shutdown.Cancel();
            return;
        }

        SetStatus($"Listening on {listen}", true);
        byte[] buffer = new byte[65535];

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(
                    buffer,
                    SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                SetStatus($"receive failed, socket error {ex.SocketErrorCode}", true);
                continue;
            }

            if (result.RemoteEndPoint is not IPEndPoint from)
                continue;

            ReadOnlyMemory<byte> packet = buffer.AsMemory(0, result.ReceivedBytes);
            PacketKind kind = ClassifyWsjtPacket(packet.Span, out uint schema, out uint type);
            bool bidirectional;
            IPEndPoint? learned;
            lock (StateLock)
            {
                bidirectional = _bidirectional;
                learned = _learnedWsjtxSource;
            }

            if (!bidirectional && kind == PacketKind.AppToWsjtx)
            {
                lock (StateLock)
                {
                    _counters.DroppedPackets++;
                    _status = $"Dropped app command from {from} because read-only mode is enabled.";
                    AddEventLocked(_status);
                }
                continue;
            }

            if (kind == PacketKind.WsjtxToApps)
            {
                await MarkWsjtxToAppsAsync(socket, packet, from, kind, schema, type, cancellationToken);
            }
            else if (kind == PacketKind.AppToWsjtx)
            {
                await MarkAppToWsjtxAsync(socket, packet, from, kind, schema, type, cancellationToken);
            }
            else if (kind == PacketKind.Ambiguous)
            {
                if (learned is not null && from.Equals(learned))
                    await MarkWsjtxToAppsAsync(socket, packet, from, kind, schema, type, cancellationToken);
                else if (bidirectional && learned is not null)
                    await MarkAppToWsjtxAsync(socket, packet, from, kind, schema, type, cancellationToken);
                else
                    await MarkWsjtxToAppsAsync(socket, packet, from, kind, schema, type, cancellationToken);
            }
            else if (learned is not null && from.Equals(learned))
            {
                await MarkWsjtxToAppsAsync(socket, packet, from, kind, schema, type, cancellationToken);
            }
            else if (learned is null)
            {
                lock (StateLock)
                    _counters.UnknownPackets++;
                await MarkWsjtxToAppsAsync(socket, packet, from, kind, schema, type, cancellationToken);
            }
            else
            {
                lock (StateLock)
                {
                    _counters.UnknownPackets++;
                    _counters.DroppedPackets++;
                    _status = $"Dropped unknown packet from {from}";
                    AddEventLocked(_status);
                }
            }
        }
    }

    private static async Task MarkWsjtxToAppsAsync(
        Socket socket,
        ReadOnlyMemory<byte> packet,
        IPEndPoint from,
        PacketKind kind,
        uint schema,
        uint type,
        CancellationToken cancellationToken)
    {
        lock (StateLock)
        {
            _learnedWsjtxSource = from;
            _counters.WsjtxToAppsPackets++;
            _counters.WsjtxToAppsBytes += (ulong)packet.Length;
            _lastPacket = $"{PacketKindText(kind)} type={type} schema={schema} from={from} bytes={packet.Length}";
            AddEventLocked(_lastPacket);
        }
        await ForwardToTargetsAsync(socket, packet, cancellationToken);
    }

    private static async Task MarkAppToWsjtxAsync(
        Socket socket,
        ReadOnlyMemory<byte> packet,
        IPEndPoint from,
        PacketKind kind,
        uint schema,
        uint type,
        CancellationToken cancellationToken)
    {
        IPEndPoint? destination;
        lock (StateLock)
        {
            destination = _learnedWsjtxSource;
            _counters.AppsToWsjtxPackets++;
            _counters.AppsToWsjtxBytes += (ulong)packet.Length;
            _lastPacket = $"{PacketKindText(kind)} type={type} schema={schema} from={from} bytes={packet.Length}";
        }

        if (destination is null)
        {
            lock (StateLock)
            {
                _counters.DroppedPackets++;
                _status = "Dropped app command; WSJT-X source is not learned yet.";
                AddEventLocked(_status);
            }
            return;
        }

        try
        {
            await socket.SendToAsync(packet, SocketFlags.None, destination, cancellationToken).ConfigureAwait(false);
            lock (StateLock)
                AddEventLocked($"{_lastPacket} -> {destination}");
        }
        catch (SocketException ex)
        {
            lock (StateLock)
            {
                _counters.SendErrors++;
                _status = $"send to WSJT-X failed, socket error {ex.SocketErrorCode}";
                AddEventLocked(_status);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ForwardToTargetsAsync(
        Socket socket,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken)
    {
        List<Target> destinations;
        lock (StateLock)
            destinations = Targets.ToList();

        foreach (Target target in destinations)
        {
            try
            {
                await socket.SendToAsync(packet, SocketFlags.None, target.Endpoint, cancellationToken).ConfigureAwait(false);
                lock (StateLock)
                {
                    Target? current = Targets.FirstOrDefault(item => ReferenceEquals(item, target));
                    if (current is not null)
                    {
                        current.Packets++;
                        current.Bytes += (ulong)packet.Length;
                    }
                }
            }
            catch (SocketException)
            {
                lock (StateLock)
                {
                    Target? current = Targets.FirstOrDefault(item => ReferenceEquals(item, target));
                    if (current is not null)
                        current.SendErrors++;
                    _counters.SendErrors++;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task DashboardWorkerAsync(CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            await WaitForShutdownAsync(cancellationToken);
            return;
        }

        var inputBuffer = new StringBuilder();
        DateTime nextRefresh = DateTime.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            while (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    string command = inputBuffer.ToString();
                    inputBuffer.Clear();
                    HandleCommand(command);
                    nextRefresh = DateTime.MinValue;
                }
                else if (key.Key == ConsoleKey.Backspace && inputBuffer.Length > 0)
                {
                    inputBuffer.Length--;
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    inputBuffer.Clear();
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    inputBuffer.Append(key.KeyChar);
                }
            }

            if (DateTime.UtcNow < nextRefresh)
            {
                await DelayAsync(25, cancellationToken);
                continue;
            }

            StateSnapshot snapshot = TakeSnapshot();
            Console.Clear();
            Console.WriteLine("WSJT-X UDP Fanout");
            Console.WriteLine("=================\n");
            Console.WriteLine($"Listen: {snapshot.Listen}");
            Console.WriteLine($"Mode: {(snapshot.Bidirectional ? "bidirectional" : "read-only")}");
            Console.WriteLine($"Config: {snapshot.ConfigPath}");
            Console.WriteLine($"WSJT-X source: {snapshot.LearnedWsjtxSource?.ToString() ?? "not learned yet"}");
            Console.WriteLine($"Status: {snapshot.Status}\n");

            Console.WriteLine("Traffic");
            Console.WriteLine($"  WSJT-X -> apps:  {snapshot.Counters.WsjtxToAppsPackets} packets, {snapshot.Counters.WsjtxToAppsBytes} bytes");
            Console.WriteLine($"  apps -> WSJT-X:  {snapshot.Counters.AppsToWsjtxPackets} packets, {snapshot.Counters.AppsToWsjtxBytes} bytes");
            Console.WriteLine($"  send errors:     {snapshot.Counters.SendErrors}");
            Console.WriteLine($"  dropped packets: {snapshot.Counters.DroppedPackets}");
            Console.WriteLine($"  unknown packets: {snapshot.Counters.UnknownPackets}");
            Console.WriteLine($"  last packet:     {snapshot.LastPacket}\n");

            Console.WriteLine("Destinations");
            if (snapshot.Targets.Count == 0)
            {
                Console.WriteLine("  none");
            }
            else
            {
                foreach (Target target in snapshot.Targets)
                    Console.WriteLine($"  {target.Name,-22} {target.Endpoint,-21} packets={target.Packets,-8} bytes={target.Bytes,-10} errors={target.SendErrors}");
            }

            Console.WriteLine("\nRecent events");
            if (snapshot.Events.Count == 0)
                Console.WriteLine("  none");
            else
                snapshot.Events.ForEach(eventText => Console.WriteLine($"  {eventText}"));

            if (snapshot.ShowHelp)
            {
                Console.WriteLine(
                    "\nCommands\n" +
                    "  help | hidehelp | quit\n" +
                    "  add JTSync 2249\n" +
                    "  add \"Hamilton Auto FT8\" 127.0.0.1:2238\n" +
                    "  set JTSync 127.0.0.1:2249\n" +
                    "  remove JTSync\n" +
                    "  rename JTSync \"JT Sync\"\n" +
                    "  save | load | config\n" +
                    "  bidirectional | read-only\n" +
                    "  refresh 500\n" +
                    "  clearstats");
            }

            Console.WriteLine("\nType 'help' for commands. Example: add JTSync 2249");
            Console.Write($"> {inputBuffer}");
            nextRefresh = DateTime.UtcNow.AddMilliseconds(Math.Max(250, snapshot.DashboardMs));
        }
    }

    private static void HandleCommand(string rawLine)
    {
        List<string> args = SplitCommandLine(rawLine.Trim());
        if (args.Count == 0)
            return;

        string command = args[0].ToLowerInvariant();
        bool changedConfig = false;
        string message;

        switch (command)
        {
            case "help":
            case "?":
                lock (StateLock)
                {
                    _showHelp = true;
                    _status = "Help shown. Type hidehelp to close it.";
                    AddEventLocked(_status);
                }
                return;

            case "hidehelp":
                lock (StateLock)
                {
                    _showHelp = false;
                    _status = "Help hidden.";
                    AddEventLocked(_status);
                }
                return;

            case "quit":
            case "exit":
                Shutdown.Cancel();
                return;

            case "config":
                SetStatus("Configuration is shown on the dashboard.", true);
                return;

            case "save":
                SaveConfigAndReport();
                return;

            case "load":
                bool loaded = LoadConfig(_configPath, keepDefaults: false);
                SetStatus(loaded ? $"Loaded config: {_configPath}" : $"Could not load config: {_configPath}", true);
                return;

            case "add":
                if (args.Count < 3)
                {
                    message = "Usage: add <name> <port|ip:port>";
                }
                else if (!TryParseEndpoint(args[2], out IPEndPoint addEndpoint))
                {
                    message = "Invalid endpoint.";
                }
                else
                {
                    lock (StateLock)
                    {
                        if (FindTargetLocked(args[1]) is not null)
                            message = $"Target already exists: {args[1]}";
                        else
                        {
                            Targets.Add(new Target(args[1], addEndpoint));
                            message = $"Added {args[1]} -> {addEndpoint}";
                            changedConfig = true;
                        }
                    }
                }
                break;

            case "set":
                if (args.Count < 3)
                {
                    message = "Usage: set <name> <port|ip:port>";
                }
                else if (!TryParseEndpoint(args[2], out IPEndPoint setEndpoint))
                {
                    message = "Invalid endpoint.";
                }
                else
                {
                    lock (StateLock)
                    {
                        Target? target = FindTargetLocked(args[1]);
                        if (target is null)
                            message = $"No target named: {args[1]}";
                        else
                        {
                            target.Endpoint = setEndpoint;
                            message = $"Updated {args[1]} -> {setEndpoint}";
                            changedConfig = true;
                        }
                    }
                }
                break;

            case "remove":
            case "delete":
                if (args.Count < 2)
                {
                    message = "Usage: remove <name>";
                }
                else
                {
                    lock (StateLock)
                    {
                        Target? target = FindTargetLocked(args[1]);
                        if (target is null)
                            message = $"No target named: {args[1]}";
                        else
                        {
                            Targets.Remove(target);
                            message = $"Removed {target.Name}";
                            changedConfig = true;
                        }
                    }
                }
                break;

            case "rename":
                if (args.Count < 3)
                {
                    message = "Usage: rename <old-name> <new-name>";
                }
                else
                {
                    lock (StateLock)
                    {
                        Target? target = FindTargetLocked(args[1]);
                        if (target is null)
                            message = $"No target named: {args[1]}";
                        else if (FindTargetLocked(args[2]) is not null)
                            message = $"Target already exists: {args[2]}";
                        else
                        {
                            target.Name = args[2];
                            message = $"Renamed {args[1]} to {args[2]}";
                            changedConfig = true;
                        }
                    }
                }
                break;

            case "bidirectional":
                lock (StateLock)
                    _bidirectional = true;
                message = "Bidirectional relay enabled.";
                changedConfig = true;
                break;

            case "read-only":
                lock (StateLock)
                    _bidirectional = false;
                message = "Read-only mode enabled.";
                changedConfig = true;
                break;

            case "refresh":
                if (args.Count < 2)
                    message = "Usage: refresh <milliseconds>";
                else if (!int.TryParse(args[1], out int refreshMs) || refreshMs is < 250 or > 10000)
                    message = "Refresh must be between 250 and 10000 milliseconds.";
                else
                {
                    lock (StateLock)
                        _dashboardMs = refreshMs;
                    message = $"Dashboard refresh set to {refreshMs} ms.";
                    changedConfig = true;
                }
                break;

            case "clearstats":
                lock (StateLock)
                {
                    _counters = new Counters();
                    foreach (Target target in Targets)
                    {
                        target.Packets = 0;
                        target.Bytes = 0;
                        target.SendErrors = 0;
                    }
                    _lastPacket = "none";
                }
                message = "Counters cleared.";
                break;

            default:
                message = $"Unknown command: {args[0]} (type help)";
                break;
        }

        SetStatus(message, true);
        if (changedConfig)
            SaveConfigAndReport();
    }

    private static bool LoadConfig(string path, bool keepDefaults)
    {
        if (!File.Exists(path))
            return false;

        IPEndPoint listen;
        bool bidirectional;
        int dashboardMs;
        lock (StateLock)
        {
            listen = _listen;
            bidirectional = _bidirectional;
            dashboardMs = _dashboardMs;
        }
        var loadedTargets = new List<Target>();

        try
        {
            foreach (string rawLine in File.ReadLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                int equals = line.IndexOf('=');
                if (equals < 0)
                    continue;

                string key = line[..equals].Trim().ToLowerInvariant();
                string value = line[(equals + 1)..].Trim();
                switch (key)
                {
                    case "listen" when TryParseEndpoint(value, out IPEndPoint endpoint):
                        listen = endpoint;
                        break;
                    case "bidirectional":
                        bidirectional = value.ToLowerInvariant() is "true" or "yes" or "1" or "on";
                        break;
                    case "dashboard_ms" when int.TryParse(value, out int ms) && ms is >= 250 and <= 10000:
                        dashboardMs = ms;
                        break;
                    case "target":
                        int separator = value.LastIndexOf('|');
                        if (separator > 0 &&
                            TryParseEndpoint(value[(separator + 1)..].Trim(), out IPEndPoint targetEndpoint))
                        {
                            string name = value[..separator].Trim();
                            if (name.Length > 0)
                                loadedTargets.Add(new Target(name, targetEndpoint));
                        }
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not load config: {ex.Message}", true);
            return false;
        }

        lock (StateLock)
        {
            _listen = listen;
            _bidirectional = bidirectional;
            _dashboardMs = dashboardMs;
            if (!keepDefaults || loadedTargets.Count > 0)
            {
                Targets.Clear();
                Targets.AddRange(loadedTargets);
            }
        }
        return true;
    }

    private static void SaveConfigAndReport()
    {
        string path;
        string contents;
        lock (StateLock)
        {
            path = _configPath;
            var lines = new List<string>
            {
                "# WSJT-X UDP Fanout configuration",
                $"listen={_listen}",
                $"bidirectional={_bidirectional.ToString().ToLowerInvariant()}",
                $"dashboard_ms={_dashboardMs}"
            };
            lines.AddRange(Targets.Select(target => $"target={target.Name}|{target.Endpoint}"));
            contents = string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            SetStatus($"Saved config: {path}", true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not save config: {ex.Message}", true);
        }
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
            Console.Error.WriteLine($"Invalid endpoint, expected IPv4:port or port: {rawValue}");
            endpoint = null!;
            return false;
        }

        endpoint = new IPEndPoint(address, port);
        return true;
    }

    private static bool TryParseTargetArgument(string value, out Target target)
    {
        int separator = value.IndexOf('=');
        if (separator < 0)
            separator = value.IndexOf('|');
        if (separator < 0)
            separator = value.LastIndexOf(':');
        if (separator <= 0)
        {
            Console.Error.WriteLine($"Invalid target. Expected name=ip:port, name|ip:port, or name:port: {value}");
            target = null!;
            return false;
        }

        string name = value[..separator].Trim();
        if (name.Length == 0 || !TryParseEndpoint(value[(separator + 1)..].Trim(), out IPEndPoint endpoint))
        {
            target = null!;
            return false;
        }
        target = new Target(name, endpoint);
        return true;
    }

    private static List<string> SplitCommandLine(string line)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (char character in line)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(character) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }
        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }

    private static string PacketKindText(PacketKind kind) => kind switch
    {
        PacketKind.WsjtxToApps => "WSJT-X -> apps",
        PacketKind.AppToWsjtx => "apps -> WSJT-X",
        PacketKind.Ambiguous => "ambiguous",
        _ => "unknown"
    };

    private static void AddDefaultTargets()
    {
        lock (StateLock)
            AddDefaultTargetsLocked();
    }

    private static void AddDefaultTargetsLocked()
    {
        Targets.Add(new Target("GridTracker", new IPEndPoint(IPAddress.Loopback, 2237)));
        Targets.Add(new Target("Hamilton Auto FT8", new IPEndPoint(IPAddress.Loopback, 2238)));
        Targets.Add(new Target("WRL CAT Control", new IPEndPoint(IPAddress.Loopback, 2239)));
    }

    private static Target? FindTargetLocked(string name) =>
        Targets.FirstOrDefault(target => string.Equals(target.Name, name, StringComparison.OrdinalIgnoreCase));

    private static void AddEventLocked(string eventText)
    {
        Events.Add(eventText);
        if (Events.Count > 10)
            Events.RemoveAt(0);
    }

    private static void SetStatus(string message, bool addEvent)
    {
        lock (StateLock)
        {
            _status = message;
            if (addEvent)
                AddEventLocked(message);
        }
    }

    private static StateSnapshot TakeSnapshot()
    {
        lock (StateLock)
        {
            return new StateSnapshot
            {
                Listen = _listen,
                Bidirectional = _bidirectional,
                DashboardMs = _dashboardMs,
                Targets = Targets.Select(target => target.Copy()).ToList(),
                Counters = _counters.Copy(),
                LearnedWsjtxSource = _learnedWsjtxSource,
                ShowHelp = _showHelp,
                LastPacket = _lastPacket,
                Status = _status,
                Events = [.. Events],
                ConfigPath = _configPath
            };
        }
    }

    private static string DefaultConfigPath()
    {
        string basePath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(basePath))
            basePath = Environment.CurrentDirectory;
        return Path.Combine(basePath, "WsjtxUdpFanout", "WsjtxUdpFanout.ini");
    }

    private static async Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(milliseconds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "WSJT-X UDP Fanout\n\n" +
            "Usage:\n" +
            "  WsjtxUdpFanout.exe [options]\n\n" +
            "Options:\n" +
            "  --listen ip:port       Listen endpoint. Default: 127.0.0.1:2236\n" +
            "  --target name=ip:port  Add destination. Repeat for multiple targets.\n" +
            "  --target name:port     Also accepted when the port is local-only.\n" +
            "  --bidirectional        Enable app command relay back to WSJT-X. Default.\n" +
            "  --read-only            Disable app command relay.\n" +
            "  --dashboard-ms n       Dashboard refresh, 250-10000 ms.\n" +
            "  --config path          Use a custom config file.\n" +
            "  --help                 Show this help.");
    }
}
