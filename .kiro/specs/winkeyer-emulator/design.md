# Design Document

## Overview

The WinKeyer Emulator is a .NET 9 WinForms desktop application that implements the K1EL WinKeyer protocol in software. The architecture separates concerns into a transport-agnostic protocol core, a high-precision timing engine, pluggable I/O transports, and a thin UI shell. This separation enables comprehensive unit and integration testing without physical hardware.

## Architecture

### High-Level Component Diagram

```
┌──────────────────────────────────────────────────────────────────┐
│                        WinForms UI Shell                          │
│  ┌────────────┐  ┌──────────┐  ┌──────────┐  ┌──────────────┐   │
│  │Port Dropdowns│  │Start/Stop│  │Log Window│  │Config Panel  │   │
│  └──────┬─────┘  └─────┬────┘  └─────▲────┘  └──────┬───────┘   │
└─────────┼───────────────┼─────────────┼──────────────┼───────────┘
          │               │             │              │
          ▼               ▼             │              ▼
┌─────────────────────────────────────────────────────────────────┐
│                      Application Controller                      │
│  (Orchestrates lifecycle, wires dependencies, routes events)     │
└──────┬──────────────┬───────────────┬────────────────┬──────────┘
       │              │               │                │
       ▼              ▼               ▼                ▼
┌────────────┐ ┌────────────┐ ┌─────────────┐ ┌──────────────┐
│ Keyer_Core │ │Timing_Engine│ │Port_Monitor │ │   Logger     │
│ (Protocol) │ │ (Precision) │ │(WMI/SetupAPI)│ │  (Events)    │
└──────┬─────┘ └──────┬─────┘ └─────────────┘ └──────────────┘
       │               │
       ▼               ▼
┌─────────────────────────────────────────┐
│           I/O Abstraction Layer          │
│  ┌──────────────┐  ┌────────────────┐   │
│  │ICommandSource│  │IKeyingOutput   │   │
│  └──────┬───────┘  └───────┬────────┘   │
└─────────┼───────────────────┼────────────┘
          │                   │
    ┌─────┴─────┐       ┌────┴────────────────┐
    │           │       │                     │
    ▼           ▼       ▼                     ▼
┌────────┐ ┌───────┐ ┌──────────────────┐ ┌──────────────┐
│Serial  │ │UDP    │ │SerialKeyingOutput│ │TestKeying    │
│Command │ │Command│ │(EscapeCommFunc)  │ │Output (Mock) │
│Source  │ │Source │ └──────────────────┘ └──────────────┘
└────────┘ └───────┘
```

### Project Structure

```
WinKeyerEmulator/
├── WinKeyerEmulator.sln
├── src/
│   ├── WinKeyerEmulator.Core/          # Class library - protocol, timing, abstractions
│   │   ├── Protocol/
│   │   │   ├── WinKeyerProtocol.cs     # Command parser and state machine
│   │   │   ├── CommandDefinitions.cs   # Command byte constants and structures
│   │   │   ├── MorseTable.cs           # Character-to-Morse lookup
│   │   │   └── ProtocolState.cs        # Host mode state tracking
│   │   ├── Timing/
│   │   │   ├── TimingEngine.cs         # High-precision edge scheduler
│   │   │   ├── EdgeScheduleBuilder.cs  # Precomputes absolute timestamps
│   │   │   ├── HybridWaiter.cs         # Sleep+spin wait implementation
│   │   │   └── ISystemClock.cs         # Abstraction for Stopwatch (testable)
│   │   ├── IO/
│   │   │   ├── IKeyingOutput.cs        # Interface for DTR/RTS toggling
│   │   │   ├── ICommandSource.cs       # Interface for command byte streams
│   │   │   ├── ICommandSink.cs         # Interface for response byte output
│   │   │   └── KeyingLine.cs           # Enum: DTR or RTS
│   │   └── KeyerCore.cs                # Orchestrates protocol + timing
│   │
│   └── WinKeyerEmulator.App/           # WinForms application
│       ├── Program.cs
│       ├── MainForm.cs
│       ├── MainForm.Designer.cs
│       ├── Controllers/
│       │   └── AppController.cs        # Lifecycle and dependency wiring
│       ├── IO/
│       │   ├── SerialKeyingOutput.cs   # EscapeCommFunction implementation
│       │   ├── SerialCommandSource.cs  # Serial port command reader
│       │   ├── UdpCommandSource.cs     # UDP listener implementation
│       │   └── NativeMethods.cs        # P/Invoke declarations
│       ├── Services/
│       │   ├── PortMonitor.cs          # WMI port detection
│       │   └── UsbPowerManager.cs      # Selective suspend control
│       └── Logging/
│           └── UILogger.cs             # Thread-safe UI log writer
│
└── tests/
    ├── WinKeyerEmulator.Core.Tests/    # Unit tests for protocol and timing
    │   ├── Protocol/
    │   │   ├── WinKeyerProtocolTests.cs
    │   │   ├── MorseTableTests.cs
    │   │   └── CommandParsingPropertyTests.cs
    │   ├── Timing/
    │   │   ├── EdgeScheduleBuilderTests.cs
    │   │   └── TimingPropertyTests.cs
    │   └── TestDoubles/
    │       ├── FakeKeyingOutput.cs
    │       ├── FakeClock.cs
    │       └── FakeCommandSource.cs
    │
    └── WinKeyerEmulator.Integration.Tests/  # UDP integration tests
        ├── UdpProtocolTests.cs
        ├── AdminCommandTests.cs
        └── TextBufferTests.cs
```

## Detailed Design

### 1. Interface Abstractions (IO Layer)

```csharp
// IKeyingOutput.cs - Abstraction for physical keying
public interface IKeyingOutput : IDisposable
{
    void Open(string portName, KeyingLine line);
    void Close();
    void KeyDown();   // Assert DTR/RTS
    void KeyUp();     // De-assert DTR/RTS
    bool IsOpen { get; }
}

// ICommandSource.cs - Abstraction for command byte input
public interface ICommandSource : IDisposable
{
    event EventHandler<byte[]> DataReceived;
    void Start();
    void Stop();
}

// ICommandSink.cs - Abstraction for response output
public interface ICommandSink
{
    void SendResponse(byte[] data);
}

// KeyingLine.cs
public enum KeyingLine { DTR, RTS }
```

### 2. Keyer Core (Protocol Engine)

The `KeyerCore` class is the heart of the application. It owns the WinKeyer protocol state machine and coordinates with the `TimingEngine` to produce keying output.

```csharp
public class KeyerCore : IDisposable
{
    private readonly IKeyingOutput _keyingOutput;
    private readonly TimingEngine _timingEngine;
    private readonly ILogger _logger;
    private ProtocolState _state;

    public KeyerCore(IKeyingOutput keyingOutput, TimingEngine timingEngine, ILogger logger)
    {
        _keyingOutput = keyingOutput;
        _timingEngine = timingEngine;
        _logger = logger;
        _state = new ProtocolState();
    }

    // Process incoming bytes from any command source
    public byte[]? ProcessCommand(ReadOnlySpan<byte> data) { ... }

    // Cancel current transmission
    public void AbortMessage() { ... }
}
```

Key design decisions:
- `ProcessCommand` returns response bytes (or null) allowing the transport layer to route replies
- The core has no knowledge of whether commands arrive via serial or UDP
- `ProtocolState` is a separate class enabling snapshot testing of state transitions

### 3. Timing Engine

The `TimingEngine` manages the dedicated keying thread and executes precomputed edge schedules.

```csharp
public class TimingEngine : IDisposable
{
    private readonly IKeyingOutput _keyingOutput;
    private readonly ISystemClock _clock;
    private Thread? _keyingThread;
    private CancellationTokenSource? _cts;
    private readonly BlockingCollection<long[]> _scheduleQueue;

    public void EnqueueMessage(string text, int wpm)
    {
        long[] schedule = EdgeScheduleBuilder.Build(text, wpm, _clock.Frequency);
        _scheduleQueue.Add(schedule);
    }

    private void KeyingLoop()
    {
        // Set thread priority, GC latency mode, timeBeginPeriod
        // Dequeue schedules and execute with HybridWaiter
    }
}
```

### 4. Edge Schedule Builder (Pure Function)

```csharp
public static class EdgeScheduleBuilder
{
    /// <summary>
    /// Builds an array of absolute tick offsets for all key-down/key-up edges.
    /// Even indices are key-down, odd indices are key-up.
    /// </summary>
    public static long[] Build(string text, int wpm, long tickFrequency)
    {
        long dit = tickFrequency * 1200L / (wpm * 1000L);
        long dah = 3 * dit;
        long intraCharGap = dit;       // Between elements within a character
        long interCharGap = 3 * dit;   // Between characters
        long wordGap = 7 * dit;        // Between words
        // ... build edge array with absolute offsets from t=0
    }
}
```

### 5. Serial Keying Output (Native Interop)

```csharp
public class SerialKeyingOutput : IKeyingOutput
{
    private SafeFileHandle? _handle;
    private KeyingLine _line;

    public void Open(string portName, KeyingLine line)
    {
        _line = line;
        // CreateFile with OPEN_EXISTING, set DCB with DTR/RTS CONTROL_DISABLE
        _handle = CreateFile($"\\\\.\\{portName}", ...);
        ConfigureDcb(_handle);
    }

    public void KeyDown()
    {
        uint func = _line == KeyingLine.DTR ? SETDTR : SETRTS;
        EscapeCommFunction(_handle!, func);
    }

    public void KeyUp()
    {
        uint func = _line == KeyingLine.DTR ? CLRDTR : CLRRTS;
        EscapeCommFunction(_handle!, func);
    }
}
```

### 6. Hybrid Waiter

```csharp
public static class HybridWaiter
{
    private const long SpinThresholdTicks = 15000; // ~1.5ms at 10MHz frequency

    public static void WaitUntil(long targetTimestamp, ISystemClock clock)
    {
        long remaining = targetTimestamp - clock.GetTimestamp();

        // Coarse sleep phase: sleep while > threshold away
        while (remaining > SpinThresholdTicks)
        {
            Thread.Sleep(1);
            remaining = targetTimestamp - clock.GetTimestamp();
        }

        // Spin phase: busy-wait for final precision
        while (clock.GetTimestamp() < targetTimestamp)
        {
            Thread.SpinWait(1);
        }
    }
}
```

### 7. UDP Command Source

```csharp
public class UdpCommandSource : ICommandSource, ICommandSink
{
    private UdpClient? _client;
    private IPEndPoint? _lastSender;
    private CancellationTokenSource? _cts;

    public event EventHandler<byte[]>? DataReceived;

    public void Start()
    {
        _client = new UdpClient(_endpoint);
        _cts = new CancellationTokenSource();
        Task.Run(() => ReceiveLoop(_cts.Token));
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await _client!.ReceiveAsync(ct);
            _lastSender = result.RemoteEndPoint;
            DataReceived?.Invoke(this, result.Buffer);
        }
    }

    public void SendResponse(byte[] data)
    {
        if (_lastSender != null)
            _client?.Send(data, data.Length, _lastSender);
    }
}
```

### 8. Port Monitor

Uses `ManagementEventWatcher` with WQL queries for `__InstanceCreationEvent` and `__InstanceDeletionEvent` on `Win32_SerialPort` to detect hot-plug events. Raises events on the UI thread via `SynchronizationContext`.

### 9. Application Controller

The `AppController` wires all components together and manages the start/stop lifecycle:

```csharp
public class AppController
{
    public void Start(AppConfig config)
    {
        // 1. Open keying port via IKeyingOutput
        // 2. Open command port (if configured)
        // 3. Start UDP listener (if configured)
        // 4. Wire DataReceived events to KeyerCore.ProcessCommand
        // 5. Start TimingEngine keying thread
        // 6. Attempt USB selective suspend disable
    }

    public void Stop()
    {
        // Reverse order: stop timing, close sources, close keying port
    }
}
```

### 10. WinForms UI (MainForm)

Simple single-form layout:
- **Top panel**: Port selection dropdowns (Keying Port, Command Port), DTR/RTS radio buttons, UDP IP/Port fields
- **Middle panel**: Start/Stop button, status indicator
- **Bottom panel**: Multiline read-only TextBox for log output

All UI updates are marshaled to the UI thread via `Control.Invoke` or `SynchronizationContext.Post`.

## Correctness Properties

### Property 1: Edge Schedule Monotonicity
For any valid text and WPM (5-99), all timestamps in the Edge_Schedule are strictly monotonically increasing.
```
∀ text ∈ ValidMorseText, wpm ∈ [5,45]:
  let schedule = EdgeScheduleBuilder.Build(text, wpm, freq)
  ∀ i ∈ [0, schedule.Length-2]: schedule[i] < schedule[i+1]
```

### Property 2: Edge Schedule Even Count
For any valid text, the Edge_Schedule always contains an even number of entries (each key-down has a matching key-up).
```
∀ text ∈ ValidMorseText, wpm ∈ [5,45]:
  let schedule = EdgeScheduleBuilder.Build(text, wpm, freq)
  schedule.Length % 2 == 0
```

### Property 3: PARIS Timing Accuracy
For any WPM, each dit element in the schedule spans exactly 1200/WPM ms worth of ticks, and each dah spans exactly 3× dit.
```
∀ wpm ∈ [5,45]:
  let ditTicks = freq * 1200 / (wpm * 1000)
  For single-element characters (E='.' T='-'):
    schedule[1] - schedule[0] == ditTicks  (for dit)
    schedule[1] - schedule[0] == 3*ditTicks (for dah)
```

### Property 4: Command Source Equivalence (Metamorphic)
For any valid WinKeyer command sequence, processing via serial and UDP sources produces identical Keyer_Core state transitions and response bytes.
```
∀ commands ∈ ValidCommandSequence:
  let (state1, responses1) = process(commands, via=Serial)
  let (state2, responses2) = process(commands, via=UDP)
  state1 == state2 ∧ responses1 == responses2
```

### Property 5: Protocol Round-Trip (Admin Open/Close)
For any sequence of operations between Admin Open and Admin Close, closing always returns the core to its initial idle state.
```
∀ operations ∈ ValidHostModeOperations:
  let core = new KeyerCore()
  core.ProcessCommand(AdminOpen)
  foreach op in operations: core.ProcessCommand(op)
  core.ProcessCommand(AdminClose)
  core.State == IdleState
```

### Property 6: Speed Command Idempotence
Setting the same speed twice produces the same state as setting it once.
```
∀ wpm ∈ [5,45]:
  let core1 = new KeyerCore(); core1.SetSpeed(wpm)
  let core2 = new KeyerCore(); core2.SetSpeed(wpm); core2.SetSpeed(wpm)
  core1.CurrentWpm == core2.CurrentWpm ∧ core1.State == core2.State
```

### Property 7: Invalid Commands Are Discarded
For any byte that is not a valid WinKeyer command in the current state, processing it does not change the protocol state.
```
∀ state ∈ ProtocolStates, byte ∈ InvalidCommandsForState(state):
  let stateBefore = core.State
  core.ProcessCommand(byte)
  core.State == stateBefore
```

### Property 8: Log Line Count Invariant
After any number of log entries, the displayed line count never exceeds 10000.
```
∀ n ∈ ℕ:
  let logger = new Logger(maxLines=10000)
  repeat n times: logger.Log("entry")
  logger.LineCount <= 10000
```

### Property 9: Morse Table Completeness
Every printable ASCII character that the WinKeyer protocol supports has a defined Morse encoding, and every encoding consists only of '.' and '-' characters.
```
∀ c ∈ SupportedCharacters:
  MorseTable.Contains(c) ∧
  MorseTable[c].All(ch => ch == '.' || ch == '-')
```

### Property 10: Schedule Builder Determinism
For the same text and WPM, the Edge_Schedule builder always produces identical output.
```
∀ text ∈ ValidMorseText, wpm ∈ [5,45]:
  EdgeScheduleBuilder.Build(text, wpm, freq) == EdgeScheduleBuilder.Build(text, wpm, freq)
```

## Deployment

### Build Configuration

```xml
<PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
</PropertyGroup>
```

### Publish Command

```bash
dotnet publish src/WinKeyerEmulator.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Testing Strategy

### Unit Tests (WinKeyerEmulator.Core.Tests)
- Protocol command parsing with property-based tests (FsCheck or similar)
- Edge schedule builder with property-based tests for monotonicity, even count, timing accuracy
- Morse table completeness property tests
- State machine transitions with example-based tests

### Integration Tests (WinKeyerEmulator.Integration.Tests)
- UDP client sends Admin Open → verifies version response
- UDP client sends Speed command → sends text → verifies status bytes
- UDP client sends Admin Close → verifies clean shutdown
- Multi-command sequences verifying full protocol flows

### Test Doubles
- `FakeKeyingOutput`: Records all KeyDown/KeyUp calls with timestamps for timing verification
- `FakeClock`: Controllable clock for deterministic timing tests
- `FakeCommandSource`: Programmatic command injection for unit tests
