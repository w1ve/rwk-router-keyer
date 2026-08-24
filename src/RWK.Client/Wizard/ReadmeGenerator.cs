/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Diagnostics;
using System.Text;

namespace RWK.Client.Wizard;

/// <summary>
/// Generates the plain-text setup guide ([radioname]-readme.txt) per §8 of the spec.
/// Constraints: hard-wrapped at 76 columns, CRLF line endings, ASCII only, no Markdown.
/// </summary>
public static class ReadmeGenerator
{
    private const int MaxWidth = 76;
    private const string Separator = "================================================================";
    private const string SubSeparator = "----------------------------------------------";

    /// <summary>
    /// Generates the readme text content and writes it to disk.
    /// Returns the full file path.
    /// </summary>
    public static string Generate(WizardProfile profile, IReadOnlyList<ConflictResult>? conflicts = null)
    {
        string path = ProfileManager.GetReadmePath(profile.Profile.Name);
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        string content = BuildContent(profile, conflicts);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>
    /// Builds the readme content string.
    /// </summary>
    public static string BuildContent(WizardProfile profile, IReadOnlyList<ConflictResult>? conflicts = null)
    {
        var sb = new StringBuilder();

        // Header
        AppendLine(sb, Separator);
        AppendLine(sb, " RWK PORT FORWARD SETUP");
        AppendLine(sb, $" {Ascii(profile.Profile.Name)}");
        AppendLine(sb, $" Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC by {profile.Generator}");
        AppendLine(sb, Separator);
        AppendLine(sb, "");

        // WHAT THIS DOES
        AppendLine(sb, "WHAT THIS DOES");
        AppendLine(sb, SubSeparator);
        AppendWrapped(sb,
            "These port forwards make devices at your remote station appear on " +
            "this PC at 127.0.0.1, so your control software connects as though " +
            "the radio were plugged in here. No public IP address, no dynamic " +
            "DNS, no router configuration at either end.");
        AppendLine(sb, "");

        // BEFORE YOU CONNECT
        if (profile.SetupNotes.Radio.Count > 0 ||
            profile.SetupNotes.Client.Count > 0 ||
            profile.SetupNotes.Station.Count > 0)
        {
            int stepNum = 0;
            int totalSteps = (profile.SetupNotes.Radio.Count > 0 ? 1 : 0)
                           + (profile.SetupNotes.Client.Count > 0 ? 1 : 0)
                           + 1; // always have "enable the rules" step

            AppendLine(sb, $"BEFORE YOU CONNECT -- {totalSteps + (profile.SetupNotes.Station.Count > 0 ? 1 : 0)} things still need doing");
            AppendLine(sb, SubSeparator);
            AppendLine(sb, "");

            if (profile.SetupNotes.Radio.Count > 0)
            {
                stepNum++;
                AppendLine(sb, $"{stepNum}. ON THE RADIO");
                foreach (var note in profile.SetupNotes.Radio)
                    AppendWrapped(sb, $"   {Ascii(note)}", 3);
                AppendLine(sb, "");
            }

            if (profile.SetupNotes.Station.Count > 0)
            {
                stepNum++;
                AppendLine(sb, $"{stepNum}. ON THE STATION");
                foreach (var note in profile.SetupNotes.Station)
                    AppendWrapped(sb, $"   {Ascii(note)}", 3);
                AppendLine(sb, "");
            }

            if (profile.SetupNotes.Client.Count > 0)
            {
                stepNum++;
                AppendLine(sb, $"{stepNum}. ON THIS PC -- Control Software");
                foreach (var note in profile.SetupNotes.Client)
                    AppendWrapped(sb, $"   {Ascii(note)}", 3);
                AppendLine(sb, "");
            }

            stepNum++;
            AppendLine(sb, $"{stepNum}. ENABLE THE RULES");
            AppendWrapped(sb,
                "   The rules were created but left disabled. In the Port Forwards " +
                "panel, select all and click \"Enable Sel\". Status should read " +
                "\"Listening\".", 3);
            AppendLine(sb, "");
        }

        // RULES CREATED
        AppendLine(sb, "RULES CREATED");
        AppendLine(sb, SubSeparator);

        // Table header
        AppendLine(sb, $"   {"Name",-20} {"Proto",-6} {"Local",-22} {"-> Station",-22}");
        foreach (var rule in profile.Forwards)
        {
            string local = $"{rule.BindAddress}:{rule.ClientPort}";
            string remote = $"{rule.StationTarget}:{rule.StationPort}";
            AppendLine(sb, $"   {Ascii(rule.Name),-20} {rule.Protocol,-6} {local,-22} -> {remote}");
        }
        AppendLine(sb, "");

        // Port identity notes
        var requiredPorts = profile.Forwards.Where(f => f.PortIdentity is "required" or "unknown").ToList();
        if (requiredPorts.Count > 0)
        {
            AppendWrapped(sb,
                "   These ports must stay identical on both sides. The protocol " +
                "takes port numbers from the server's own settings rather than " +
                "asking for them here, so renumbering one side alone will cause " +
                "connection failures. Do not change them.", 3);
            AppendLine(sb, "");
        }

        // SERIAL BRIDGE CONFIGURATION (if applicable)
        if (profile.SerialBridge is not null)
        {
            var br = profile.SerialBridge;
            AppendLine(sb, "SERIAL BRIDGE CONFIGURATION");
            AppendLine(sb, SubSeparator);
            AppendLine(sb, "");
            AppendLine(sb, $"   Device:          {Ascii(br.DeviceName)}");
            AppendLine(sb, $"   Preset:          {Ascii(br.PresetName)}");
            AppendLine(sb, $"   TCP port:        {br.TcpPort}");
            AppendLine(sb, $"   Client COM port: COM{br.ClientComPort} (virtual)");
            AppendLine(sb, $"   Station COM port: {br.StationComPort} (real, connected to radio)");
            AppendLine(sb, "");
            AppendLine(sb, "   Serial parameters:");
            AppendLine(sb, $"     Baud rate:  {br.BaudRate}");
            AppendLine(sb, $"     Data bits:  {br.DataBits}");
            AppendLine(sb, $"     Parity:     {br.Parity}");
            AppendLine(sb, $"     Stop bits:  {br.StopBits}");
            AppendLine(sb, $"     DTR:        {br.DtrControl}");
            AppendLine(sb, $"     RTS:        {br.RtsControl}");
            AppendLine(sb, "");

            // DTR/RTS warning
            if (br.DtrControl == "Off" && br.RtsControl == "Off")
            {
                AppendLine(sb, "   IMPORTANT -- DTR/RTS and PTT:");
                AppendWrapped(sb,
                    "   A TCP serial bridge carries DATA ONLY. It does NOT carry " +
                    "DTR, RTS, CTS, or DSR modem control lines. If your logger " +
                    "uses RTS or DTR for PTT, that will NOT work through this " +
                    "bridge.", 3);
                AppendLine(sb, "");
                AppendLine(sb, "   Alternatives for PTT over the bridge:");
                AppendLine(sb, "     - Use CAT PTT commands (TX;/RX; for Kenwood/Yaesu,");
                AppendLine(sb, "       FEFE...1C00.../1C0001 for Icom CI-V)");
                AppendLine(sb, "     - Use RWK's own keyer output for CW PTT");
                AppendLine(sb, "     - Use VOX on the radio");
                AppendLine(sb, "");
            }

            // COM port conflict guidance
            AppendLine(sb, "   COM PORT SELECTION:");
            AppendWrapped(sb,
                $"   COM{br.ClientComPort} was chosen as the client virtual port. " +
                "If this conflicts with an existing port, change the number in " +
                "VSPE to any unused COMxx above COM19. High-numbered ports " +
                "(COM20-COM99) are virtually never physical hardware.", 3);
            AppendLine(sb, "");
            AppendWrapped(sb,
                $"   {br.StationComPort} is the real port on the Station PC. Verify " +
                "this is the correct port by checking Device Manager on the " +
                "Station. If the radio is connected via USB, note that the COM " +
                "port number can change if you plug into a different USB port.", 3);
            AppendLine(sb, "");

            // VSPE file locations
            if (profile.SetupNotes.VirtualSerial.Count > 0)
            {
                AppendLine(sb, "   GENERATED FILES:");
                foreach (var note in profile.SetupNotes.VirtualSerial)
                    AppendLine(sb, $"     {Ascii(note)}");
                AppendLine(sb, "");
                AppendWrapped(sb,
                    "   Double-click the .vspe files to load them into VSPE. " +
                    "The Station file goes on the Station PC, the Client file " +
                    "goes on this PC. Alternatively, use the com2tcp.cmd script " +
                    "with com0com (free/open-source alternative to VSPE).", 3);
                AppendLine(sb, "");
                AppendWrapped(sb,
                    "   NOTE: VSPE's 64-bit driver requires a paid licence. The " +
                    "free alternative is com0com (virtual port pairs) plus " +
                    "com2tcp, which ships with it. The .cmd file has the exact " +
                    "commands for both sides.", 3);
                AppendLine(sb, "");
            }

            // Latency guidance
            AppendLine(sb, "   LATENCY GUIDANCE:");
            AppendWrapped(sb,
                "   CAT polling that is comfortable on a local USB cable can " +
                "misbehave across a tunnel. Recommendations:", 3);
            AppendLine(sb, "     - Raise the logger's CAT poll interval to 500ms or more");
            AppendLine(sb, "     - Disable 'verify every command' / read-back options");
            AppendLine(sb, "     - For Icom CI-V: disable transceive mode if the logger");
            AppendLine(sb, "       supports polling instead");
            AppendLine(sb, "     - Expect 1-5ms added latency on Direct, 20-50ms on DERP");
            AppendLine(sb, "");
        }

        // WARNINGS FROM SETUP
        var warnings = conflicts?.Where(c => c.Severity == ConflictSeverity.Warning).ToList();
        if (warnings is { Count: > 0 })
        {
            AppendLine(sb, "WARNINGS FROM SETUP");
            AppendLine(sb, SubSeparator);
            foreach (var w in warnings)
                AppendWrapped(sb, $"   * {Ascii(w.Message)}", 5);
            AppendLine(sb, "");
        }

        // IF IT DOES NOT WORK
        AppendLine(sb, "IF IT DOES NOT WORK");
        AppendLine(sb, SubSeparator);
        AppendWrapped(sb,
            "   Status stuck at \"Listening\", client times out", 3);
        AppendWrapped(sb,
            "     -> Almost always the station target IP. Confirm the address " +
            "is still correct and reachable from the Station PC.", 7);
        AppendLine(sb, "");
        AppendWrapped(sb,
            "   Connection refused or reset", 3);
        AppendWrapped(sb,
            "     -> The target service is not running, or is listening on a " +
            "different port than configured here.", 7);
        AppendLine(sb, "");
        AppendWrapped(sb,
            "   RWK status bar shows path type (Direct or DERP) and RTT. If " +
            "it reads DERP, expect 20-50 ms more latency; this is normal and " +
            "does not affect most control protocols.", 3);
        AppendLine(sb, "");

        // Footer
        AppendLine(sb, Separator);
        string profileFile = ProfileManager.SanitizeFileName(profile.Profile.Name) + ".rwkprofile.json";
        AppendLine(sb, $" Profile saved: {profileFile}");
        AppendLine(sb, " Re-run the wizard at any time; it updates these rules in place.");
        AppendLine(sb, Separator);

        return sb.ToString();
    }

    /// <summary>
    /// Opens the generated readme in the default text editor.
    /// Falls back to notepad.exe if shell execute fails.
    /// </summary>
    public static void OpenInEditor(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = $"\"{filePath}\"",
                    UseShellExecute = false
                });
            }
            catch
            {
                // Both failed — caller should show the path in a dialog.
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────────────────

    private static void AppendLine(StringBuilder sb, string line)
    {
        sb.Append(line);
        sb.Append("\r\n");
    }

    /// <summary>
    /// Wraps text at MaxWidth columns with optional indent for continuation lines.
    /// </summary>
    private static void AppendWrapped(StringBuilder sb, string text, int hangingIndent = 0)
    {
        string ascii = Ascii(text);
        if (ascii.Length <= MaxWidth)
        {
            AppendLine(sb, ascii);
            return;
        }

        string indent = new(' ', hangingIndent);
        int pos = 0;
        bool firstLine = true;

        while (pos < ascii.Length)
        {
            int lineStart = pos;
            int available = firstLine ? MaxWidth : MaxWidth - hangingIndent;
            int lineEnd = Math.Min(pos + available, ascii.Length);

            if (lineEnd < ascii.Length)
            {
                // Find last space within the available width to break at.
                int breakAt = ascii.LastIndexOf(' ', lineEnd - 1, lineEnd - lineStart);
                if (breakAt > lineStart)
                    lineEnd = breakAt;
            }

            string segment = ascii[lineStart..lineEnd].TrimEnd();
            if (firstLine)
            {
                AppendLine(sb, segment);
                firstLine = false;
            }
            else
            {
                AppendLine(sb, indent + segment);
            }

            pos = lineEnd;
            // Skip the space we broke at.
            if (pos < ascii.Length && ascii[pos] == ' ')
                pos++;
        }
    }

    /// <summary>
    /// Transliterates non-ASCII characters to ASCII equivalents for Notepad compatibility.
    /// </summary>
    private static string Ascii(string text)
    {
        return text
            .Replace('\u2014', '-')  // em-dash -> hyphen
            .Replace('\u2013', '-')  // en-dash -> hyphen
            .Replace('\u2192', '-')  // right arrow -> dash (->)
            .Replace('\u2190', '<')  // left arrow
            .Replace('\u2018', '\'') // left single quote
            .Replace('\u2019', '\'') // right single quote
            .Replace('\u201C', '"')  // left double quote
            .Replace('\u201D', '"')  // right double quote
            .Replace('\u2026', '.') // ellipsis
            .Replace("\u2014", "--"); // em-dash string form
    }
}
