// WSJT-X UDP Fanout
// Build with:
//   cl /EHsc /std:c++17 WsjtxUdpFanout.cpp ws2_32.lib /Fe:WsjtxUdpFanout.exe

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cctype>
#include <cstdint>
#include <cstdlib>
#include <exception>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <utility>
#include <vector>
#include <conio.h>

#pragma comment(lib, "Ws2_32.lib")

namespace fs = std::filesystem;

static std::atomic<bool> g_running{true};

BOOL WINAPI consoleHandler(DWORD controlType)
{
    switch (controlType)
    {
    case CTRL_C_EVENT:
    case CTRL_BREAK_EVENT:
    case CTRL_CLOSE_EVENT:
    case CTRL_SHUTDOWN_EVENT:
        g_running = false;
        return TRUE;
    default:
        return FALSE;
    }
}

struct Endpoint
{
    std::string text;
    sockaddr_in addr{};
};

struct Target
{
    std::string name;
    Endpoint endpoint;
    uint64_t packets = 0;
    uint64_t bytes = 0;
    uint64_t sendErrors = 0;
};

struct Counters
{
    uint64_t wsjtxToAppsPackets = 0;
    uint64_t wsjtxToAppsBytes = 0;
    uint64_t appsToWsjtxPackets = 0;
    uint64_t appsToWsjtxBytes = 0;
    uint64_t droppedPackets = 0;
    uint64_t unknownPackets = 0;
    uint64_t sendErrors = 0;
};

struct AppState
{
    Endpoint listen;
    bool bidirectional = true;
    int dashboardMs = 1000;
    std::vector<Target> targets;
    Counters counters;
    sockaddr_in learnedWsjtxSource{};
    bool haveWsjtxSource = false;
    bool showHelp = false;
    std::string lastPacket = "none";
    std::string status = "Ready.";
    std::vector<std::string> events;
    fs::path configPath;
};

static std::mutex g_stateMutex;
static AppState g_state;

static std::string trim(const std::string& value)
{
    const auto start = value.find_first_not_of(" \t\r\n");
    if (start == std::string::npos)
        return "";
    const auto end = value.find_last_not_of(" \t\r\n");
    return value.substr(start, end - start + 1);
}

static std::string lower(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(),
                   [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return value;
}

static bool equalsIgnoreCase(const std::string& a, const std::string& b)
{
    return lower(a) == lower(b);
}

static std::string wsaErrorText(const char* what)
{
    return std::string(what) + " failed, WSA error " + std::to_string(WSAGetLastError());
}

static bool parseEndpoint(const std::string& rawValue, Endpoint& out)
{
    std::string value = trim(rawValue);
    if (value.find(':') == std::string::npos)
        value = "127.0.0.1:" + value;

    const auto colon = value.rfind(':');
    if (colon == std::string::npos)
    {
        std::cerr << "Invalid endpoint, expected ip:port or port: " << rawValue << "\n";
        return false;
    }

    const std::string ip = value.substr(0, colon);
    const std::string portText = value.substr(colon + 1);

    char* end = nullptr;
    long port = std::strtol(portText.c_str(), &end, 10);
    if (end == portText.c_str() || *end != '\0' || port < 1 || port > 65535)
    {
        std::cerr << "Invalid port in endpoint: " << rawValue << "\n";
        return false;
    }

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(static_cast<unsigned short>(port));

    if (InetPtonA(AF_INET, ip.c_str(), &addr.sin_addr) != 1)
    {
        std::cerr << "Invalid IPv4 address in endpoint: " << rawValue << "\n";
        return false;
    }

    out.text = ip + ":" + std::to_string(port);
    out.addr = addr;
    return true;
}

static std::string endpointToString(const sockaddr_in& ep)
{
    char ip[INET_ADDRSTRLEN]{};
    InetNtopA(AF_INET, const_cast<IN_ADDR*>(&ep.sin_addr), ip, sizeof(ip));
    return std::string(ip) + ":" + std::to_string(ntohs(ep.sin_port));
}

static bool sameEndpoint(const sockaddr_in& a, const sockaddr_in& b)
{
    return a.sin_family == b.sin_family &&
           a.sin_port == b.sin_port &&
           a.sin_addr.s_addr == b.sin_addr.s_addr;
}

static void addEventLocked(const std::string& eventText)
{
    g_state.events.push_back(eventText);
    if (g_state.events.size() > 10)
        g_state.events.erase(g_state.events.begin());
}

static uint32_t readBE32(const char* p)
{
    const auto b0 = static_cast<unsigned char>(p[0]);
    const auto b1 = static_cast<unsigned char>(p[1]);
    const auto b2 = static_cast<unsigned char>(p[2]);
    const auto b3 = static_cast<unsigned char>(p[3]);
    return (static_cast<uint32_t>(b0) << 24) |
           (static_cast<uint32_t>(b1) << 16) |
           (static_cast<uint32_t>(b2) << 8) |
           static_cast<uint32_t>(b3);
}

enum class PacketKind
{
    Unknown,
    WsjtxToApps,
    AppToWsjtx,
    Ambiguous
};

static std::string packetKindText(PacketKind kind)
{
    switch (kind)
    {
    case PacketKind::WsjtxToApps:
        return "WSJT-X -> apps";
    case PacketKind::AppToWsjtx:
        return "apps -> WSJT-X";
    case PacketKind::Ambiguous:
        return "ambiguous";
    default:
        return "unknown";
    }
}

static PacketKind classifyWsjtPacket(const char* data, int length, uint32_t& schema, uint32_t& type)
{
    schema = 0;
    type = 0;

    if (length < 12)
        return PacketKind::Unknown;

    constexpr uint32_t WSJTX_MAGIC = 0xadbccbda;
    const uint32_t magic = readBE32(data);
    if (magic != WSJTX_MAGIC)
        return PacketKind::Unknown;

    schema = readBE32(data + 4);
    type = readBE32(data + 8);

    switch (type)
    {
    case 1:  // Status
    case 2:  // Decode
    case 5:  // QSOLogged
    case 10: // WSPRDecode
    case 12: // LoggedADIF
        return PacketKind::WsjtxToApps;

    case 4:  // Reply
    case 7:  // Replay
    case 8:  // HaltTx
    case 9:  // FreeText
    case 11: // Location
    case 13: // HighlightCallsign
    case 14: // SwitchConfiguration
    case 15: // Configure
        return PacketKind::AppToWsjtx;

    case 0: // Heartbeat
    case 3: // Clear
    case 6: // Close
        return PacketKind::Ambiguous;

    default:
        return PacketKind::Unknown;
    }
}

static fs::path defaultConfigPath()
{
    const char* appData = std::getenv("APPDATA");
    fs::path base = appData && *appData ? fs::path(appData) : fs::current_path();
    return base / "WsjtxUdpFanout" / "WsjtxUdpFanout.ini";
}

static std::string targetConfigLine(const Target& target)
{
    return "target=" + target.name + "|" + target.endpoint.text;
}

static bool saveConfig(const fs::path& path, std::string& message)
{
    std::vector<Target> targets;
    Endpoint listen;
    bool bidirectional;
    int dashboardMs;

    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        targets = g_state.targets;
        listen = g_state.listen;
        bidirectional = g_state.bidirectional;
        dashboardMs = g_state.dashboardMs;
    }

    try
    {
        fs::create_directories(path.parent_path());
        std::ofstream file(path);
        if (!file)
        {
            message = "Could not open config for writing: " + path.string();
            return false;
        }

        file << "# WSJT-X UDP Fanout configuration\n";
        file << "listen=" << listen.text << "\n";
        file << "bidirectional=" << (bidirectional ? "true" : "false") << "\n";
        file << "dashboard_ms=" << dashboardMs << "\n";
        for (const auto& target : targets)
            file << targetConfigLine(target) << "\n";

        message = "Saved config: " + path.string();
        return true;
    }
    catch (const std::exception& ex)
    {
        message = std::string("Could not save config: ") + ex.what();
        return false;
    }
}

static void saveConfigAndReport()
{
    std::string message;
    const bool ok = saveConfig(g_state.configPath, message);
    std::lock_guard<std::mutex> lock(g_stateMutex);
    g_state.status = message;
    addEventLocked(ok ? message : ("Save failed: " + message));
}

static void addDefaultTargets()
{
    const std::vector<std::pair<std::string, std::string>> defaults = {
        {"GridTracker", "127.0.0.1:2237"},
        {"Hamilton Auto FT8", "127.0.0.1:2238"},
        {"WRL CAT Control", "127.0.0.1:2239"},
    };

    for (const auto& item : defaults)
    {
        Endpoint ep{};
        if (parseEndpoint(item.second, ep))
            g_state.targets.push_back(Target{item.first, ep});
    }
}

static bool loadConfig(const fs::path& path, bool keepDefaults)
{
    std::ifstream file(path);
    if (!file)
        return false;

    std::vector<Target> loadedTargets;
    Endpoint listen = g_state.listen;
    bool bidirectional = g_state.bidirectional;
    int dashboardMs = g_state.dashboardMs;

    std::string line;
    while (std::getline(file, line))
    {
        line = trim(line);
        if (line.empty() || line[0] == '#')
            continue;

        const auto equals = line.find('=');
        if (equals == std::string::npos)
            continue;

        const std::string key = lower(trim(line.substr(0, equals)));
        const std::string value = trim(line.substr(equals + 1));

        if (key == "listen")
        {
            Endpoint ep{};
            if (parseEndpoint(value, ep))
                listen = ep;
        }
        else if (key == "bidirectional")
        {
            const std::string v = lower(value);
            bidirectional = (v == "true" || v == "yes" || v == "1" || v == "on");
        }
        else if (key == "dashboard_ms")
        {
            const int ms = std::atoi(value.c_str());
            if (ms >= 250 && ms <= 10000)
                dashboardMs = ms;
        }
        else if (key == "target")
        {
            const auto sep = value.rfind('|');
            if (sep != std::string::npos)
            {
                Endpoint ep{};
                const std::string name = trim(value.substr(0, sep));
                const std::string endpointText = trim(value.substr(sep + 1));
                if (!name.empty() && parseEndpoint(endpointText, ep))
                    loadedTargets.push_back(Target{name, ep});
            }
        }
    }

    if (!keepDefaults || !loadedTargets.empty())
        g_state.targets = loadedTargets;

    g_state.listen = listen;
    g_state.bidirectional = bidirectional;
    g_state.dashboardMs = dashboardMs;
    return true;
}

static std::vector<std::string> splitCommandLine(const std::string& line)
{
    std::vector<std::string> parts;
    std::string current;
    bool inQuotes = false;

    for (char ch : line)
    {
        if (ch == '"')
        {
            inQuotes = !inQuotes;
            continue;
        }

        if (std::isspace(static_cast<unsigned char>(ch)) && !inQuotes)
        {
            if (!current.empty())
            {
                parts.push_back(current);
                current.clear();
            }
            continue;
        }

        current.push_back(ch);
    }

    if (!current.empty())
        parts.push_back(current);

    return parts;
}

static int findTargetIndexLocked(const std::string& name)
{
    for (size_t i = 0; i < g_state.targets.size(); ++i)
    {
        if (equalsIgnoreCase(g_state.targets[i].name, name))
            return static_cast<int>(i);
    }
    return -1;
}

static void handleCommand(const std::string& rawLine)
{
    const auto args = splitCommandLine(trim(rawLine));
    if (args.empty())
        return;

    const std::string cmd = lower(args[0]);
    bool changedConfig = false;
    std::string message;

    if (cmd == "help" || cmd == "?")
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_state.showHelp = true;
        g_state.status = "Help shown. Type hidehelp to close it.";
        addEventLocked(g_state.status);
        return;
    }
    else if (cmd == "hidehelp")
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_state.showHelp = false;
        g_state.status = "Help hidden.";
        addEventLocked(g_state.status);
        return;
    }
    else if (cmd == "quit" || cmd == "exit")
    {
        g_running = false;
        return;
    }
    else if (cmd == "config")
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_state.status = "Configuration is shown on the dashboard.";
        addEventLocked(g_state.status);
        return;
    }
    else if (cmd == "save")
    {
        saveConfigAndReport();
        return;
    }
    else if (cmd == "load")
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        if (loadConfig(g_state.configPath, false))
            message = "Loaded config: " + g_state.configPath.string();
        else
            message = "Could not load config: " + g_state.configPath.string();
        g_state.status = message;
        addEventLocked(message);
        return;
    }
    else if (cmd == "add")
    {
        if (args.size() < 3)
        {
            message = "Usage: add <name> <port|ip:port>";
        }
        else
        {
            Endpoint ep{};
            if (parseEndpoint(args[2], ep))
            {
                std::lock_guard<std::mutex> lock(g_stateMutex);
                if (findTargetIndexLocked(args[1]) >= 0)
                {
                    message = "Target already exists: " + args[1];
                }
                else
                {
                    g_state.targets.push_back(Target{args[1], ep});
                    message = "Added " + args[1] + " -> " + ep.text;
                    changedConfig = true;
                }
            }
            else
            {
                message = "Invalid endpoint.";
            }
        }
    }
    else if (cmd == "set")
    {
        if (args.size() < 3)
        {
            message = "Usage: set <name> <port|ip:port>";
        }
        else
        {
            Endpoint ep{};
            if (parseEndpoint(args[2], ep))
            {
                std::lock_guard<std::mutex> lock(g_stateMutex);
                const int index = findTargetIndexLocked(args[1]);
                if (index < 0)
                {
                    message = "No target named: " + args[1];
                }
                else
                {
                    g_state.targets[static_cast<size_t>(index)].endpoint = ep;
                    message = "Updated " + args[1] + " -> " + ep.text;
                    changedConfig = true;
                }
            }
            else
            {
                message = "Invalid endpoint.";
            }
        }
    }
    else if (cmd == "remove" || cmd == "delete")
    {
        if (args.size() < 2)
        {
            message = "Usage: remove <name>";
        }
        else
        {
            std::lock_guard<std::mutex> lock(g_stateMutex);
            const int index = findTargetIndexLocked(args[1]);
            if (index < 0)
            {
                message = "No target named: " + args[1];
            }
            else
            {
                const std::string name = g_state.targets[static_cast<size_t>(index)].name;
                g_state.targets.erase(g_state.targets.begin() + index);
                message = "Removed " + name;
                changedConfig = true;
            }
        }
    }
    else if (cmd == "rename")
    {
        if (args.size() < 3)
        {
            message = "Usage: rename <old-name> <new-name>";
        }
        else
        {
            std::lock_guard<std::mutex> lock(g_stateMutex);
            const int index = findTargetIndexLocked(args[1]);
            if (index < 0)
            {
                message = "No target named: " + args[1];
            }
            else if (findTargetIndexLocked(args[2]) >= 0)
            {
                message = "Target already exists: " + args[2];
            }
            else
            {
                g_state.targets[static_cast<size_t>(index)].name = args[2];
                message = "Renamed " + args[1] + " to " + args[2];
                changedConfig = true;
            }
        }
    }
    else if (cmd == "bidirectional")
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_state.bidirectional = true;
        message = "Bidirectional relay enabled.";
        changedConfig = true;
    }
    else if (cmd == "read-only")
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_state.bidirectional = false;
        message = "Read-only mode enabled.";
        changedConfig = true;
    }
    else if (cmd == "refresh")
    {
        if (args.size() < 2)
        {
            message = "Usage: refresh <milliseconds>";
        }
        else
        {
            const int ms = std::atoi(args[1].c_str());
            if (ms < 250 || ms > 10000)
            {
                message = "Refresh must be between 250 and 10000 milliseconds.";
            }
            else
            {
                std::lock_guard<std::mutex> lock(g_stateMutex);
                g_state.dashboardMs = ms;
                message = "Dashboard refresh set to " + std::to_string(ms) + " ms.";
                changedConfig = true;
            }
        }
    }
    else if (cmd == "clearstats")
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_state.counters = Counters{};
        for (auto& target : g_state.targets)
        {
            target.packets = 0;
            target.bytes = 0;
            target.sendErrors = 0;
        }
        g_state.lastPacket = "none";
        message = "Counters cleared.";
    }
    else
    {
        message = "Unknown command: " + args[0] + " (type help)";
    }

    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_state.status = message;
        if (!message.empty())
            addEventLocked(message);
    }

    if (changedConfig)
        saveConfigAndReport();
}

static bool sendPacket(SOCKET sock, const sockaddr_in& destination, const char* data, int len)
{
    const int sent = sendto(sock, data, len, 0,
                            reinterpret_cast<const sockaddr*>(&destination),
                            sizeof(destination));
    return sent != SOCKET_ERROR;
}

static void forwardToTargets(SOCKET sock, const char* data, int len)
{
    std::vector<std::pair<size_t, sockaddr_in>> destinations;
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        for (size_t i = 0; i < g_state.targets.size(); ++i)
            destinations.push_back({i, g_state.targets[i].endpoint.addr});
    }

    for (const auto& destination : destinations)
    {
        const bool ok = sendPacket(sock, destination.second, data, len);
        std::lock_guard<std::mutex> lock(g_stateMutex);
        if (destination.first < g_state.targets.size())
        {
            if (ok)
            {
                g_state.targets[destination.first].packets++;
                g_state.targets[destination.first].bytes += static_cast<uint64_t>(len);
            }
            else
            {
                g_state.targets[destination.first].sendErrors++;
                g_state.counters.sendErrors++;
            }
        }
    }
}

static void udpWorker()
{
    WSADATA wsa{};
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0)
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_state.status = "WSAStartup failed.";
        g_running = false;
        return;
    }

    SOCKET sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock == INVALID_SOCKET)
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_state.status = wsaErrorText("socket");
        WSACleanup();
        g_running = false;
        return;
    }

    DWORD recvTimeoutMs = 200;
    setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO,
               reinterpret_cast<const char*>(&recvTimeoutMs),
               sizeof(recvTimeoutMs));

    Endpoint listen;
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        listen = g_state.listen;
    }

    if (bind(sock, reinterpret_cast<const sockaddr*>(&listen.addr), sizeof(listen.addr)) == SOCKET_ERROR)
    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_state.status = wsaErrorText("bind") + ". Another program may already be listening on " + listen.text + ".";
        closesocket(sock);
        WSACleanup();
        g_running = false;
        return;
    }

    {
        std::lock_guard<std::mutex> lock(g_stateMutex);
        g_state.status = "Listening on " + listen.text;
        addEventLocked(g_state.status);
    }

    char buffer[65535];

    while (g_running)
    {
        sockaddr_in from{};
        int fromLen = sizeof(from);
        const int received = recvfrom(sock, buffer, sizeof(buffer), 0,
                                      reinterpret_cast<sockaddr*>(&from),
                                      &fromLen);

        if (received == SOCKET_ERROR)
        {
            const int err = WSAGetLastError();
            if (err == WSAETIMEDOUT)
                continue;

            std::lock_guard<std::mutex> lock(g_stateMutex);
            g_state.status = "recvfrom failed, WSA error " + std::to_string(err);
            addEventLocked(g_state.status);
            continue;
        }

        uint32_t schema = 0;
        uint32_t type = 0;
        PacketKind kind = classifyWsjtPacket(buffer, received, schema, type);
        const std::string fromText = endpointToString(from);

        bool bidirectional = true;
        bool haveWsjtx = false;
        sockaddr_in learned{};
        {
            std::lock_guard<std::mutex> lock(g_stateMutex);
            bidirectional = g_state.bidirectional;
            haveWsjtx = g_state.haveWsjtxSource;
            learned = g_state.learnedWsjtxSource;
        }

        auto markWsjtxToApps = [&]() {
            {
                std::lock_guard<std::mutex> lock(g_stateMutex);
                g_state.learnedWsjtxSource = from;
                g_state.haveWsjtxSource = true;
                g_state.counters.wsjtxToAppsPackets++;
                g_state.counters.wsjtxToAppsBytes += static_cast<uint64_t>(received);
                g_state.lastPacket = packetKindText(kind) + " type=" + std::to_string(type) +
                                     " schema=" + std::to_string(schema) + " from=" + fromText +
                                     " bytes=" + std::to_string(received);
                addEventLocked(g_state.lastPacket);
            }
            forwardToTargets(sock, buffer, received);
        };

        auto markAppToWsjtx = [&]() {
            sockaddr_in destination{};
            bool canSend = false;
            {
                std::lock_guard<std::mutex> lock(g_stateMutex);
                if (g_state.haveWsjtxSource)
                {
                    destination = g_state.learnedWsjtxSource;
                    canSend = true;
                }
            }

            std::lock_guard<std::mutex> lock(g_stateMutex);
            g_state.counters.appsToWsjtxPackets++;
            g_state.counters.appsToWsjtxBytes += static_cast<uint64_t>(received);
            g_state.lastPacket = packetKindText(kind) + " type=" + std::to_string(type) +
                                 " schema=" + std::to_string(schema) + " from=" + fromText +
                                 " bytes=" + std::to_string(received);

            if (!canSend)
            {
                g_state.counters.droppedPackets++;
                g_state.status = "Dropped app command; WSJT-X source is not learned yet.";
                addEventLocked(g_state.status);
                return;
            }

            const bool ok = sendPacket(sock, destination, buffer, received);
            if (!ok)
            {
                g_state.counters.sendErrors++;
                g_state.status = "sendto WSJT-X failed, WSA error " + std::to_string(WSAGetLastError());
                addEventLocked(g_state.status);
            }
            else
            {
                addEventLocked(g_state.lastPacket + " -> " + endpointToString(destination));
            }
        };

        if (!bidirectional && kind == PacketKind::AppToWsjtx)
        {
            std::lock_guard<std::mutex> lock(g_stateMutex);
            g_state.counters.droppedPackets++;
            g_state.status = "Dropped app command from " + fromText + " because read-only mode is enabled.";
            addEventLocked(g_state.status);
            continue;
        }

        if (kind == PacketKind::WsjtxToApps)
        {
            markWsjtxToApps();
        }
        else if (kind == PacketKind::AppToWsjtx)
        {
            markAppToWsjtx();
        }
        else if (kind == PacketKind::Ambiguous)
        {
            if (haveWsjtx && sameEndpoint(from, learned))
                markWsjtxToApps();
            else if (bidirectional && haveWsjtx)
                markAppToWsjtx();
            else
                markWsjtxToApps();
        }
        else
        {
            if (haveWsjtx && sameEndpoint(from, learned))
            {
                markWsjtxToApps();
            }
            else if (!haveWsjtx)
            {
                {
                    std::lock_guard<std::mutex> lock(g_stateMutex);
                    g_state.counters.unknownPackets++;
                }
                markWsjtxToApps();
            }
            else
            {
                std::lock_guard<std::mutex> lock(g_stateMutex);
                g_state.counters.unknownPackets++;
                g_state.counters.droppedPackets++;
                g_state.status = "Dropped unknown packet from " + fromText;
                addEventLocked(g_state.status);
            }
        }
    }

    closesocket(sock);
    WSACleanup();
}

static void enableVirtualTerminal()
{
    HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);
    if (out == INVALID_HANDLE_VALUE)
        return;

    DWORD mode = 0;
    if (!GetConsoleMode(out, &mode))
        return;

    SetConsoleMode(out, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
}

static void dashboardWorker()
{
    enableVirtualTerminal();
    std::string inputBuffer;
    auto nextRefresh = std::chrono::steady_clock::now();

    while (g_running)
    {
        while (_kbhit())
        {
            const int ch = _getch();
            if (ch == 0 || ch == 224)
            {
                if (_kbhit())
                    (void)_getch();
                continue;
            }

            if (ch == '\r' || ch == '\n')
            {
                const std::string command = inputBuffer;
                inputBuffer.clear();
                handleCommand(command);
                nextRefresh = std::chrono::steady_clock::now();
            }
            else if (ch == '\b')
            {
                if (!inputBuffer.empty())
                    inputBuffer.pop_back();
            }
            else if (ch == 27)
            {
                inputBuffer.clear();
            }
            else if (std::isprint(ch))
            {
                inputBuffer.push_back(static_cast<char>(ch));
            }
        }

        const auto now = std::chrono::steady_clock::now();
        if (now < nextRefresh)
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(25));
            continue;
        }

        AppState snapshot;
        {
            std::lock_guard<std::mutex> lock(g_stateMutex);
            snapshot = g_state;
        }

        std::cout << "\x1b[2J\x1b[H";
        std::cout << "WSJT-X UDP Fanout\n";
        std::cout << "=================\n\n";
        std::cout << "Listen: " << snapshot.listen.text << "\n";
        std::cout << "Mode: " << (snapshot.bidirectional ? "bidirectional" : "read-only") << "\n";
        std::cout << "Config: " << snapshot.configPath.string() << "\n";
        std::cout << "WSJT-X source: "
                  << (snapshot.haveWsjtxSource ? endpointToString(snapshot.learnedWsjtxSource) : "not learned yet")
                  << "\n";
        std::cout << "Status: " << snapshot.status << "\n\n";

        std::cout << "Traffic\n";
        std::cout << "  WSJT-X -> apps:  " << snapshot.counters.wsjtxToAppsPackets
                  << " packets, " << snapshot.counters.wsjtxToAppsBytes << " bytes\n";
        std::cout << "  apps -> WSJT-X:  " << snapshot.counters.appsToWsjtxPackets
                  << " packets, " << snapshot.counters.appsToWsjtxBytes << " bytes\n";
        std::cout << "  send errors:     " << snapshot.counters.sendErrors << "\n";
        std::cout << "  dropped packets: " << snapshot.counters.droppedPackets << "\n";
        std::cout << "  unknown packets: " << snapshot.counters.unknownPackets << "\n";
        std::cout << "  last packet:     " << snapshot.lastPacket << "\n\n";

        std::cout << "Destinations\n";
        if (snapshot.targets.empty())
        {
            std::cout << "  none\n";
        }
        else
        {
            for (const auto& target : snapshot.targets)
            {
                std::cout << "  " << std::left << std::setw(22) << target.name
                          << " " << std::setw(18) << target.endpoint.text
                          << " packets=" << std::setw(8) << target.packets
                          << " bytes=" << std::setw(10) << target.bytes
                          << " errors=" << target.sendErrors << "\n";
            }
        }

        std::cout << "\nRecent events\n";
        if (snapshot.events.empty())
        {
            std::cout << "  none\n";
        }
        else
        {
            for (const auto& eventText : snapshot.events)
                std::cout << "  " << eventText << "\n";
        }

        if (snapshot.showHelp)
        {
            std::cout
                << "\nCommands\n"
                << "  help | hidehelp | quit\n"
                << "  add JTSync 2249\n"
                << "  add \"Hamilton Auto FT8\" 127.0.0.1:2238\n"
                << "  set JTSync 127.0.0.1:2249\n"
                << "  remove JTSync\n"
                << "  rename JTSync \"JT Sync\"\n"
                << "  save | load | config\n"
                << "  bidirectional | read-only\n"
                << "  refresh 500\n"
                << "  clearstats\n";
        }

        std::cout << "\nType 'help' for commands. Example: add JTSync 2249\n";
        std::cout << "> " << inputBuffer << std::flush;

        const int sleepMs = std::max(250, snapshot.dashboardMs);
        nextRefresh = std::chrono::steady_clock::now() + std::chrono::milliseconds(sleepMs);
    }
}

static void printUsage()
{
    std::cout
        << "WSJT-X UDP Fanout\n\n"
        << "Usage:\n"
        << "  WsjtxUdpFanout.exe [options]\n\n"
        << "Options:\n"
        << "  --listen ip:port       Listen endpoint. Default: 127.0.0.1:2236\n"
        << "  --target name=ip:port  Add destination. Repeat for multiple targets.\n"
        << "  --target name:port     Also accepted when the port is local-only.\n"
        << "  --bidirectional        Enable app command relay back to WSJT-X. Default.\n"
        << "  --read-only            Disable app command relay.\n"
        << "  --dashboard-ms n       Dashboard refresh, 250-10000 ms.\n"
        << "  --config path          Use a custom config file.\n"
        << "  --help                 Show this help.\n";
}

static bool parseTargetArgument(const std::string& value, Target& target)
{
    auto sep = value.find('=');
    if (sep == std::string::npos)
        sep = value.find('|');
    if (sep == std::string::npos)
        sep = value.rfind(':');

    if (sep == std::string::npos)
    {
        std::cerr << "Invalid target. Expected name=ip:port, name|ip:port, or name:port: " << value << "\n";
        return false;
    }

    const std::string name = trim(value.substr(0, sep));
    const std::string endpointText = trim(value.substr(sep + 1));
    if (name.empty())
    {
        std::cerr << "Target name cannot be empty.\n";
        return false;
    }

    Endpoint ep{};
    if (!parseEndpoint(endpointText, ep))
        return false;

    target = Target{name, ep};
    return true;
}

int main(int argc, char* argv[])
{
    SetConsoleCtrlHandler(consoleHandler, TRUE);

    if (!parseEndpoint("127.0.0.1:2236", g_state.listen))
        return 1;

    g_state.configPath = defaultConfigPath();
    addDefaultTargets();

    bool userProvidedTargets = false;
    bool loadedConfig = loadConfig(g_state.configPath, false);
    if (!loadedConfig)
        saveConfigAndReport();

    for (int i = 1; i < argc; ++i)
    {
        std::string arg = argv[i];

        if (arg == "--help" || arg == "-h")
        {
            printUsage();
            return 0;
        }
        else if (arg == "--listen")
        {
            if (i + 1 >= argc || !parseEndpoint(argv[++i], g_state.listen))
                return 1;
        }
        else if (arg == "--target")
        {
            if (i + 1 >= argc)
            {
                std::cerr << "--target requires name=ip:port\n";
                return 1;
            }
            if (!userProvidedTargets)
            {
                g_state.targets.clear();
                userProvidedTargets = true;
            }
            Target target{};
            if (!parseTargetArgument(argv[++i], target))
                return 1;
            g_state.targets.push_back(target);
        }
        else if (arg == "--bidirectional")
        {
            g_state.bidirectional = true;
        }
        else if (arg == "--read-only")
        {
            g_state.bidirectional = false;
        }
        else if (arg == "--dashboard-ms")
        {
            if (i + 1 >= argc)
            {
                std::cerr << "--dashboard-ms requires a value\n";
                return 1;
            }
            const int ms = std::atoi(argv[++i]);
            if (ms < 250 || ms > 10000)
            {
                std::cerr << "--dashboard-ms must be between 250 and 10000\n";
                return 1;
            }
            g_state.dashboardMs = ms;
        }
        else if (arg == "--config")
        {
            if (i + 1 >= argc)
            {
                std::cerr << "--config requires a path\n";
                return 1;
            }
            g_state.configPath = argv[++i];
            loadConfig(g_state.configPath, false);
        }
        else
        {
            std::cerr << "Unknown argument: " << arg << "\n\n";
            printUsage();
            return 1;
        }
    }

    if (g_state.targets.empty())
        addDefaultTargets();

    std::thread udp(udpWorker);
    std::thread dashboard(dashboardWorker);

    udp.join();
    g_running = false;

    if (dashboard.joinable())
        dashboard.join();

    std::cout << "\nStopping.\n";
    return 0;
}
