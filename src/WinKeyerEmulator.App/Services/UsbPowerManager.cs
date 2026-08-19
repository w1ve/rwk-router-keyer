/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using Microsoft.Win32;
using WinKeyerEmulator.Core;

namespace WinKeyerEmulator.App.Services;

/// <summary>
/// Attempts to disable USB selective suspend for a serial port's parent USB device.
/// This is a best-effort operation to prevent the OS from suspending the USB device
/// which can cause latency spikes or port disconnections during CW keying.
/// </summary>
public static class UsbPowerManager
{
    private const string UsbRegistryBase = @"SYSTEM\CurrentControlSet\Enum\USB";
    private const string DeviceParametersKey = "Device Parameters";
    private const string SelectiveSuspendEnabled = "SelectiveSuspendEnabled";

    /// <summary>
    /// Attempts to disable USB selective suspend for the device hosting the specified COM port.
    /// This is best-effort: failures are logged but do not throw.
    /// </summary>
    /// <param name="portName">The COM port name (e.g., "COM3").</param>
    /// <param name="logger">Logger for reporting results.</param>
    /// <returns>True if selective suspend was successfully disabled; false otherwise.</returns>
    public static bool TryDisableSelectiveSuspend(string portName, ILogger logger)
    {
        try
        {
            // Find the device instance path for this COM port via the SERIALCOMM registry
            string? deviceInstancePath = FindDeviceInstanceForPort(portName);
            if (deviceInstancePath is null)
            {
                logger.Log(
                    $"Could not locate USB device for {portName}. Selective suspend not modified.",
                    LogSeverity.Warning,
                    nameof(UsbPowerManager));
                return false;
            }

            // Try to set SelectiveSuspendEnabled = 0 in the device parameters
            string registryPath = $@"SYSTEM\CurrentControlSet\Enum\{deviceInstancePath}\{DeviceParametersKey}";
            using var key = Registry.LocalMachine.OpenSubKey(registryPath, writable: true);
            if (key is null)
            {
                logger.Log(
                    $"Registry key not found for {portName} at {registryPath}. Selective suspend not modified.",
                    LogSeverity.Warning,
                    nameof(UsbPowerManager));
                return false;
            }

            key.SetValue(SelectiveSuspendEnabled, 0, RegistryValueKind.DWord);

            logger.Log(
                $"USB selective suspend disabled for {portName} ({deviceInstancePath}).",
                LogSeverity.Info,
                nameof(UsbPowerManager));
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.Log(
                $"Insufficient permissions to modify USB power settings for {portName}: {ex.Message}",
                LogSeverity.Warning,
                nameof(UsbPowerManager));
            return false;
        }
        catch (Exception ex)
        {
            logger.Log(
                $"Failed to disable selective suspend for {portName}: {ex.Message}",
                LogSeverity.Warning,
                nameof(UsbPowerManager));
            return false;
        }
    }

    /// <summary>
    /// Attempts to find the device instance path for a given COM port
    /// by searching the FTDI and generic USB serial device registry entries.
    /// </summary>
    private static string? FindDeviceInstanceForPort(string portName)
    {
        try
        {
            // Search through USB device registry entries for one that owns this COM port
            using var usbKey = Registry.LocalMachine.OpenSubKey(UsbRegistryBase);
            if (usbKey is null) return null;

            foreach (string vidPid in usbKey.GetSubKeyNames())
            {
                using var vidPidKey = usbKey.OpenSubKey(vidPid);
                if (vidPidKey is null) continue;

                foreach (string serial in vidPidKey.GetSubKeyNames())
                {
                    string instancePath = $"USB\\{vidPid}\\{serial}";
                    string deviceParamsPath = $@"{UsbRegistryBase}\{vidPid}\{serial}\{DeviceParametersKey}";

                    using var paramsKey = Registry.LocalMachine.OpenSubKey(deviceParamsPath);
                    if (paramsKey is null) continue;

                    var portValue = paramsKey.GetValue("PortName") as string;
                    if (string.Equals(portValue, portName, StringComparison.OrdinalIgnoreCase))
                    {
                        return instancePath;
                    }
                }
            }
        }
        catch
        {
            // Best effort search
        }

        return null;
    }
}
