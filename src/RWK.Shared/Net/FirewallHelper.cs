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

namespace RWK.Shared.Net;

/// <summary>
/// Manages Windows Firewall rules for the RWK application executables.
/// Creates inbound allow rules for the running exe so that port forwarding,
/// discovery capture (UDP 4992), and control channel (TCP 7373) all work
/// without manual firewall configuration.
/// </summary>
/// <remarks>
/// Uses <c>netsh advfirewall</c> which requires elevation. If the process is
/// not elevated, the call fails gracefully and returns false — the caller can
/// prompt the user or log a warning.
/// </remarks>
public static class FirewallHelper
{
    /// <summary>
    /// Ensures a Windows Firewall inbound allow rule exists for the current executable.
    /// If the rule already exists, this is a no-op. If elevation is required but not
    /// available, returns false.
    /// </summary>
    /// <param name="ruleName">Display name for the firewall rule.</param>
    /// <param name="diagnostics">Optional log callback.</param>
    /// <returns>True if the rule exists or was created; false if creation failed.</returns>
    public static bool EnsureAppAllowed(string ruleName, Action<string>? diagnostics = null)
    {
        string exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(exePath)) return false;

        try
        {
            // Check if rule already exists
            if (RuleExists(ruleName))
            {
                diagnostics?.Invoke($"Firewall rule '{ruleName}' already exists.");
                return true;
            }

            // Create the rule (requires elevation)
            string args = $"advfirewall firewall add rule name=\"{ruleName}\" " +
                          $"dir=in action=allow program=\"{exePath}\" enable=yes " +
                          $"profile=any";

            int exitCode = RunNetsh(args);
            if (exitCode == 0)
            {
                diagnostics?.Invoke($"Firewall rule '{ruleName}' created for {Path.GetFileName(exePath)}.");
                return true;
            }
            else
            {
                diagnostics?.Invoke($"Failed to create firewall rule (exit code {exitCode}). May need elevation.");
                return false;
            }
        }
        catch (Exception ex)
        {
            diagnostics?.Invoke($"Firewall rule creation error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks whether a firewall rule with the given name already exists.
    /// </summary>
    public static bool RuleExists(string ruleName)
    {
        try
        {
            string args = $"advfirewall firewall show rule name=\"{ruleName}\"";
            int exitCode = RunNetsh(args, captureOutput: true);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a firewall rule by name. Returns true if successful or rule didn't exist.
    /// </summary>
    public static bool RemoveRule(string ruleName, Action<string>? diagnostics = null)
    {
        try
        {
            if (!RuleExists(ruleName)) return true;

            string args = $"advfirewall firewall delete rule name=\"{ruleName}\"";
            int exitCode = RunNetsh(args);
            if (exitCode == 0)
            {
                diagnostics?.Invoke($"Firewall rule '{ruleName}' removed.");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            diagnostics?.Invoke($"Firewall rule removal error: {ex.Message}");
            return false;
        }
    }

    private static int RunNetsh(string arguments, bool captureOutput = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput
        };

        using var process = Process.Start(psi);
        if (process is null) return -1;

        process.WaitForExit(10_000);
        return process.ExitCode;
    }
}
