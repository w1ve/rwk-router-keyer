# WinKeyer Emulator

A .NET 9 WinForms desktop application that implements the K1EL WinKeyer protocol in software. It presents a virtual serial port (or UDP endpoint) that logging and contest programs can connect to as if talking to a real WinKeyer, then drives a physical serial port's DTR/RTS line to key a transmitter.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (for building)
- Windows x64 (required for WinForms and native P/Invoke calls)

## Build

```bash
dotnet build WinKeyerEmulator.sln -c Release
```

## Test

```bash
dotnet test WinKeyerEmulator.sln
```

All tests (unit and integration) run without physical hardware.

## Publish (Single-File EXE)

```bash
dotnet publish src/WinKeyerEmulator.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The output is a single self-contained EXE at:

```
src/WinKeyerEmulator.App/bin/Release/net9.0-windows/win-x64/publish/WinKeyerEmulator.App.exe
```

No .NET runtime installation is needed on the target machine.

## Usage

1. Launch `WinKeyerEmulator.App.exe`.
2. Select the **Keying Port** (the physical serial port connected to your transmitter keying circuit).
3. Select the keying line (DTR or RTS).
4. Optionally configure a **Command Port** (virtual serial port) or **UDP endpoint** for your logging program to connect to.
5. Click **Start** to begin emulating.
6. Point your logging/contest software at the command port or UDP address.

## Known Limitations

- **Windows x64 only** — The application uses WinForms and Win32 P/Invoke (EscapeCommFunction, CreateFile) which are Windows-specific.
- **Physical serial port required for keying** — A real COM port (or USB-to-serial adapter) must be connected to key the transmitter. The emulator drives the DTR or RTS line directly.
- **USB selective suspend disable requires elevated privileges** — Disabling USB selective suspend on the keying port's hub (to prevent power-saving dropouts) may require running as Administrator.
- **WMI port monitoring may require admin rights** — Real-time detection of serial port hot-plug events via WMI (`Win32_SerialPort`) can require elevated privileges on some systems.
- **Subset of WinKeyer commands implemented** — Only the following commands are currently supported: Admin Open/Close, Speed, Text Buffer, and Clear Buffer. Additional WinKeyer commands (sidetone, weighting, PTT lead/tail, etc.) are not yet implemented.
- **`timeBeginPeriod` affects system-wide timer resolution** — The timing engine calls `timeBeginPeriod(1)` to improve Sleep() granularity. This lowers the system-wide timer resolution for all processes while the emulator is running, which can marginally increase power consumption.

## Architecture

The project is split into two assemblies:

| Project | Description |
|---------|-------------|
| `WinKeyerEmulator.Core` | Class library containing the protocol state machine, timing engine, edge schedule builder, and I/O abstractions. Fully testable without hardware. |
| `WinKeyerEmulator.App` | WinForms application with native I/O implementations, UI, and services (port monitor, USB power management). |

See `.kiro/specs/winkeyer-emulator/design.md` for detailed architecture documentation.

## License

This project is provided as-is for amateur radio experimentation.
