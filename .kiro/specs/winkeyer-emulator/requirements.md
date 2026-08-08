# Requirements Document

## Introduction

This document specifies the requirements for a software emulation of the K1EL WinKeyer protocol, implemented as a .NET 9 WinForms desktop application. The emulator accepts WinKeyer commands via a virtual serial port or UDP socket, and keys a transmitter by toggling DTR or RTS on a physical serial port with sub-millisecond timing precision. The application targets experienced amateur radio operators who need a software-defined WinKeyer for CW (Morse code) operation.

## Glossary

- **Keyer_Core**: The central processing engine that interprets WinKeyer protocol commands and produces timed keying output
- **Keying_Port**: The physical serial port whose DTR or RTS line is toggled to key the transmitter
- **Command_Port**: A serial port that accepts incoming WinKeyer protocol commands from host applications
- **UDP_Listener**: A UDP socket endpoint that accepts WinKeyer protocol commands as if they were received on the Command_Port
- **Timing_Engine**: The high-precision timing subsystem responsible for scheduling DTR/RTS edge transitions with minimal jitter
- **Edge_Schedule**: A precomputed array of absolute timestamps representing key-down and key-up transitions for a Morse code message
- **PARIS_Weighting**: The standard timing scheme where one dit equals 1200/WPM milliseconds
- **EscapeCommFunction**: The Windows API call used to toggle DTR/RTS with minimal latency via a single IOCTL
- **SafeFileHandle**: A cached file handle to the keying serial port, avoiding repeated open/close overhead
- **Application**: The WinForms desktop application providing the user interface and orchestrating all subsystems
- **Port_Monitor**: The subsystem that detects serial port arrival and removal events and updates the UI accordingly
- **Logger**: The subsystem that records operational events to the UI log window

## Requirements

### Requirement 1: WinKeyer Protocol Interpretation

**User Story:** As a ham radio operator, I want the emulator to accept and execute the full K1EL WinKeyer command set, so that existing logging and contest software can control the keyer without modification.

#### Acceptance Criteria

1. WHEN a valid WinKeyer protocol command is received on the Command_Port, THE Keyer_Core SHALL parse and execute the command according to the K1EL WinKeyer specification
2. WHEN a valid WinKeyer protocol command is received on the UDP_Listener, THE Keyer_Core SHALL parse and execute the command identically to commands received on the Command_Port
3. WHEN an Admin Open command is received, THE Keyer_Core SHALL respond with the WinKeyer version byte and enter host mode
4. WHEN an Admin Close command is received, THE Keyer_Core SHALL exit host mode and cease keying output
5. WHEN a Speed command is received with a value between 5 and 45 WPM, THE Keyer_Core SHALL update the current WPM speed for subsequent keying operations
6. IF a Speed command is received with a value outside the range of 5 to 45 WPM, THEN THE Keyer_Core SHALL reject the value, retain the current speed, and log a warning via the Logger
6. WHEN text characters are received in host mode, THE Keyer_Core SHALL convert them to Morse code and queue them for transmission via the Timing_Engine
7. IF an unrecognized command byte is received, THEN THE Keyer_Core SHALL discard the byte and log a warning via the Logger
8. THE Keyer_Core SHALL support keying speeds from 5 WPM to 45 WPM with correct PARIS timing at all speeds including the maximum

### Requirement 2: High-Precision DTR/RTS Keying Output

**User Story:** As a ham radio operator, I want the keying output to have minimal timing jitter, so that my transmitted Morse code sounds clean and professional.

#### Acceptance Criteria

1. THE Timing_Engine SHALL toggle the selected keying line (DTR or RTS) on the Keying_Port using EscapeCommFunction with SETDTR/CLRDTR or SETRTS/CLRRTS IOCTL calls via a cached SafeFileHandle
2. THE Timing_Engine SHALL compute element timing using PARIS_Weighting where one dit equals 1200/WPM milliseconds
3. THE Timing_Engine SHALL precompute an Edge_Schedule of absolute timestamps (using Stopwatch.GetTimestamp) for all transitions in a queued message
4. THE Timing_Engine SHALL execute keying on a dedicated high-priority thread with GCLatencyMode.SustainedLowLatency enabled
5. THE Timing_Engine SHALL use a hybrid wait strategy combining Thread.Sleep for coarse waiting followed by spin-waiting for the final approach to each edge deadline
6. THE Timing_Engine SHALL call timeBeginPeriod(1) to set the system timer resolution to 1 ms during active keying
7. THE Timing_Engine SHALL open the Keying_Port with DTR_CONTROL_DISABLE and RTS_CONTROL_DISABLE in the DCB so the driver does not auto-assert the keying line
8. WHILE keying is active, THE Timing_Engine SHALL contribute less than 100 microseconds of scheduling jitter to each edge transition beyond irreducible USB latency, including at the maximum speed of 45 WPM where a dit element is approximately 26.7 milliseconds

### Requirement 3: Serial Command Port Input

**User Story:** As a ham radio operator, I want to connect my logging software to the emulator via a serial port, so that the emulator appears as a standard WinKeyer device.

#### Acceptance Criteria

1. WHEN the user selects a Command_Port and starts the emulator, THE Application SHALL open the selected serial port at the WinKeyer default baud rate of 1200 bps with 8N1 framing
2. WHILE the Command_Port is open, THE Application SHALL continuously read incoming bytes and forward them to the Keyer_Core for processing
3. WHILE the Command_Port is open, THE Application SHALL transmit WinKeyer status and response bytes back to the connected host application
4. IF the Command_Port becomes unavailable during operation, THEN THE Application SHALL stop the keyer, close the port, and log a disconnection event via the Logger

### Requirement 4: UDP Command Input

**User Story:** As a ham radio operator, I want to send WinKeyer commands over UDP, so that I can control the keyer from networked applications or scripts.

#### Acceptance Criteria

1. WHEN the user configures a UDP listen address and port and starts the emulator, THE UDP_Listener SHALL bind to the specified IP address and port
2. WHILE the UDP_Listener is active, THE UDP_Listener SHALL receive datagrams and forward their payload bytes to the Keyer_Core for processing identically to Command_Port data
3. WHEN the Keyer_Core produces a response byte, THE UDP_Listener SHALL transmit it back to the most recent source address and port
4. IF a UDP bind operation fails, THEN THE Application SHALL display an error message and log the failure via the Logger

### Requirement 5: Keying Port Configuration

**User Story:** As a ham radio operator, I want to select which serial port and which control line (DTR or RTS) is used for keying, so that I can match my station wiring.

#### Acceptance Criteria

1. THE Application SHALL present a dropdown of available serial ports for Keying_Port selection
2. THE Application SHALL present a selection control to choose between DTR and RTS as the keying line
3. WHEN the user changes the Keying_Port selection, THE Application SHALL close any previously opened Keying_Port before opening the new selection
4. WHILE the keyer is running, THE Application SHALL disable the Keying_Port and keying line selection controls to prevent changes during operation

### Requirement 6: Dynamic Serial Port Detection

**User Story:** As a ham radio operator, I want the port lists to update automatically when I plug in or remove USB serial adapters, so that I always see current hardware.

#### Acceptance Criteria

1. THE Port_Monitor SHALL detect serial port arrival and removal events using WMI or SetupAPI notifications
2. WHEN a serial port is added to the system, THE Port_Monitor SHALL update all port dropdown controls within 2 seconds
3. WHEN a serial port is removed from the system, THE Port_Monitor SHALL update all port dropdown controls within 2 seconds
4. IF a removed serial port is currently in use as the Keying_Port or Command_Port, THEN THE Application SHALL stop the keyer and log a disconnection event via the Logger

### Requirement 7: Application Start and Stop Control

**User Story:** As a ham radio operator, I want clear start and stop buttons, so that I can control when the emulator is actively listening for commands and keying.

#### Acceptance Criteria

1. WHEN the user clicks the Start button, THE Application SHALL open the configured Keying_Port, Command_Port, and UDP_Listener and begin processing commands
2. WHEN the user clicks the Stop button, THE Application SHALL cease keying, close all ports and the UDP_Listener, and return to idle state
3. WHILE the keyer is running, THE Application SHALL display the Stop button and hide the Start button
4. WHILE the keyer is idle, THE Application SHALL display the Start button and hide the Stop button
5. IF any port fails to open during start, THEN THE Application SHALL abort the start operation, close any ports that did open, display an error message, and remain in idle state

### Requirement 8: Operational Logging

**User Story:** As a ham radio operator, I want to see a log of what the emulator is doing, so that I can diagnose configuration problems and verify correct operation.

#### Acceptance Criteria

1. THE Logger SHALL display timestamped log entries in a multiline read-only text control within the Application window
2. WHEN a WinKeyer command is received, THE Logger SHALL record the command type and source (serial or UDP)
3. WHEN a keying operation begins, THE Logger SHALL record the text being sent and the WPM speed
4. WHEN an error occurs, THE Logger SHALL record the error description with a severity indicator
5. THE Logger SHALL limit the displayed log to the most recent 10000 lines to prevent excessive memory usage

### Requirement 9: Single-EXE Deployment

**User Story:** As a ham radio operator, I want to download a single executable file and run the application without an installer, so that deployment is simple.

#### Acceptance Criteria

1. THE Application SHALL be published as a self-contained single-file executable targeting Windows x64 using .NET 9 publish options
2. THE Application SHALL not require a separately installed .NET runtime on the target machine
3. THE Application SHALL function correctly when executed from any directory without additional configuration files

### Requirement 10: Testability and Test Project

**User Story:** As a developer, I want the code architecture to support automated testing, so that I can verify WinKeyer protocol handling and timing behavior without physical hardware.

#### Acceptance Criteria

1. THE Keyer_Core SHALL accept an interface abstraction for the keying output, allowing a test double to capture edge transitions without a physical serial port
2. THE Keyer_Core SHALL accept an interface abstraction for command input, allowing injection of test command sequences
3. THE Application solution SHALL include a test project that sends WinKeyer protocol commands via UDP to a running emulator instance and verifies correct responses
4. THE test project SHALL verify that Admin Open, Admin Close, Speed Set, and text buffer commands produce correct WinKeyer protocol responses
5. THE Keyer_Core SHALL be instantiable independently of the WinForms UI for unit testing purposes

### Requirement 11: USB Adapter Optimization

**User Story:** As a ham radio operator, I want the emulator to minimize USB-related timing variance, so that my keying quality is consistent regardless of adapter type.

#### Acceptance Criteria

1. WHEN the keyer starts, THE Application SHALL attempt to disable USB selective suspend for the Keying_Port device to eliminate wake-up latency penalties
2. THE Timing_Engine SHALL use absolute-deadline scheduling (never relative sleeps between edges) so that USB transfer latency on consecutive edges cancels rather than accumulates
3. THE Timing_Engine SHALL avoid calling SerialPort.DtrEnable or SerialPort.RtsEnable property setters, using only EscapeCommFunction to eliminate DCB round-trip overhead