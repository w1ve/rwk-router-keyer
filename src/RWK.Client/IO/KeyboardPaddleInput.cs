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
using System.Runtime.InteropServices;
using RWK.Shared;
using RWK.Shared.IO;

namespace RWK.Client.IO;

/// <summary>
/// Keyboard-based paddle input for use when no physical paddle is available.
/// Uses a low-level keyboard hook to capture key-down/key-up events globally,
/// filtering out auto-repeat, and exposes the same <see cref="IPaddleInputPoller"/>
/// interface as the serial paddle poller.
/// </summary>
/// <remarks>
/// This allows operators to key CW using their keyboard while travelling without
/// a paddle. Key pairs are configurable (e.g. Z/X for left hand, comma/period for
/// right hand). Auto-repeat is explicitly filtered so that held keys produce clean
/// continuous contact closure — identical to a physical paddle lever.
/// </remarks>
public sealed class KeyboardPaddleInput : IPaddleInputPoller
{
    private IntPtr _hookId = IntPtr.Zero;
    private NativeKeyboardMethods.LowLevelKeyboardProc? _hookProc;
    private bool _running;
    private bool _disposed;

    private volatile bool _ditPressed;
    private volatile bool _dahPressed;

    private Keys _ditKey;
    private Keys _dahKey;

    /// <inheritdoc/>
    public event EventHandler<PaddleStateChangedEventArgs>? StateChanged;

    /// <inheritdoc/>
    public bool DitPressed => _ditPressed;

    /// <inheritdoc/>
    public bool DahPressed => _dahPressed;

    /// <inheritdoc/>
    public bool StraightKeyPressed => false; // Keyboard mode does not support straight key

    /// <inheritdoc/>
    public TimeSpan DebounceTime { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// Predefined key pair presets for the dropdown.
    /// </summary>
    public static readonly KeyPairPreset[] Presets = new[]
    {
        new KeyPairPreset("Left Ctrl / Right Ctrl", Keys.LControlKey, Keys.RControlKey),
        new KeyPairPreset("Left Shift / Right Shift", Keys.LShiftKey, Keys.RShiftKey),
        new KeyPairPreset("Z / X (left hand)", Keys.Z, Keys.X),
        new KeyPairPreset(", / . (right hand)", Keys.Oemcomma, Keys.OemPeriod),
        new KeyPairPreset("F / J (home row)", Keys.F, Keys.J),
        new KeyPairPreset("A / L (home row wide)", Keys.A, Keys.L),
        new KeyPairPreset("[ / ] (brackets)", Keys.OemOpenBrackets, Keys.OemCloseBrackets),
    };

    /// <summary>
    /// Gets or sets the key pair to use. Default: Left Ctrl / Right Ctrl.
    /// </summary>
    public KeyPairPreset ActivePreset { get; private set; } = Presets[0];

    /// <summary>
    /// Sets the active key pair from a preset.
    /// </summary>
    public void SetKeyPair(KeyPairPreset preset)
    {
        ActivePreset = preset;
        _ditKey = preset.DitKey;
        _dahKey = preset.DahKey;
    }

    /// <summary>
    /// Starts the keyboard hook. The <paramref name="portName"/> parameter is ignored
    /// (there is no serial port) — it exists only to satisfy the interface contract.
    /// </summary>
    public void Start(string portName)
    {
        if (_running) return;

        _ditKey = ActivePreset.DitKey;
        _dahKey = ActivePreset.DahKey;

        _hookProc = KeyboardHookCallback;
        _hookId = NativeKeyboardMethods.SetKeyboardHook(_hookProc);
        _running = true;
    }

    /// <summary>
    /// Stops the keyboard hook and releases both contacts.
    /// </summary>
    public void Stop()
    {
        if (!_running) return;

        if (_hookId != IntPtr.Zero)
        {
            NativeKeyboardMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        _hookProc = null;
        _running = false;

        // Release contacts
        bool changed = _ditPressed || _dahPressed;
        _ditPressed = false;
        _dahPressed = false;
        if (changed)
            RaiseStateChanged();
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            var info = Marshal.PtrToStructure<NativeKeyboardMethods.KBDLLHOOKSTRUCT>(lParam);
            Keys vk = (Keys)info.vkCode;

            // Filter: only handle our configured keys
            if (vk == _ditKey || vk == _dahKey)
            {
                bool isDown = msg == NativeKeyboardMethods.WM_KEYDOWN || msg == NativeKeyboardMethods.WM_SYSKEYDOWN;
                bool isUp = msg == NativeKeyboardMethods.WM_KEYUP || msg == NativeKeyboardMethods.WM_SYSKEYUP;

                if (isDown)
                {
                    // Filter auto-repeat: only react if state is changing
                    bool wasPressed = vk == _ditKey ? _ditPressed : _dahPressed;
                    if (!wasPressed)
                    {
                        if (vk == _ditKey) _ditPressed = true;
                        else _dahPressed = true;
                        RaiseStateChanged();
                    }
                    // Suppress auto-repeat (already pressed, ignore repeated WM_KEYDOWN)
                }
                else if (isUp)
                {
                    if (vk == _ditKey) _ditPressed = false;
                    else _dahPressed = false;
                    RaiseStateChanged();
                }
            }
        }

        return NativeKeyboardMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void RaiseStateChanged()
    {
        long qpc = Stopwatch.GetTimestamp();
        StateChanged?.Invoke(this, new PaddleStateChangedEventArgs(
            qpc, _ditPressed, _dahPressed, StraightKeyPressed: false));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

/// <summary>
/// A named key pair preset for the keyboard paddle dropdown.
/// </summary>
public sealed record KeyPairPreset(string DisplayName, Keys DitKey, Keys DahKey)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// P/Invoke for low-level keyboard hook.
/// </summary>
internal static class NativeKeyboardMethods
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    public static IntPtr SetKeyboardHook(LowLevelKeyboardProc proc)
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
    }
}
