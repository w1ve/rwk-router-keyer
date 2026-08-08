# Implementation Plan

## Overview

Implementation of the WinKeyer Emulator - a .NET 9 WinForms desktop application that emulates the K1EL WinKeyer protocol. Tasks are ordered by dependency: scaffold first, then core abstractions, then protocol/timing logic, then I/O implementations, then UI and integration.

## Tasks

- [x] 1. Solution and Project Scaffold
  Create the .NET 9 solution structure with all projects, NuGet references, and build configuration.
  Requirements: 9, 10
  - [x] 1.1 Create solution file `WinKeyerEmulator.sln` at workspace root
  - [x] 1.2 Create `src/WinKeyerEmulator.Core` class library project targeting `net9.0` with no platform-specific dependencies
  - [x] 1.3 Create `src/WinKeyerEmulator.App` WinForms project targeting `net9.0-windows` with PublishSingleFile, SelfContained, and win-x64 RuntimeIdentifier configured
  - [x] 1.4 Create `tests/WinKeyerEmulator.Core.Tests` xUnit test project with FsCheck.Xunit NuGet package for property-based testing
  - [x] 1.5 Create `tests/WinKeyerEmulator.Integration.Tests` xUnit test project with project reference to Core
  - [x] 1.6 Add project references: App references Core; both test projects reference Core
  - [x] 1.7 Verify solution builds successfully with `dotnet build`

- [x] 2. IO Interface Abstractions
  Define the core interfaces that decouple the protocol engine from physical hardware.
  Requirements: 10.1, 10.2, 5.2
  - [x] 2.1 Create `IKeyingOutput` interface in Core/IO with Open, Close, KeyDown, KeyUp, IsOpen members
  - [x] 2.2 Create `ICommandSource` interface in Core/IO with DataReceived event, Start, Stop methods
  - [x] 2.3 Create `ICommandSink` interface in Core/IO with SendResponse method
  - [x] 2.4 Create `KeyingLine` enum (DTR, RTS) in Core/IO
  - [x] 2.5 Create `ISystemClock` interface in Core/Timing with GetTimestamp and Frequency properties
  - [x] 2.6 Create `ILogger` interface in Core with Log method accepting message, severity, and optional source

- [x] 3. Morse Table and Character Encoding
  Implement the Morse code lookup table supporting all WinKeyer-supported characters.
  Requirements: 1.6
  - [x] 3.1 Create `MorseTable` static class in Core/Protocol with a dictionary mapping characters to dit/dah strings
  - [x] 3.2 Include all 26 letters, 10 digits, and standard punctuation (. , ? / - = ' ! ( ) & : ; + _ " @ $)
  - [x] 3.3 Include prosigns: AR, BT, SK as special multi-character entries
  - [x] 3.4 Create `MorseTable.TryGetPattern(char c, out string pattern)` method
  - [x] 3.5 Write property test: all entries contain only '.' and '-' characters [PBT]
  - [x] 3.6 Write property test: all supported WinKeyer characters have entries in the table [PBT]
  - [x] 3.7 Write example tests for known Morse patterns (E='.', T='-', SOS='...---...')

- [x] 4. Edge Schedule Builder
  Implement the pure-function schedule builder that converts text and WPM into absolute timestamp arrays.
  Requirements: 2.2, 2.3
  - [x] 4.1 Create `EdgeScheduleBuilder` static class in Core/Timing
  - [x] 4.2 Implement `Build(string text, int wpm, long tickFrequency)` returning `long[]` of absolute tick offsets
  - [x] 4.3 Implement PARIS timing: dit = tickFrequency * 1200 / (wpm * 1000), dah = 3*dit, intra-char gap = dit, inter-char gap = 3*dit, word gap = 7*dit
  - [x] 4.4 Ensure even-indexed entries are key-down, odd-indexed are key-up
  - [x] 4.5 Handle spaces between words as 7-dit gaps (no keying edges for spaces)
  - [x] 4.6 Write property test: output is strictly monotonically increasing for all valid inputs (wpm 5-45, non-empty valid text) [PBT]
  - [x] 4.7 Write property test: output always has even length [PBT]
  - [x] 4.8 Write property test: for single-dit character 'E', edge[1]-edge[0] == ditTicks exactly [PBT]
  - [x] 4.9 Write property test: for single-dah character 'T', edge[1]-edge[0] == 3*ditTicks exactly [PBT]
  - [x] 4.10 Write property test: same input always produces same output (determinism) [PBT]
  - [x] 4.11 Write example test: "PARIS" at 20 WPM produces expected number of edges and total duration

- [x] 5. Hybrid Waiter
  Implement the sleep+spin wait mechanism for sub-millisecond timing precision.
  Requirements: 2.5
  - [x] 5.1 Create `HybridWaiter` static class in Core/Timing
  - [x] 5.2 Implement `WaitUntil(long targetTimestamp, ISystemClock clock)` with Thread.Sleep phase and SpinWait phase
  - [x] 5.3 Define spin threshold constant (~1.5ms worth of ticks, calibrated to clock frequency)
  - [x] 5.4 Create `StopwatchClock` implementation of `ISystemClock` wrapping `Stopwatch.GetTimestamp()` and `Stopwatch.Frequency`
  - [x] 5.5 Create `FakeClock` test double with controllable timestamps
  - [x] 5.6 Write unit test: WaitUntil with FakeClock returns after target is reached

- [x] 6. WinKeyer Protocol State Machine
  Implement the WinKeyer protocol parser and state machine handling Admin, Speed, and text buffer commands.
  Requirements: 1
  - [x] 6.1 Create `ProtocolState` class in Core/Protocol with HostMode flag, CurrentWpm, BufferState
  - [x] 6.2 Create `CommandDefinitions` static class with WinKeyer command byte constants (Admin=0x00, immediate commands 0x01-0x1F, etc.)
  - [x] 6.3 Create `WinKeyerProtocol` class in Core/Protocol with `ProcessByte(byte b)` method returning response bytes or null
  - [x] 6.4 Implement Admin command handling: Open (0x00 0x02) responds with version, Close (0x00 0x03) exits host mode
  - [x] 6.5 Implement Speed command (0x02 + speed byte) with validation for 5-45 WPM range
  - [x] 6.6 Implement text buffer: in host mode, non-command bytes (0x20-0x7E) are queued as text characters
  - [x] 6.7 Implement Clear Buffer command (0x0A) to cancel pending text
  - [x] 6.8 Implement status byte reporting (idle, sending, buffer space available)
  - [x] 6.9 Reject speed values outside 5-45 range and log warning
  - [x] 6.10 Write property test: Admin Open followed by any valid operations followed by Admin Close returns to idle state [PBT]
  - [x] 6.11 Write property test: setting same speed twice produces same state as once (idempotence) [PBT]
  - [x] 6.12 Write property test: invalid command bytes in any state do not change protocol state [PBT]
  - [x] 6.13 Write example tests: Admin Open response contains correct version byte
  - [x] 6.14 Write example tests: Speed set to 25 WPM followed by text produces correct status transitions

- [x] 7. Keyer Core Orchestration
  Implement the KeyerCore class that wires protocol parsing to timing engine execution.
  Requirements: 1.6, 10.5
  - [x] 7.1 Create `KeyerCore` class in Core with constructor accepting IKeyingOutput, TimingEngine, ILogger
  - [x] 7.2 Implement `ProcessCommand(ReadOnlySpan<byte> data)` that delegates to WinKeyerProtocol and routes text to TimingEngine
  - [x] 7.3 Implement `AbortMessage()` that cancels current transmission and clears buffer
  - [x] 7.4 Expose `ProtocolState` for test inspection
  - [x] 7.5 Create `FakeKeyingOutput` test double that records KeyDown/KeyUp calls with timestamps
  - [x] 7.6 Write unit test: processing text in host mode enqueues message to TimingEngine
  - [x] 7.7 Write unit test: KeyerCore instantiates without any UI dependencies

- [x] 8. Timing Engine Thread Management
  Implement the dedicated keying thread with high-priority scheduling, GC latency mode, and timeBeginPeriod.
  Requirements: 2.4, 2.6, 2.8
  - [x] 8.1 Create `TimingEngine` class in Core/Timing with constructor accepting IKeyingOutput and ISystemClock
  - [x] 8.2 Implement `Start()` that creates dedicated Thread with ThreadPriority.Highest
  - [x] 8.3 Implement keying loop that dequeues schedules from BlockingCollection and executes via HybridWaiter
  - [x] 8.4 Set GCLatencyMode.SustainedLowLatency on the keying thread
  - [x] 8.5 Implement `EnqueueMessage(string text, int wpm)` that builds schedule and adds to queue
  - [x] 8.6 Implement `Stop()` with clean cancellation and thread join
  - [x] 8.7 Implement `AbortCurrent()` to interrupt in-progress keying
  - [x] 8.8 Add P/Invoke for timeBeginPeriod/timeEndPeriod in NativeMethods (App project)
  - [x] 8.9 Call timeBeginPeriod(1) on thread start and timeEndPeriod(1) on thread stop
  - [x] 8.10 Write unit test with FakeClock and FakeKeyingOutput: verify edges fire in correct order

- [x] 9. Native Serial Port Implementation
  Implement SerialKeyingOutput using CreateFile and EscapeCommFunction for minimal-latency DTR/RTS toggling.
  Requirements: 2.1, 2.7, 11.3
  - [x] 9.1 Create `NativeMethods` static class in App/IO with P/Invoke declarations for CreateFile, EscapeCommFunction, SetCommState, CloseHandle
  - [x] 9.2 Define constants: SETDTR, CLRDTR, SETRTS, CLRRTS, DTR_CONTROL_DISABLE, RTS_CONTROL_DISABLE
  - [x] 9.3 Create `SerialKeyingOutput` class implementing IKeyingOutput
  - [x] 9.4 Implement `Open`: CreateFile with `\\\\.\\COMx`, configure DCB with manual DTR/RTS control
  - [x] 9.5 Implement `KeyDown`/`KeyUp` using EscapeCommFunction with cached SafeFileHandle
  - [x] 9.6 Implement `Close` with proper handle disposal
  - [x] 9.7 Implement `IsOpen` property checking handle validity

- [x] 10. Serial Command Source
  Implement the serial port reader that accepts incoming WinKeyer commands from host software.
  Requirements: 3
  - [x] 10.1 Create `SerialCommandSource` class in App/IO implementing ICommandSource and ICommandSink
  - [x] 10.2 Implement `Start(string portName)` opening port at 1200 baud, 8N1
  - [x] 10.3 Implement continuous byte reading on background thread raising DataReceived event
  - [x] 10.4 Implement `SendResponse(byte[] data)` writing bytes back to host
  - [x] 10.5 Implement `Stop()` with clean port closure
  - [x] 10.6 Handle port disconnection (IOException) by raising a Disconnected event and stopping

- [x] 11. UDP Command Source
  Implement the UDP listener that accepts WinKeyer commands over the network.
  Requirements: 4
  - [x] 11.1 Create `UdpCommandSource` class in App/IO implementing ICommandSource and ICommandSink
  - [x] 11.2 Implement `Start(IPEndPoint endpoint)` binding UdpClient to configured address/port
  - [x] 11.3 Implement async receive loop raising DataReceived with datagram payload
  - [x] 11.4 Track last sender endpoint for response routing
  - [x] 11.5 Implement `SendResponse(byte[] data)` sending to last sender
  - [x] 11.6 Implement `Stop()` with clean UdpClient disposal
  - [x] 11.7 Handle SocketException on bind failure with descriptive error event

- [x] 12. Port Monitor
  Implement WMI-based serial port hot-plug detection.
  Requirements: 6
  - [x] 12.1 Create `PortMonitor` class in App/Services
  - [x] 12.2 Implement WMI ManagementEventWatcher for `__InstanceCreationEvent` on serial port devices
  - [x] 12.3 Implement WMI ManagementEventWatcher for `__InstanceDeletionEvent` on serial port devices
  - [x] 12.4 Raise `PortsChanged` event with current list of available port names
  - [x] 12.5 Marshal events to UI thread via SynchronizationContext
  - [x] 12.6 Implement `GetAvailablePorts()` returning current SerialPort.GetPortNames() result
  - [x] 12.7 Implement `Dispose()` stopping watchers

- [x] 13. USB Power Manager
  Implement USB selective suspend disable for the keying port device.
  Requirements: 11.1
  - [x] 13.1 Create `UsbPowerManager` static class in App/Services
  - [x] 13.2 Implement `TryDisableSelectiveSuspend(string portName)` using SetupAPI/registry to locate device and modify power settings
  - [x] 13.3 Log success or failure via ILogger (best-effort, do not throw on failure)

- [x] 14. Logger Implementation
  Implement thread-safe UI logging with line count limiting.
  Requirements: 8
  - [x] 14.1 Create `UILogger` class in App/Logging implementing ILogger
  - [x] 14.2 Implement thread-safe log append using Control.BeginInvoke to marshal to UI thread
  - [x] 14.3 Include timestamp, severity, and source in each log line format
  - [x] 14.4 Implement 10000 line cap by trimming oldest lines when limit exceeded
  - [x] 14.5 Write property test: after N log entries (N > 10000), line count never exceeds 10000 [PBT]
  - [x] 14.6 Write example test: log entries include timestamp and severity

- [x] 15. Application Controller
  Implement the lifecycle orchestrator that wires all components and manages start/stop.
  Requirements: 7
  - [x] 15.1 Create `AppController` class in App/Controllers
  - [x] 15.2 Implement `Start(AppConfig config)` that opens keying port, command port, UDP listener in order
  - [x] 15.3 Wire DataReceived events from command sources to KeyerCore.ProcessCommand
  - [x] 15.4 Route KeyerCore response bytes to appropriate ICommandSink
  - [x] 15.5 Implement `Stop()` reversing start order: stop timing, close sources, close keying
  - [x] 15.6 Implement error handling: if any port fails during start, close previously opened resources and report error
  - [x] 15.7 Expose `IsRunning` property for UI binding
  - [x] 15.8 Raise `Stopped` event when keyer stops (including on port disconnection)

- [x] 16. WinForms Main Form UI
  Implement the WinForms application shell with all controls.
  Requirements: 5, 6, 7, 8
  - [x] 16.1 Create `MainForm` with designer layout: top config panel, middle controls, bottom log
  - [x] 16.2 Add Keying Port dropdown (ComboBox) and DTR/RTS radio buttons
  - [x] 16.3 Add Command Port dropdown (ComboBox) with "None" option
  - [x] 16.4 Add UDP IP TextBox and Port NumericUpDown fields
  - [x] 16.5 Add Start/Stop button with visibility toggling based on state
  - [x] 16.6 Add multiline read-only TextBox for log output
  - [x] 16.7 Wire PortMonitor.PortsChanged to update all dropdown controls
  - [x] 16.8 Wire Start button click to AppController.Start with current config
  - [x] 16.9 Wire Stop button click to AppController.Stop
  - [x] 16.10 Disable configuration controls while running, re-enable on stop
  - [x] 16.11 Handle PortMonitor disconnection of active port: auto-stop and show notification

- [x] 17. Program Entry Point
  Create the application entry point with proper initialization.
  Requirements: 9
  - [x] 17.1 Create `Program.cs` with ApplicationConfiguration.Initialize() and Application.Run(new MainForm())
  - [x] 17.2 Set application to use visual styles and compatible text rendering
  - [x] 17.3 Verify application launches and displays MainForm correctly

- [x] 18. UDP Integration Tests
  Build the integration test suite that validates WinKeyer protocol over UDP.
  Requirements: 10.3, 10.4
  - [x] 18.1 Create test helper class that manages a KeyerCore instance with UdpCommandSource on a test port
  - [x] 18.2 Create UDP test client helper that sends commands and receives responses with timeout
  - [x] 18.3 Write test: Admin Open command returns correct version byte
  - [x] 18.4 Write test: Admin Close after Open returns to idle (no further responses to text)
  - [x] 18.5 Write test: Speed Set to 20 WPM followed by text buffer produces status byte responses
  - [x] 18.6 Write test: Clear Buffer command stops pending text transmission
  - [x] 18.7 Write test: multiple commands in sequence maintain correct protocol state
  - [x] 18.8 Write test: invalid command bytes are ignored without breaking session

- [x] 19. End-to-End Verification
  Verify the complete application builds, publishes, and the single-EXE functions correctly.
  Requirements: 9
  - [x] 19.1 Run `dotnet build` on entire solution and verify zero errors
  - [x] 19.2 Run `dotnet test` on entire solution and verify all tests pass
  - [x] 19.3 Run `dotnet publish` with single-file configuration and verify output is a single EXE
  - [x] 19.4 Verify published EXE size is reasonable (< 100MB)
  - [x] 19.5 Document any known limitations or hardware-specific notes in a README.md

## Task Dependency Graph

```
1 (Scaffold)
├── 2 (IO Interfaces) → depends on 1
├── 3 (Morse Table) → depends on 1
├── 5 (Hybrid Waiter) → depends on 2
├── 4 (Edge Schedule Builder) → depends on 2, 3
├── 6 (Protocol State Machine) → depends on 2, 3
├── 8 (Timing Engine) → depends on 4, 5
├── 7 (Keyer Core) → depends on 6, 8
├── 9 (Serial Keying Output) → depends on 2
├── 10 (Serial Command Source) → depends on 2
├── 11 (UDP Command Source) → depends on 2
├── 12 (Port Monitor) → depends on 1
├── 13 (USB Power Manager) → depends on 2
├── 14 (Logger) → depends on 2
├── 15 (App Controller) → depends on 7, 9, 10, 11, 12, 13, 14
├── 16 (Main Form UI) → depends on 15, 12, 14
├── 17 (Program Entry Point) → depends on 16
├── 18 (UDP Integration Tests) → depends on 7, 11
├── 19 (End-to-End Verification) → depends on 17, 18
```

## Notes

- Tasks 2, 3, 5, 9, 10, 11, 12 can proceed in parallel once Task 1 completes
- Property-based tests (marked [PBT]) use FsCheck.Xunit
- Hardware-dependent tasks (9, 10, 12, 13) can only be fully verified with physical serial ports
- The WinForms UI (Task 16) is intentionally simple - single form, no MVVM framework
