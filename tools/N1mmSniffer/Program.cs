using System.Net;
using System.Net.Sockets;
using System.Text;

namespace N1mmSniffer;

/// <summary>
/// N1MM+ UDP Packet Sniffer — Raw Socket Mode (requires elevation)
/// 
/// Uses a raw IP socket to capture ALL UDP packets on the machine,
/// then filters for known N1MM+ ports. This captures packets even on
/// ports that N1MM holds exclusively (like 2237 and 12070).
/// 
/// MUST BE RUN AS ADMINISTRATOR.
/// 
/// Known N1MM+ UDP ports:
///   2237  - N1MM+ inter-station networking / discovery
///   2238  - N1MM+ inter-station networking
///   12060 - Contact/QSO data (XML)
///   12061 - Score data (XML)  
///   12062 - Radio info (XML)
///   12063 - Lookup/callsign data (XML)
///   12064 - Packet spot data
///   12065 - Rotor control
///   12066 - Focus
///   12067 - Function key
///   12068 - Dynamic scoring results
///   12069 - Send CW/messages
///   12070 - External broadcast (general)
///   12071 - Bandmap data
///   13063 - Alternate configuration
/// </summary>
class Program
{
    static readonly HashSet<int> TargetPorts = new()
    {
        2237, 2238,
        12060, 12061, 12062, 12063, 12064, 12065,
        12066, 12067, 12068, 12069, 12070, 12071,
        13063
    };

    static readonly Dictionary<int, string> PortNames = new()
    {
        { 2237, "Discovery/InterStation" },
        { 2238, "InterStation" },
        { 12060, "Contact/QSO" },
        { 12061, "Score" },
        { 12062, "RadioInfo" },
        { 12063, "Lookup" },
        { 12064, "PacketSpot" },
        { 12065, "Rotor" },
        { 12066, "Focus" },
        { 12067, "FunctionKey" },
        { 12068, "DynamicResults" },
        { 12069, "SendCW" },
        { 12070, "ExternalBroadcast" },
        { 12071, "Bandmap" },
        { 13063, "Alternate" }
    };

    static string _logFile = "";
    static readonly object _logLock = new();
    static long _packetCount = 0;

    static async Task Main(string[] args)
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _logFile = Path.Combine(AppContext.BaseDirectory, $"n1mm-capture-{timestamp}.txt");

        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine("  N1MM+ UDP Packet Sniffer (RAW SOCKET MODE)");
        Console.WriteLine("  Captures ALL UDP traffic on N1MM+ ports");
        Console.WriteLine("  ** REQUIRES ADMINISTRATOR ELEVATION **");
        Console.WriteLine($"  Log file: {_logFile}");
        Console.WriteLine("  Press Ctrl+C to stop");
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine();
        Console.WriteLine($"  Monitoring ports: {string.Join(", ", TargetPorts.Order())}");
        Console.WriteLine();

        Log($"N1MM+ UDP Packet Sniffer (RAW) started at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log($"Monitoring ports: {string.Join(", ", TargetPorts.Order())}");
        Log("");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("\nStopping...");
        };

        // Get all local IPv4 addresses and listen on each
        var localAddresses = GetLocalIPv4Addresses();
        Console.WriteLine($"  Local interfaces: {string.Join(", ", localAddresses)}");
        Console.WriteLine();

        var tasks = new List<Task>();
        foreach (var addr in localAddresses)
        {
            tasks.Add(CaptureOnInterface(addr, cts.Token));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }

        Log("");
        Log($"Stopped. Total packets captured: {_packetCount}");
        Console.WriteLine($"\nTotal packets captured: {_packetCount}");
        Console.WriteLine($"Log saved to: {_logFile}");
    }

    static async Task CaptureOnInterface(IPAddress localAddr, CancellationToken ct)
    {
        Socket? rawSocket = null;
        try
        {
            // Create raw IP socket
            rawSocket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Udp);
            rawSocket.Bind(new IPEndPoint(localAddr, 0));

            // Set SIO_RCVALL to capture all incoming packets on this interface
            byte[] optIn = BitConverter.GetBytes(1); // RCVALL_ON
            rawSocket.IOControl(unchecked((int)0x98000001), optIn, null); // SIO_RCVALL

            Console.WriteLine($"  ✓ Raw capture on {localAddr}");
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"  ✗ Failed on {localAddr}: {ex.Message}");
            if (ex.SocketErrorCode == SocketError.AccessDenied)
                Console.WriteLine("    → Run as Administrator!");
            rawSocket?.Dispose();
            return;
        }

        byte[] buffer = new byte[65535];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int received;
                try
                {
                    // Use Task.Run to make the blocking Receive cancellable
                    received = await Task.Run(() =>
                    {
                        rawSocket.ReceiveTimeout = 1000;
                        try { return rawSocket.Receive(buffer); }
                        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { return 0; }
                    }, ct);
                }
                catch (OperationCanceledException) { break; }

                if (received == 0) continue;
                if (received < 28) continue; // Too small for IP + UDP header + any payload

                // Parse IP header to find UDP payload
                // IP header: first nibble of first byte = version (4), second nibble = IHL (header length in 32-bit words)
                int ipHeaderLen = (buffer[0] & 0x0F) * 4;
                if (ipHeaderLen < 20 || ipHeaderLen >= received) continue;

                // Check protocol field = 17 (UDP)
                if (buffer[9] != 17) continue;

                // Source and destination IP
                var srcIp = new IPAddress(new ReadOnlySpan<byte>(buffer, 12, 4));
                var dstIp = new IPAddress(new ReadOnlySpan<byte>(buffer, 16, 4));

                // UDP header starts after IP header
                int udpOffset = ipHeaderLen;
                if (udpOffset + 8 > received) continue;

                int srcPort = (buffer[udpOffset] << 8) | buffer[udpOffset + 1];
                int dstPort = (buffer[udpOffset + 2] << 8) | buffer[udpOffset + 3];
                int udpLen = (buffer[udpOffset + 4] << 8) | buffer[udpOffset + 5];

                // Filter: only process packets TO or FROM our target ports
                if (!TargetPorts.Contains(srcPort) && !TargetPorts.Contains(dstPort))
                    continue;

                // Extract UDP payload
                int payloadOffset = udpOffset + 8;
                int payloadLen = Math.Min(udpLen - 8, received - payloadOffset);
                if (payloadLen <= 0) continue;

                byte[] payload = new byte[payloadLen];
                Array.Copy(buffer, payloadOffset, payload, 0, payloadLen);

                int relevantPort = TargetPorts.Contains(dstPort) ? dstPort : srcPort;
                string portName = PortNames.GetValueOrDefault(relevantPort, "Unknown");
                string direction = TargetPorts.Contains(dstPort) ? "→" : "←";

                Interlocked.Increment(ref _packetCount);
                ProcessPacket(relevantPort, portName, srcIp, srcPort, dstIp, dstPort, direction, payload);
            }
        }
        finally
        {
            rawSocket.Dispose();
        }
    }

    static void ProcessPacket(int port, string portName, IPAddress srcIp, int srcPort,
        IPAddress dstIp, int dstPort, string direction, byte[] data)
    {
        string time = DateTime.Now.ToString("HH:mm:ss.fff");
        string header = $"[{time}] {direction} Port {port} ({portName}) | {srcIp}:{srcPort} -> {dstIp}:{dstPort} | {data.Length} bytes";

        bool isText = IsPlainText(data);

        var sb = new StringBuilder();
        sb.AppendLine("────────────────────────────────────────────────────────────");
        sb.AppendLine(header);
        sb.AppendLine();

        if (isText)
        {
            string text = Encoding.UTF8.GetString(data).TrimEnd('\0', '\r', '\n');
            sb.AppendLine("  [TEXT/XML]");
            foreach (string line in text.Split('\n'))
            {
                sb.AppendLine($"  {line.TrimEnd('\r')}");
            }
        }
        else
        {
            sb.AppendLine("  [BINARY]");
            sb.AppendLine($"  Hex ({data.Length} bytes):");
            sb.AppendLine(FormatHexDump(data, indent: 4));

            string ascii = ExtractAscii(data);
            if (ascii.Length > 4)
            {
                sb.AppendLine($"  ASCII fragments: {ascii}");
            }
        }

        sb.AppendLine();
        string output = sb.ToString();

        // Console (abbreviated)
        if (isText)
        {
            string text = Encoding.UTF8.GetString(data).TrimEnd('\0', '\r', '\n');
            string preview = text.Length > 150 ? text[..150] + "..." : text;
            Console.WriteLine($"{header}");
            Console.WriteLine($"  {preview}");
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine($"{header}");
            Console.WriteLine($"  [BINARY] {BitConverter.ToString(data[..Math.Min(40, data.Length)])}");
            string ascii = ExtractAscii(data);
            if (ascii.Length > 4)
                Console.WriteLine($"  ASCII: {ascii[..Math.Min(80, ascii.Length)]}");
            Console.WriteLine();
        }

        Log(output);
    }

    static bool IsPlainText(byte[] data)
    {
        if (data.Length == 0) return false;
        int textChars = 0;
        int totalChecked = Math.Min(data.Length, 100);
        for (int i = 0; i < totalChecked; i++)
        {
            byte b = data[i];
            if (b == 0) return false;
            if ((b >= 0x20 && b <= 0x7E) || b == 0x09 || b == 0x0A || b == 0x0D) textChars++;
        }
        return textChars > totalChecked * 0.85;
    }

    static string FormatHexDump(byte[] data, int indent)
    {
        var sb = new StringBuilder();
        string pad = new(' ', indent);
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            sb.Append(pad);
            sb.Append($"{offset:X4}: ");
            for (int i = 0; i < 16; i++)
            {
                if (offset + i < data.Length)
                    sb.Append($"{data[offset + i]:X2} ");
                else
                    sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(" |");
            for (int i = 0; i < 16 && offset + i < data.Length; i++)
            {
                byte b = data[offset + i];
                sb.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
            }
            sb.AppendLine("|");
        }
        return sb.ToString();
    }

    static string ExtractAscii(byte[] data)
    {
        var sb = new StringBuilder();
        foreach (byte b in data)
        {
            if (b >= 0x20 && b <= 0x7E)
                sb.Append((char)b);
            else if (sb.Length > 0 && sb[^1] != ' ')
                sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    static List<IPAddress> GetLocalIPv4Addresses()
    {
        var addresses = new List<IPAddress>();
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                continue;
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(addr.Address))
                {
                    addresses.Add(addr.Address);
                }
            }
        }
        // Also add loopback since N1MM often sends to 127.0.0.1
        addresses.Add(IPAddress.Loopback);
        return addresses;
    }

    static void Log(string message)
    {
        lock (_logLock)
        {
            try { File.AppendAllText(_logFile, message + "\r\n"); }
            catch { }
        }
    }
}
