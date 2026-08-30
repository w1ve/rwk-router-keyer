/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
/*
 * FakeFlex — VITA-49 Discovery Packet Emulator
 *
 * Broadcasts FlexRadio-compatible VITA-49 discovery packets on UDP 4992
 * at 1-second intervals. SmartSDR should detect the fake radio and show
 * it in the discovery list.
 *
 * Usage: FakeFlex [--ip <local-ip>] [--serial <serial>] [--model <model>]
 *
 * Defaults:
 *   IP:     first non-loopback IPv4 address on this machine
 *   Serial: 0000-0000-0000-FAKE
 *   Model:  FLEX-6600
 */

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

string ip = GetLocalIp();
string serial = "0000-0000-0000-FAKE";
string model = "FLEX-6600";
int port = 4992;
string nickname = "FakeFlex";
string callsign = "W1VE";
string version = "3.8.2.35826";
string discoveryVersion = "4.0.0.1";

// Parse command-line args
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--ip" when i + 1 < args.Length:
            ip = args[++i];
            break;
        case "--serial" when i + 1 < args.Length:
            serial = args[++i];
            break;
        case "--model" when i + 1 < args.Length:
            model = args[++i];
            break;
        case "--port" when i + 1 < args.Length:
            port = int.Parse(args[++i]);
            break;
        case "--nickname" when i + 1 < args.Length:
            nickname = args[++i];
            break;
        case "--callsign" when i + 1 < args.Length:
            callsign = args[++i];
            break;
    }
}

Console.WriteLine("╔══════════════════════════════════════════════════╗");
Console.WriteLine("║           FakeFlex Discovery Emulator            ║");
Console.WriteLine("╚══════════════════════════════════════════════════╝");
Console.WriteLine();
Console.WriteLine($"  Model:    {model}");
Console.WriteLine($"  Serial:   {serial}");
Console.WriteLine($"  IP:       {ip}");
Console.WriteLine($"  Port:     {port}");
Console.WriteLine($"  Nickname: {nickname}");
Console.WriteLine($"  Callsign: {callsign}");
Console.WriteLine($"  Version:  {version}");
Console.WriteLine();
Console.WriteLine("Broadcasting VITA-49 discovery on 255.255.255.255:4992 every 1s...");
Console.WriteLine("Press Ctrl+C to stop.");
Console.WriteLine();

// Self-test: verify our packet is parseable
byte[] testPacket = BuildDiscoveryPacket(ip, port, serial, model, nickname, callsign, version, discoveryVersion);
if (VerifyPacket(testPacket, ip, port, serial))
    Console.WriteLine($"  Self-test PASSED ({testPacket.Length} bytes, parses correctly)");
else
    Console.WriteLine("  Self-test FAILED — packet may not be recognized by SmartSDR!");
Console.WriteLine();

using var udp = new UdpClient();
udp.EnableBroadcast = true;

// Get subnet broadcast addresses for all active interfaces
var broadcastAddresses = GetBroadcastAddresses();
Console.WriteLine($"  Broadcast targets: {string.Join(", ", broadcastAddresses)}");
Console.WriteLine();

int seq = 0;
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    while (!cts.IsCancellationRequested)
    {
        byte[] packet = BuildDiscoveryPacket(ip, port, serial, model, nickname, callsign, version, discoveryVersion);
        // Send to each broadcast address on both ports 4992 and 4991
        foreach (var bcast in broadcastAddresses)
        {
            udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Parse(bcast), 4992));
            udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Parse(bcast), 4991));
        }
        seq++;
        Console.Write($"\r  Sent packet #{seq} ({packet.Length} bytes) to {broadcastAddresses.Count} interfaces   ");
        await Task.Delay(1000, cts.Token);
    }
}
catch (OperationCanceledException) { }

Console.WriteLine();
Console.WriteLine("Stopped.");

// ─── Packet Builder ─────────────────────────────────────────────────────────────

static byte[] BuildDiscoveryPacket(
    string ip, int port, string serial, string model,
    string nickname, string callsign, string version, string discoveryVersion)
{
    // Build the ASCII key=value payload (space-separated, matches SmartUnlink's format)
    string payload = string.Join(" ",
        $"discovery_protocol_version=3.1.0.2",
        $"model={model}",
        $"serial={serial}",
        $"version={version}",
        $"nickname={nickname}",
        $"callsign={callsign}",
        $"ip={ip}",
        $"port={port}",
        $"status=Available",
        $"inuse_ip=",
        $"inuse_host=",
        $"max_licensed_version=v3",
        $"radio_license_id=00-00-00-00-00-00",
        $"fpc_mac=",
        $"wan_connected=0",
        $"licensed_clients=4",
        $"available_clients=4",
        $"max_panadapters=4",
        $"available_panadapters=4",
        $"max_slices=4",
        $"available_slices=4"
    );

    byte[] payloadBytes = Encoding.ASCII.GetBytes(payload);

    // Pad to 4-byte boundary
    int paddedLen = (payloadBytes.Length + 3) & ~3;
    int totalLen = 28 + paddedLen; // 28-byte VITA-49 preamble + padded payload
    int wordCount = totalLen / 4;

    byte[] packet = new byte[totalLen];

    // ─── VITA-49 Preamble (7 × 32-bit words, big-endian) ───

    // Word 0: Header (matches SmartUnlink's proven format)
    //   Bits 31-28: Packet type 0x3 (Extension Command)
    //   Bit 27: Class ID present (1)
    //   Bits 25-24: Reserved (0)
    //   Bits 23-22: TSI 0x1 (Other timestamp)
    //   Bits 21-20: TSF 0x1 (Sample count timestamp)
    //   Bits 19-16: Packet count (0)
    //   Bits 15-0: Packet size in 32-bit words
    uint header = 0x38500000 | (uint)(wordCount & 0xFFFF);
    WriteUInt32BE(packet, 0, header);

    // Word 1: Stream ID = 0x00000800 (Flex discovery stream)
    WriteUInt32BE(packet, 4, 0x00000800);

    // Word 2: Class ID high = 0x00001C2D (FlexRadio OUI)
    WriteUInt32BE(packet, 8, 0x00001C2D);

    // Word 3: Class ID low = 0x534CFFFF (Discovery class code)
    WriteUInt32BE(packet, 12, 0x534CFFFF);

    // Words 4-6: Timestamp (all zeros — not used for discovery)
    WriteUInt32BE(packet, 16, 0x00000000);
    WriteUInt32BE(packet, 20, 0x00000000);
    WriteUInt32BE(packet, 24, 0x00000000);

    // ─── Payload ───
    Array.Copy(payloadBytes, 0, packet, 28, payloadBytes.Length);
    // Remaining bytes are already zero (padding)

    return packet;
}

static void WriteUInt32BE(byte[] buf, int offset, uint value)
{
    buf[offset] = (byte)(value >> 24);
    buf[offset + 1] = (byte)(value >> 16);
    buf[offset + 2] = (byte)(value >> 8);
    buf[offset + 3] = (byte)(value & 0xFF);
}

static string GetLocalIp()
{
    // Find the first non-loopback IPv4 address
    foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (iface.OperationalStatus != OperationalStatus.Up) continue;
        if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

        foreach (var addr in iface.GetIPProperties().UnicastAddresses)
        {
            if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                return addr.Address.ToString();
        }
    }
    return "192.168.1.100";
}

static List<string> GetBroadcastAddresses()
{
    var result = new List<string>();
    foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (iface.OperationalStatus != OperationalStatus.Up) continue;
        if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

        foreach (var unicast in iface.GetIPProperties().UnicastAddresses)
        {
            if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            if (unicast.IPv4Mask is null) continue;

            byte[] addrBytes = unicast.Address.GetAddressBytes();
            byte[] maskBytes = unicast.IPv4Mask.GetAddressBytes();
            byte[] bcast = new byte[4];
            for (int i = 0; i < 4; i++)
                bcast[i] = (byte)(addrBytes[i] | ~maskBytes[i]);

            result.Add(new IPAddress(bcast).ToString());
        }
    }
    if (result.Count == 0) result.Add("255.255.255.255");
    return result;
}


static bool VerifyPacket(byte[] packet, string expectedIp, int expectedPort, string expectedSerial)
{
    // Verify VITA-49 preamble
    if (packet.Length < 32) return false;

    uint streamId = ReadUInt32BE(packet, 4);
    if (streamId != 0x00000800) { Console.WriteLine($"    BAD stream ID: 0x{streamId:X8}"); return false; }

    uint classHigh = ReadUInt32BE(packet, 8);
    uint classLow = ReadUInt32BE(packet, 12);
    if (classHigh != 0x001C2D53 || classLow != 0x4CFFFF00)
    {
        Console.WriteLine($"    BAD class ID: {classHigh:X8}:{classLow:X8}");
        return false;
    }

    // Parse ASCII payload
    string ascii = Encoding.ASCII.GetString(packet, 28, packet.Length - 28).TrimEnd('\0', ' ');
    var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (string token in ascii.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        int eq = token.IndexOf('=');
        if (eq > 0 && eq < token.Length - 1)
            fields[token[..eq]] = token[(eq + 1)..];
    }

    if (!fields.TryGetValue("ip", out string? ipVal) || ipVal != expectedIp)
    { Console.WriteLine($"    BAD ip: '{ipVal}' expected '{expectedIp}'"); return false; }

    if (!fields.TryGetValue("port", out string? portVal) || portVal != expectedPort.ToString())
    { Console.WriteLine($"    BAD port: '{portVal}' expected '{expectedPort}'"); return false; }

    if (!fields.TryGetValue("serial", out string? serialVal) || serialVal != expectedSerial)
    { Console.WriteLine($"    BAD serial: '{serialVal}' expected '{expectedSerial}'"); return false; }

    return true;
}

static uint ReadUInt32BE(byte[] data, int offset)
{
    return ((uint)data[offset] << 24) |
           ((uint)data[offset + 1] << 16) |
           ((uint)data[offset + 2] << 8) |
           data[offset + 3];
}
