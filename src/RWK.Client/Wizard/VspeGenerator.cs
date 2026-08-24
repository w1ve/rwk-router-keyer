/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Text;

namespace RWK.Client.Wizard;

/// <summary>
/// Generates VSPE configuration XML files for the serial bridge sub-flow.
/// Produces two files:
/// - Client .vspe: TcpClient device → virtual COM port (connects to localhost:tcpPort)
/// - Station .vspe: TcpServer device → real COM port (listens on 0.0.0.0:tcpPort)
/// </summary>
public static class VspeGenerator
{
    /// <summary>
    /// Serial bridge configuration used to generate VSPE files and readme text.
    /// All fields flow from the Wizard's Step 3 serial sub-flow inputs.
    /// The <see cref="TcpPort"/> value is shared between the RWK port forwarding rule,
    /// the client VSPE TcpClient target, and the station VSPE TcpServer listener.
    /// </summary>
    public sealed class SerialBridgeConfig
    {
        /// <summary>Descriptive name for the bridge (e.g. "IC-7300 CAT").</summary>
        public string DeviceName { get; set; } = "CAT Bridge";

        /// <summary>TCP port used for the tunnel (e.g. 4000).</summary>
        public int TcpPort { get; set; } = 4000;

        /// <summary>Virtual COM port number on the Client side (e.g. 20 for COM20).</summary>
        public int ClientComPort { get; set; } = 20;

        /// <summary>Real COM port name on the Station side (e.g. "COM3").</summary>
        public string StationComPort { get; set; } = "COM3";

        /// <summary>Baud rate.</summary>
        public int BaudRate { get; set; } = 9600;

        /// <summary>Data bits (5, 6, 7, or 8).</summary>
        public int DataBits { get; set; } = 8;

        /// <summary>Parity: "None", "Even", "Odd", "Mark", "Space".</summary>
        public string Parity { get; set; } = "None";

        /// <summary>Stop bits (1 or 2).</summary>
        public int StopBits { get; set; } = 1;

        /// <summary>DTR control: "Off", "On", "Handshake".</summary>
        public string DtrControl { get; set; } = "Off";

        /// <summary>RTS control: "Off", "On", "Handshake".</summary>
        public string RtsControl { get; set; } = "Off";

        /// <summary>The preset name that was selected (for the readme).</summary>
        public string PresetName { get; set; } = "Generic";
    }

    /// <summary>
    /// Generates the Client-side VSPE configuration file.
    /// Creates a TcpClient virtual COM port that connects to 127.0.0.1:tcpPort.
    /// </summary>
    public static string GenerateClientVspe(SerialBridgeConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<VspeConfig>");
        sb.AppendLine("  <Version>1</Version>");
        sb.AppendLine($"  <Description>RWK Serial Bridge - Client - {EscapeXml(config.DeviceName)}</Description>");
        sb.AppendLine("  <Devices>");
        sb.AppendLine("    <Device>");
        sb.AppendLine("      <Type>TcpClient</Type>");
        sb.AppendLine($"      <Name>COM{config.ClientComPort}</Name>");
        sb.AppendLine($"      <ComPortNumber>{config.ClientComPort}</ComPortNumber>");
        sb.AppendLine("      <TcpClient>");
        sb.AppendLine("        <RemoteHost>127.0.0.1</RemoteHost>");
        sb.AppendLine($"        <RemotePort>{config.TcpPort}</RemotePort>");
        sb.AppendLine("        <AutoReconnect>true</AutoReconnect>");
        sb.AppendLine("        <ReconnectInterval>3000</ReconnectInterval>");
        sb.AppendLine("      </TcpClient>");
        sb.AppendLine("      <SerialSettings>");
        sb.AppendLine($"        <BaudRate>{config.BaudRate}</BaudRate>");
        sb.AppendLine($"        <DataBits>{config.DataBits}</DataBits>");
        sb.AppendLine($"        <Parity>{config.Parity}</Parity>");
        sb.AppendLine($"        <StopBits>{config.StopBits}</StopBits>");
        sb.AppendLine($"        <DtrControl>{config.DtrControl}</DtrControl>");
        sb.AppendLine($"        <RtsControl>{config.RtsControl}</RtsControl>");
        sb.AppendLine("      </SerialSettings>");
        sb.AppendLine("    </Device>");
        sb.AppendLine("  </Devices>");
        sb.AppendLine("</VspeConfig>");
        return sb.ToString();
    }

    /// <summary>
    /// Generates the Station-side VSPE configuration file.
    /// Creates a TcpServer that bridges a real COM port to TCP (listens on 0.0.0.0:tcpPort).
    /// </summary>
    public static string GenerateStationVspe(SerialBridgeConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<VspeConfig>");
        sb.AppendLine("  <Version>1</Version>");
        sb.AppendLine($"  <Description>RWK Serial Bridge - Station - {EscapeXml(config.DeviceName)}</Description>");
        sb.AppendLine("  <Devices>");
        sb.AppendLine("    <Device>");
        sb.AppendLine("      <Type>TcpServer</Type>");
        sb.AppendLine($"      <Name>{config.StationComPort} via TCP</Name>");
        sb.AppendLine("      <TcpServer>");
        sb.AppendLine("        <ListenAddress>0.0.0.0</ListenAddress>");
        sb.AppendLine($"        <ListenPort>{config.TcpPort}</ListenPort>");
        sb.AppendLine("        <MaxConnections>1</MaxConnections>");
        sb.AppendLine("      </TcpServer>");
        sb.AppendLine($"      <DataSource>{config.StationComPort}</DataSource>");
        sb.AppendLine("      <SerialSettings>");
        sb.AppendLine($"        <BaudRate>{config.BaudRate}</BaudRate>");
        sb.AppendLine($"        <DataBits>{config.DataBits}</DataBits>");
        sb.AppendLine($"        <Parity>{config.Parity}</Parity>");
        sb.AppendLine($"        <StopBits>{config.StopBits}</StopBits>");
        sb.AppendLine($"        <DtrControl>{config.DtrControl}</DtrControl>");
        sb.AppendLine($"        <RtsControl>{config.RtsControl}</RtsControl>");
        sb.AppendLine("      </SerialSettings>");
        sb.AppendLine("    </Device>");
        sb.AppendLine("  </Devices>");
        sb.AppendLine("</VspeConfig>");
        return sb.ToString();
    }

    /// <summary>
    /// Generates com0com/com2tcp command lines as an alternative to VSPE.
    /// </summary>
    public static string GenerateCom2TcpCommands(SerialBridgeConfig config)
    {
        string parityChar = config.Parity switch
        {
            "Even" => "e",
            "Odd" => "o",
            "Mark" => "m",
            "Space" => "s",
            _ => "n"
        };

        var sb = new StringBuilder();
        sb.AppendLine(":: ============================================================");
        sb.AppendLine($":: RWK Serial Bridge - {config.DeviceName}");
        sb.AppendLine(":: Alternative to VSPE using com0com + com2tcp (free/open-source)");
        sb.AppendLine(":: ============================================================");
        sb.AppendLine();
        sb.AppendLine(":: STATION SIDE");
        sb.AppendLine($":: Bridges real {config.StationComPort} to TCP listener on port {config.TcpPort}");
        sb.AppendLine($"com2tcp --baud {config.BaudRate} --parity {parityChar} --data {config.DataBits} --stop {config.StopBits} \\\\.\\{config.StationComPort} {config.TcpPort}");
        sb.AppendLine();
        sb.AppendLine(":: CLIENT SIDE");
        sb.AppendLine($":: First create a com0com pair: CNCA0/CNCB0 (use com0com setupc.exe)");
        sb.AppendLine($":: Then bridge CNCB0 to the tunnel endpoint:");
        sb.AppendLine($"com2tcp --baud {config.BaudRate} --parity {parityChar} --data {config.DataBits} --stop {config.StopBits} \\\\.\\CNCB0 127.0.0.1 {config.TcpPort}");
        sb.AppendLine($":: Your logger uses CNCA0 (appears as a normal COM port)");
        sb.AppendLine();
        sb.AppendLine(":: NOTE: com0com's virtual ports may need renaming to COMxx via");
        sb.AppendLine(":: setupc.exe: change CNCA0 COMxx");
        return sb.ToString();
    }

    /// <summary>
    /// Writes both VSPE files and the com2tcp script to the profiles directory.
    /// Returns the paths of all generated files.
    /// </summary>
    public static SerialBridgeFiles WriteFiles(SerialBridgeConfig config, string profileBaseName)
    {
        string dir = ProfileManager.GetProfilesDirectory();
        Directory.CreateDirectory(dir);

        string clientVspePath = Path.Combine(dir, $"{profileBaseName}-client.vspe");
        string stationVspePath = Path.Combine(dir, $"{profileBaseName}-station.vspe");
        string com2tcpPath = Path.Combine(dir, $"{profileBaseName}-com2tcp.cmd");

        File.WriteAllText(clientVspePath, GenerateClientVspe(config), Encoding.UTF8);
        File.WriteAllText(stationVspePath, GenerateStationVspe(config), Encoding.UTF8);
        File.WriteAllText(com2tcpPath, GenerateCom2TcpCommands(config), Encoding.UTF8);

        return new SerialBridgeFiles(clientVspePath, stationVspePath, com2tcpPath);
    }

    private static string EscapeXml(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

/// <summary>Paths of the generated serial bridge files.</summary>
public record SerialBridgeFiles(string ClientVspePath, string StationVspePath, string Com2TcpPath);
