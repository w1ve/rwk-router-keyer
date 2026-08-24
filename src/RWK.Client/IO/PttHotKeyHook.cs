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

namespace RWK.Client.IO;

/// <summary>
/// Global low-level keyboard hook that triggers PTT on a configurable hotkey combo.
/// The hotkey is momentary: PTT is asserted while the key combo is held, released when any
/// key in the combo is released.
/// </summary>
/// <remarks>
/// Uses the same WH_KEYBOARD_LL mechanism as <see cref="KeyboardPaddleInput"/> but captures
/// an arbitrary key combination (any single key plus optional modifiers: Ctrl, Shift, Alt).
/// The hook is enabled at pairing time and disabled on close or loss of pairing.
/// </remarks>
public sealed class PttHotKeyHook : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;
    private NativeKeyboardMethods.LowLevelKeyboardProc? _hookProc;
    private bool _running;
    private bool _disposed;
    private bool _capturing; // true while in "Set Hot Key" capture mode

    private volatile bool _pttActive;

    // The configured hotkey combo
    private Keys _hotKey = Keys.None;
    private bool _requireCtrl;
    private bool _requireShift;
    private bool _requireAlt;

    // Current modifier state tracked by the hook
    private volatile bool _ctrlDown;
    private volatile bool _shiftDown;
    private volatile bool _altDown;
    private volatile bool _mainKeyDown;

    /// <summary>Fired when PTT state changes (down = true, up = false).</summary>
    public event EventHandler<bool>? PttStateChanged;

    /// <summary>Fired when a hotkey combo is captured during "Set Hot Key" mode.</summary>
    public event EventHandler<PttHotKeyInfo>? HotKeyCaptured;

    /// <summary>Whether PTT is currently asserted via the hotkey.</summary>
    public bool IsPttActive => _pttActive;

    /// <summary>Whether the hook is actively running.</summary>
    public bool IsRunning => _running;

    /// <summary>Whether a hotkey is configured.</summary>
    public bool HasHotKey => _hotKey != Keys.None;

    /// <summary>Gets the currently configured hotkey info, or null if none.</summary>
    public PttHotKeyInfo? CurrentHotKey =>
        _hotKey == Keys.None ? null : new PttHotKeyInfo(_hotKey, _requireCtrl, _requireShift, _requireAlt);

    /// <summary>
    /// Sets the hotkey combo to listen for.
    /// </summary>
    public void SetHotKey(PttHotKeyInfo info)
    {
        _hotKey = info.Key;
        _requireCtrl = info.Ctrl;
        _requireShift = info.Shift;
        _requireAlt = info.Alt;
    }

    /// <summary>
    /// Enters capture mode: the next key combo pressed will be recorded as the hotkey.
    /// After capture, <see cref="HotKeyCaptured"/> fires and capture mode ends.
    /// </summary>
    public void StartCapture()
    {
        _capturing = true;
        if (!_running) Start();
    }

    /// <summary>
    /// Starts the global keyboard hook for PTT detection.
    /// </summary>
    public void Start()
    {
        if (_running) return;

        _hookProc = KeyboardHookCallback;
        _hookId = NativeKeyboardMethods.SetKeyboardHook(_hookProc);
        _running = true;
    }

    /// <summary>
    /// Stops the global keyboard hook and releases PTT if active.
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
        _capturing = false;

        if (_pttActive)
        {
            _pttActive = false;
            PttStateChanged?.Invoke(this, false);
        }

        _ctrlDown = false;
        _shiftDown = false;
        _altDown = false;
        _mainKeyDown = false;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            var info = Marshal.PtrToStructure<NativeKeyboardMethods.KBDLLHOOKSTRUCT>(lParam);
            Keys vk = (Keys)info.vkCode;

            bool isDown = msg == NativeKeyboardMethods.WM_KEYDOWN || msg == NativeKeyboardMethods.WM_SYSKEYDOWN;
            bool isUp = msg == NativeKeyboardMethods.WM_KEYUP || msg == NativeKeyboardMethods.WM_SYSKEYUP;

            // Track modifier state
            if (vk is Keys.LControlKey or Keys.RControlKey or Keys.ControlKey)
            {
                _ctrlDown = isDown;
            }
            else if (vk is Keys.LShiftKey or Keys.RShiftKey or Keys.ShiftKey)
            {
                _shiftDown = isDown;
            }
            else if (vk is Keys.LMenu or Keys.RMenu or Keys.Menu)
            {
                _altDown = isDown;
            }

            if (_capturing && isDown && !IsModifierKey(vk))
            {
                // Capture mode: record this combo as the hotkey
                var captured = new PttHotKeyInfo(vk, _ctrlDown, _shiftDown, _altDown);
                _hotKey = vk;
                _requireCtrl = _ctrlDown;
                _requireShift = _shiftDown;
                _requireAlt = _altDown;
                _capturing = false;
                HotKeyCaptured?.Invoke(this, captured);
                return NativeKeyboardMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            if (!_capturing && _hotKey != Keys.None)
            {
                // Normal PTT mode: check if the configured combo is pressed
                if (vk == _hotKey)
                {
                    if (isDown && !_mainKeyDown)
                    {
                        _mainKeyDown = true;
                        EvaluatePttState();
                    }
                    else if (isUp)
                    {
                        _mainKeyDown = false;
                        EvaluatePttState();
                    }
                }
                else if (IsModifierKey(vk))
                {
                    // Modifier change while main key may be held
                    EvaluatePttState();
                }
            }
        }

        return NativeKeyboardMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void EvaluatePttState()
    {
        bool shouldBeActive = _mainKeyDown
            && (!_requireCtrl || _ctrlDown)
            && (!_requireShift || _shiftDown)
            && (!_requireAlt || _altDown);

        if (shouldBeActive != _pttActive)
        {
            _pttActive = shouldBeActive;
            PttStateChanged?.Invoke(this, _pttActive);
        }
    }

    private static bool IsModifierKey(Keys vk)
        => vk is Keys.LControlKey or Keys.RControlKey or Keys.ControlKey
            or Keys.LShiftKey or Keys.RShiftKey or Keys.ShiftKey
            or Keys.LMenu or Keys.RMenu or Keys.Menu;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

/// <summary>
/// Represents a PTT hotkey combination.
/// </summary>
public sealed record PttHotKeyInfo(Keys Key, bool Ctrl, bool Shift, bool Alt)
{
    /// <summary>
    /// Returns a human-readable description like "Ctrl+Shift+P" or "F9".
    /// </summary>
    public string ToDisplayString()
    {
        var parts = new List<string>(4);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        parts.Add(KeyToFriendlyName(Key));
        return string.Join("+", parts);
    }

    /// <summary>
    /// Serializes to a compact string for persistence: "Key|Ctrl|Shift|Alt".
    /// </summary>
    public string Serialize() => $"{(int)Key}|{(Ctrl ? 1 : 0)}|{(Shift ? 1 : 0)}|{(Alt ? 1 : 0)}";

    /// <summary>
    /// Deserializes from the compact string format.
    /// </summary>
    public static PttHotKeyInfo? Deserialize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('|');
        if (parts.Length != 4) return null;
        if (!int.TryParse(parts[0], out int keyInt)) return null;
        return new PttHotKeyInfo(
            (Keys)keyInt,
            parts[1] == "1",
            parts[2] == "1",
            parts[3] == "1");
    }

    private static string KeyToFriendlyName(Keys key) => key switch
    {
        Keys.LControlKey => "LeftCtrl",
        Keys.RControlKey => "RightCtrl",
        Keys.LShiftKey => "LeftShift",
        Keys.RShiftKey => "RightShift",
        Keys.LMenu => "LeftAlt",
        Keys.RMenu => "RightAlt",
        Keys.OemPeriod => ".",
        Keys.Oemcomma => ",",
        Keys.OemQuestion => "/",
        Keys.OemSemicolon => ";",
        Keys.OemQuotes => "'",
        Keys.OemOpenBrackets => "[",
        Keys.OemCloseBrackets => "]",
        Keys.OemMinus => "-",
        Keys.Oemplus => "=",
        Keys.OemBackslash => "\\",
        Keys.OemPipe => "|",
        Keys.Space => "Space",
        Keys.Return => "Enter",
        Keys.Back => "Backspace",
        Keys.Escape => "Esc",
        Keys.Tab => "Tab",
        _ => key.ToString()
    };
}
