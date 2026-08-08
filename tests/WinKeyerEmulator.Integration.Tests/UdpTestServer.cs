using System.Net;
using System.Net.Sockets;
using WinKeyerEmulator.Core;
using WinKeyerEmulator.Core.IO;
using WinKeyerEmulator.Core.Timing;

namespace WinKeyerEmulator.Integration.Tests;

/// <summary>
/// Test helper that manages a KeyerCore instance with a UDP listener on a dynamic port.
/// Receives command datagrams, processes them through KeyerCore, and sends responses back.
/// </summary>
public sealed class UdpTestServer : IDisposable
{
    private readonly UdpClient _listener;
    private readonly KeyerCore _keyerCore;
    private readonly CancellationTokenSource _cts;
    private readonly Task _receiveTask;
    private IPEndPoint? _lastRemote;
    private bool _disposed;

    /// <summary>
    /// The local port the server is listening on (dynamically allocated).
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// The KeyerCore instance for inspecting protocol state in tests.
    /// </summary>
    public KeyerCore Core => _keyerCore;

    /// <summary>
    /// Creates and starts the UDP test server on a dynamically allocated port.
    /// </summary>
    public UdpTestServer()
    {
        // Bind to port 0 for dynamic allocation
        _listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_listener.Client.LocalEndPoint!).Port;

        // Create test doubles
        var clock = new FakeClock(autoAdvanceStep: 1000);
        var keyingOutput = new FakeKeyingOutput();
        var timingEngine = new TimingEngine(keyingOutput, clock);
        var logger = new NullLogger();

        _keyerCore = new KeyerCore(keyingOutput, timingEngine, logger);
        _keyerCore.ResponseAvailable += OnAsyncResponse;

        _cts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));
    }

    private void OnAsyncResponse(object? sender, byte[] data)
    {
        if (_lastRemote is not null)
        {
            try
            {
                _listener.Send(data, data.Length, _lastRemote);
            }
            catch { /* Best effort */ }
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _listener.ReceiveAsync(ct);
                _lastRemote = result.RemoteEndPoint;
                var response = _keyerCore.ProcessCommand(result.Buffer);
                if (response != null)
                {
                    await _listener.SendAsync(response, response.Length, result.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                if (!ct.IsCancellationRequested)
                    break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _listener.Close();
        _listener.Dispose();

        try { _receiveTask.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* Expected on cancellation */ }

        _keyerCore.Dispose();
        _cts.Dispose();
    }

    /// <summary>
    /// Simple fake keying output for test use.
    /// </summary>
    private sealed class FakeKeyingOutput : IKeyingOutput
    {
        public bool IsOpen { get; private set; }
        public void Open(string portName, KeyingLine line) => IsOpen = true;
        public void Close() => IsOpen = false;
        public void KeyDown() { }
        public void KeyUp() { }
        public void Dispose() => Close();
    }

    /// <summary>
    /// Simple fake clock for test use.
    /// </summary>
    private sealed class FakeClock : ISystemClock
    {
        private long _current;
        private readonly long _step;

        public long Frequency => 10_000_000L;

        public FakeClock(long autoAdvanceStep = 1000)
        {
            _step = autoAdvanceStep;
        }

        public long GetTimestamp()
        {
            var val = _current;
            _current += _step;
            return val;
        }
    }

    /// <summary>
    /// No-op logger for tests.
    /// </summary>
    private sealed class NullLogger : ILogger
    {
        public void Log(string message, LogSeverity severity, string? source = null) { }
    }
}
