/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.IO.Ports;
using System.Management;

namespace WinKeyerEmulator.App.Services;

/// <summary>
/// Monitors serial port hot-plug events via WMI and notifies listeners
/// when ports are added or removed.
/// </summary>
public sealed class PortMonitor : IDisposable
{
    private ManagementEventWatcher? _creationWatcher;
    private ManagementEventWatcher? _deletionWatcher;
    private SynchronizationContext? _syncContext;
    private bool _disposed;

    /// <summary>
    /// Raised when the set of available serial ports changes.
    /// Provides the current list of available port names.
    /// </summary>
    public event EventHandler<string[]>? PortsChanged;

    /// <summary>
    /// Starts monitoring for serial port arrival and removal events.
    /// Should be called from the UI thread to capture the SynchronizationContext.
    /// </summary>
    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PortMonitor));

        _syncContext = SynchronizationContext.Current;

        // Watch for new serial port devices
        var creationQuery = new WqlEventQuery(
            "__InstanceCreationEvent",
            TimeSpan.FromSeconds(2),
            "TargetInstance ISA 'Win32_PnPEntity' AND TargetInstance.Name LIKE '%(COM%'");

        _creationWatcher = new ManagementEventWatcher(creationQuery);
        _creationWatcher.EventArrived += OnPortEvent;
        _creationWatcher.Start();

        // Watch for removed serial port devices
        var deletionQuery = new WqlEventQuery(
            "__InstanceDeletionEvent",
            TimeSpan.FromSeconds(2),
            "TargetInstance ISA 'Win32_PnPEntity' AND TargetInstance.Name LIKE '%(COM%'");

        _deletionWatcher = new ManagementEventWatcher(deletionQuery);
        _deletionWatcher.EventArrived += OnPortEvent;
        _deletionWatcher.Start();
    }

    /// <summary>
    /// Gets the currently available serial port names.
    /// </summary>
    public string[] GetAvailablePorts()
    {
        return SerialPort.GetPortNames();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            if (_creationWatcher is not null)
            {
                _creationWatcher.EventArrived -= OnPortEvent;
                _creationWatcher.Stop();
                _creationWatcher.Dispose();
                _creationWatcher = null;
            }

            if (_deletionWatcher is not null)
            {
                _deletionWatcher.EventArrived -= OnPortEvent;
                _deletionWatcher.Stop();
                _deletionWatcher.Dispose();
                _deletionWatcher = null;
            }
        }
    }

    private void OnPortEvent(object sender, EventArrivedEventArgs e)
    {
        // Small delay to let the system stabilize after plug/unplug
        Thread.Sleep(500);

        var ports = GetAvailablePorts();

        if (_syncContext is not null)
        {
            _syncContext.Post(_ => PortsChanged?.Invoke(this, ports), null);
        }
        else
        {
            PortsChanged?.Invoke(this, ports);
        }
    }
}
