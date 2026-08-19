/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Net;
using WinKeyerEmulator.App.Audio;
using WinKeyerEmulator.App.IO;
using WinKeyerEmulator.App.Services;
using WinKeyerEmulator.Core;
using WinKeyerEmulator.Core.CloudRelay;
using WinKeyerEmulator.Core.IO;
using WinKeyerEmulator.Core.Timing;

namespace WinKeyerEmulator.App.Controllers;

/// <summary>
/// Orchestrates the emulator lifecycle: opens ports, wires events, manages start/stop.
/// </summary>
public class AppController
{
    private readonly ILogger _logger;

    private SerialKeyingOutput? _keyingOutput;
    private SidetoneKeyingOutput? _sidetoneKeyingOutput;
    private SerialCommandSource? _serialCommandSource;
    private UdpCommandSource? _udpCommandSource;
    private CloudRelayTransport? _relayTransport;
    private TimingEngine? _timingEngine;
    private KeyerCore? _keyerCore;

    /// <summary>
    /// Gets whether the emulator is currently running.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// When true, logs hex dumps of all incoming/outgoing bytes on command and UDP ports.
    /// </summary>
    public bool LogRawData { get; set; }

    /// <summary>
    /// Raised when the emulator stops, including on port disconnection.
    /// </summary>
    public event EventHandler? Stopped;

    /// <summary>
    /// Raised when the cloud relay connection status changes.
    /// </summary>
    public event EventHandler<RelayStatusEventArgs>? RelayStatusChanged;

    /// <summary>
    /// Raised when the keying speed changes.
    /// </summary>
    public event EventHandler<int>? SpeedChanged;

    /// <summary>
    /// Raised for timing diagnostic information during keying.
    /// </summary>
    public event EventHandler<TimingDiagnosticEventArgs>? TimingDiagnostic;

    /// <summary>
    /// Creates a new AppController with the specified logger.
    /// </summary>
    public AppController(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Starts the emulator with the specified configuration.
    /// Opens keying port, command port, UDP listener in order.
    /// If any port fails, previously opened resources are closed and the error is reported.
    /// </summary>
    public void Start(AppConfig config)
    {
        if (IsRunning)
            throw new InvalidOperationException("Emulator is already running.");

        try
        {
            // 1. Open keying port
            _keyingOutput = new SerialKeyingOutput();
            _keyingOutput.Open(config.KeyingPortName, config.KeyingLine);
            _logger.Log($"Keying port {config.KeyingPortName} opened ({config.KeyingLine})", LogSeverity.Info, "AppController");

            // 2. Wrap with sidetone if enabled
            _sidetoneKeyingOutput = new SidetoneKeyingOutput(_keyingOutput);
            if (config.SidetoneEnabled)
            {
                try
                {
                    _sidetoneKeyingOutput.ConfigureSidetone(config.SidetoneDeviceId, config.SidetoneFrequency);
                    _sidetoneKeyingOutput.SidetoneEnabled = true;
                    _logger.Log($"Sidetone enabled at {config.SidetoneFrequency} Hz", LogSeverity.Info, "AppController");
                }
                catch (Exception ex)
                {
                    _logger.Log($"Failed to initialize sidetone: {ex.Message}", LogSeverity.Warning, "AppController");
                    _sidetoneKeyingOutput.SidetoneEnabled = false;
                }
            }

            // 3. Open command port (if configured)
            if (!string.IsNullOrEmpty(config.CommandPortName))
            {
                _serialCommandSource = new SerialCommandSource();
                _serialCommandSource.Start(config.CommandPortName);
                _serialCommandSource.Disconnected += OnSerialDisconnected;
                _logger.Log($"Command port {config.CommandPortName} opened", LogSeverity.Info, "AppController");
            }

            // 4. Start remote transport (UDP or Cloud Relay)
            if (config.Transport == TransportMode.CloudRelay)
            {
                if (string.IsNullOrEmpty(config.PairingToken) || !TokenGenerator.IsValid(config.PairingToken))
                    throw new ArgumentException("A valid 64-character pairing token is required for Cloud Relay mode.");

                var relayConfig = new RelayConfig
                {
                    RelayUrl = config.RelayUrl,
                    PairingToken = config.PairingToken,
                    EndpointType = RelayEndpointType.StationSide,
                };
                _relayTransport = new CloudRelayTransport(relayConfig);
                _relayTransport.StatusChanged += OnRelayStatusChanged;
                _relayTransport.Error += OnRelayError;
                _relayTransport.DataReceived += OnCommandReceived_Relay;
                _relayTransport.Start();
                _logger.Log($"Cloud Relay transport started (token: {config.PairingToken[..8]}...)", LogSeverity.Info, "AppController");
            }
            else
            {
                var udpEndpoint = new IPEndPoint(IPAddress.Parse(config.UdpAddress), config.UdpPort);
                _udpCommandSource = new UdpCommandSource();
                _udpCommandSource.Start(udpEndpoint);
                _logger.Log($"UDP listener started on {config.UdpAddress}:{config.UdpPort}", LogSeverity.Info, "AppController");
            }

            // 5. Create TimingEngine with sidetone-wrapped keying output and StopwatchClock
            var clock = new StopwatchClock();
            _timingEngine = new TimingEngine(_sidetoneKeyingOutput, clock);
            _timingEngine.Weight = config.Weight;

            // 6. Hook up timeBeginPeriod/timeEndPeriod
            _timingEngine.OnThreadStart = () => NativeMethods.TimeBeginPeriod(1);
            _timingEngine.OnThreadStop = () => NativeMethods.TimeEndPeriod(1);

            if (config.Weight != 50)
            {
                _logger.Log($"CW weight set to {config.Weight}%", LogSeverity.Info, "AppController");
            }

            // 7. Create KeyerCore
            _keyerCore = new KeyerCore(_sidetoneKeyingOutput, _timingEngine, _logger);
            _keyerCore.ResponseAvailable += OnAsyncResponse;
            _keyerCore.SpeedChanged += OnSpeedChanged;
            _keyerCore.TimingDiagnostic += OnTimingDiagnostic;

            // 8. Wire DataReceived events
            if (_serialCommandSource is not null)
            {
                _serialCommandSource.DataReceived += OnCommandReceived_Serial;
            }
            if (_udpCommandSource is not null)
            {
                _udpCommandSource.DataReceived += OnCommandReceived_Udp;
            }

            // 9. Start TimingEngine
            _timingEngine.Start();

            // 10. Try to disable USB selective suspend (best effort)
            UsbPowerManager.TryDisableSelectiveSuspend(config.KeyingPortName, _logger);

            IsRunning = true;
            _logger.Log("Emulator started", LogSeverity.Info, "AppController");
        }
        catch (Exception ex)
        {
            _logger.Log($"Start failed: {ex.Message}", LogSeverity.Error, "AppController");
            CleanupResources();
            throw;
        }
    }

    /// <summary>
    /// Stops the emulator, reversing start order: stop timing, close sources, close keying.
    /// </summary>
    public void Stop()
    {
        if (!IsRunning)
            return;

        IsRunning = false;
        _logger.Log("Stopping emulator...", LogSeverity.Info, "AppController");

        try
        {
            CleanupResources();
        }
        catch (Exception ex)
        {
            _logger.Log($"Error during stop: {ex.Message}", LogSeverity.Warning, "AppController");
        }

        _logger.Log("Emulator stopped", LogSeverity.Info, "AppController");
        Stopped?.Invoke(this, EventArgs.Empty);
    }

    private void CleanupResources()
    {
        // 1. Unwire events first to prevent new data from arriving
        try
        {
            if (_serialCommandSource is not null)
                _serialCommandSource.DataReceived -= OnCommandReceived_Serial;
            if (_udpCommandSource is not null)
                _udpCommandSource.DataReceived -= OnCommandReceived_Udp;
            if (_relayTransport is not null)
            {
                _relayTransport.DataReceived -= OnCommandReceived_Relay;
                _relayTransport.StatusChanged -= OnRelayStatusChanged;
                _relayTransport.Error -= OnRelayError;
            }
        }
        catch { }

        // 2. Dispose keyer core (stops flush timer, prevents new enqueues)
        try
        {
            if (_keyerCore is not null)
            {
                _keyerCore.ResponseAvailable -= OnAsyncResponse;
                _keyerCore.SpeedChanged -= OnSpeedChanged;
                _keyerCore.TimingDiagnostic -= OnTimingDiagnostic;
                _keyerCore.Dispose();
                _keyerCore = null;
            }
        }
        catch { }

        // 3. Stop timing engine
        try
        {
            if (_timingEngine is not null)
            {
                _timingEngine.Stop();
                _timingEngine.Dispose();
                _timingEngine = null;
            }
        }
        catch { }

        // 4. Stop serial command source
        try
        {
            if (_serialCommandSource is not null)
            {
                _serialCommandSource.Disconnected -= OnSerialDisconnected;
                _serialCommandSource.Dispose();
                _serialCommandSource = null;
            }
        }
        catch { }

        // 5. Stop UDP source
        try
        {
            if (_udpCommandSource is not null)
            {
                _udpCommandSource.Dispose();
                _udpCommandSource = null;
            }
        }
        catch { }

        // 6. Stop Cloud Relay transport
        try
        {
            if (_relayTransport is not null)
            {
                _relayTransport.Dispose();
                _relayTransport = null;
            }
        }
        catch { }

        // 7. Close sidetone wrapper (also disposes sidetone audio)
        try
        {
            if (_sidetoneKeyingOutput is not null)
            {
                _sidetoneKeyingOutput.Dispose();
                _sidetoneKeyingOutput = null;
            }
        }
        catch { }

        // 8. Close keying output
        try
        {
            if (_keyingOutput is not null)
            {
                _keyingOutput.Dispose();
                _keyingOutput = null;
            }
        }
        catch { }
    }

    private void OnCommandReceived_Serial(object? sender, byte[] data)
    {
        if (!IsRunning || _keyerCore is null) return;

        if (LogRawData)
            _logger.Log($"Serial RX: {FormatHex(data)}", LogSeverity.Info, "RawData");

        var response = _keyerCore.ProcessCommand(data);
        if (response is not null && _serialCommandSource is not null)
        {
            if (LogRawData)
                _logger.Log($"Serial TX: {FormatHex(response)}", LogSeverity.Info, "RawData");
            _serialCommandSource.SendResponse(response);
        }
    }

    private void OnCommandReceived_Udp(object? sender, byte[] data)
    {
        if (!IsRunning || _keyerCore is null) return;

        if (LogRawData)
            _logger.Log($"UDP RX: {FormatHex(data)}", LogSeverity.Info, "RawData");

        var response = _keyerCore.ProcessCommand(data);
        if (response is not null && _udpCommandSource is not null)
        {
            if (LogRawData)
                _logger.Log($"UDP TX: {FormatHex(response)}", LogSeverity.Info, "RawData");
            _udpCommandSource.SendResponse(response);
        }
    }

    private void OnCommandReceived_Relay(object? sender, byte[] data)
    {
        if (!IsRunning || _keyerCore is null) return;

        if (LogRawData)
            _logger.Log($"Relay RX: {FormatHex(data)}", LogSeverity.Info, "RawData");

        var response = _keyerCore.ProcessCommand(data);
        if (response is not null && _relayTransport is not null)
        {
            if (LogRawData)
                _logger.Log($"Relay TX: {FormatHex(response)}", LogSeverity.Info, "RawData");
            _relayTransport.SendResponse(response);
        }
    }

    private void OnRelayStatusChanged(object? sender, RelayStatusEventArgs e)
    {
        _logger.Log($"Relay: {e.Status} — {e.Message}", LogSeverity.Info, "CloudRelay");
        RelayStatusChanged?.Invoke(this, e);
    }

    private void OnRelayError(object? sender, string message)
    {
        _logger.Log($"Relay error: {message}", LogSeverity.Warning, "CloudRelay");
    }

    private void OnSpeedChanged(object? sender, int wpm)
    {
        SpeedChanged?.Invoke(this, wpm);
    }

    private void OnTimingDiagnostic(object? sender, TimingDiagnosticEventArgs e)
    {
        TimingDiagnostic?.Invoke(this, e);
    }

    private static string FormatHex(byte[] data)
    {
        // If all bytes are printable ASCII (0x20-0x7E), show as text with hex prefix
        if (data.Length > 0 && data.All(b => b >= 0x20 && b <= 0x7E))
        {
            string text = System.Text.Encoding.ASCII.GetString(data);
            return $"\"{text}\" ({string.Join(" ", data.Select(b => b.ToString("X2")))})";
        }
        return string.Join(" ", data.Select(b => b.ToString("X2")));
    }

    private void OnSerialDisconnected(object? sender, EventArgs e)
    {
        if (!IsRunning) return; // Already stopping
        _logger.Log("Command port disconnected", LogSeverity.Warning, "AppController");
        Stop();
    }

    /// <summary>
    /// Handles asynchronous responses from KeyerCore (character echoes, status bytes).
    /// Sends them back through whichever command source is active.
    /// </summary>
    private void OnAsyncResponse(object? sender, byte[] data)
    {
        if (!IsRunning) return;

        if (LogRawData)
            _logger.Log($"Async TX: {FormatHex(data)}", LogSeverity.Info, "RawData");

        // Send to serial command source if connected
        try { _serialCommandSource?.SendResponse(data); } catch { }

        // Send to UDP source if connected and has a client
        try { _udpCommandSource?.SendResponse(data); } catch { }

        // Send to cloud relay if connected
        try { _relayTransport?.SendResponse(data); } catch { }
    }
}
