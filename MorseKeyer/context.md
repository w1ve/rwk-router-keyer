# MorseTest Project Context

## Overview
A .NET 9 WinForms application that reads serial port control pins (CTS/DSR/DCD) and generates shaped morse code audio tones. Designed as a CW keyer for ham radio use.

## Project Location
`e:\AI2\Mumble\MumbleClientLib\samples\MorseTest\`

## Branch
`feature/morse-test`

## Files
- **MorseTest.csproj** - .NET 9 WinForms project, references NAudio and System.IO.Ports
- **Program.cs** - Entry point
- **MainForm.cs** - Main UI with COM port dropdown, audio device dropdown, pin mode selection, start/stop
- **AudioOutput.cs** - NAudio WASAPI output with `KeyedSineWaveProvider` for continuous streaming
- **SerialPinReader.cs** - P/Invoke for direct COM port pin reading via CreateFile/GetCommModemStatus
- **MorseGenerator.cs** - Shaped tone generation (copied from StereoTest, currently unused)
- **AppSettings.cs** - JSON persistence to %LocalAppData%\MorseTest\settings.json

## Key Design Decisions

### Audio Generation
- Uses `IWaveProvider` (`KeyedSineWaveProvider`) for continuous sample streaming
- NOT using buffered chunks (caused choppy audio)
- 2ms raised-cosine envelope attack/release for click-free keying without shortening dits
- 48kHz stereo float output via WASAPI

### Serial Port Polling
- Dedicated high-priority background thread (not Windows Forms Timer)
- Tight polling loop with SpinWait for sub-millisecond response
- UI updates throttled to 50ms to avoid lag
- Sets DTR and RTS high for loopback key detection

### Pin Monitoring Options
- CTS (typically looped from RTS)
- DSR (typically looped from DTR)
- DCD
- Invert option available

## Current Issues Being Debugged
- Dits still choppy at high WPM - may need further investigation of polling or audio latency

## Dependencies
- NAudio 2.2.1
- System.IO.Ports 9.0.0

## Build
```
dotnet build samples/MorseTest/MorseTest.csproj
```

## EXE Location
```
e:\AI2\Mumble\MumbleClientLib\samples\MorseTest\bin\Debug\net9.0-windows\MorseTest.exe
```
