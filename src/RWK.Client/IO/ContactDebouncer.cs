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

namespace RWK.Client.IO;

/// <summary>
/// The three paddle contacts, in the order used by <see cref="ContactDebouncer"/>'s internal
/// per-contact state.
/// </summary>
public enum PaddleContact
{
    /// <summary>Dit contact, driven by CTS (1.2).</summary>
    Dit = 0,

    /// <summary>Dah contact, driven by DSR (1.2).</summary>
    Dah = 1,

    /// <summary>Straight key contact, driven by DCD (1.2).</summary>
    StraightKey = 2
}

/// <summary>
/// A snapshot of the three paddle contact states.
/// </summary>
/// <param name="DitPressed">Dit contact closed.</param>
/// <param name="DahPressed">Dah contact closed.</param>
/// <param name="StraightKeyPressed">Straight key contact closed.</param>
public readonly record struct ContactStates(
    bool DitPressed,
    bool DahPressed,
    bool StraightKeyPressed)
{
    /// <summary>All contacts open.</summary>
    public static ContactStates None => default;

    /// <summary>Gets the state of a single contact.</summary>
    /// <param name="contact">The contact to read.</param>
    public bool this[PaddleContact contact] => contact switch
    {
        PaddleContact.Dit => DitPressed,
        PaddleContact.Dah => DahPressed,
        PaddleContact.StraightKey => StraightKeyPressed,
        _ => false
    };
}

/// <summary>
/// Pure software debounce state machine for the three paddle contacts (1.4).
/// </summary>
/// <remarks>
/// Each contact carries its own accepted state and the QPC timestamp at which that state was
/// accepted. A raw sample that differs from the accepted state is accepted only when at least
/// <see cref="DebounceTime"/> has elapsed since that contact's last accepted transition;
/// otherwise it is filtered as contact bounce. Contacts are entirely independent of one
/// another: a filtered dit bounce never delays a genuine dah transition.
/// <para>
/// No hardware, no clock, and no I/O: the caller supplies both the raw sample and the QPC
/// timestamp, which is what makes the debounce rule testable without a paddle. Timestamps are
/// raw QPC ticks, converted using <see cref="QpcFrequency"/>.
/// </para>
/// <para>
/// Not thread-safe for concurrent <see cref="TryAccept"/> calls — the polling thread is the
/// only caller. <see cref="DebounceTime"/> is the one member that may safely be written from
/// another thread (a UI settings change) while polling runs.
/// </para>
/// _Requirements: 1.4_
/// </remarks>
public sealed class ContactDebouncer
{
    /// <summary>Default debounce window required by 1.4.</summary>
    public static readonly TimeSpan DefaultDebounceTime = TimeSpan.FromMilliseconds(5);

    private const int ContactCount = 3;

    private readonly long _qpcFrequency;

    /// <summary>Debounce window in QPC ticks. Written/read atomically; see class remarks.</summary>
    private long _windowTicks;

    private readonly bool[] _accepted = new bool[ContactCount];
    private readonly long[] _lastAcceptedTicks = new long[ContactCount];
    private readonly bool[] _hasAccepted = new bool[ContactCount];

    /// <summary>
    /// Creates a debouncer.
    /// </summary>
    /// <param name="debounceTime">
    /// Debounce window. Defaults to <see cref="DefaultDebounceTime"/> (5 ms). Zero accepts
    /// every transition.
    /// </param>
    /// <param name="qpcFrequency">
    /// Ticks per second of the timestamps passed to <see cref="TryAccept"/>. Defaults to
    /// <see cref="Stopwatch.Frequency"/>. Tests pass an explicit value so timestamps can be
    /// written in convenient units.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="debounceTime"/> is negative, or <paramref name="qpcFrequency"/> is not
    /// positive.
    /// </exception>
    public ContactDebouncer(TimeSpan? debounceTime = null, long? qpcFrequency = null)
    {
        long frequency = qpcFrequency ?? Stopwatch.Frequency;
        if (frequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(qpcFrequency), frequency, "QPC frequency must be positive.");

        _qpcFrequency = frequency;
        DebounceTime = debounceTime ?? DefaultDebounceTime;
    }

    /// <summary>Gets the tick frequency used to convert <see cref="DebounceTime"/> to ticks.</summary>
    public long QpcFrequency => _qpcFrequency;

    /// <summary>
    /// Gets or sets the debounce window applied independently to each contact (1.4).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan DebounceTime
    {
        get => TimeSpan.FromSeconds((double)Volatile.Read(ref _windowTicks) / _qpcFrequency);
        set
        {
            if (value < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Debounce time cannot be negative.");

            Volatile.Write(ref _windowTicks, (long)(value.TotalSeconds * _qpcFrequency));
        }
    }

    /// <summary>
    /// Gets the currently accepted (debounced) contact states.
    /// </summary>
    public ContactStates AcceptedStates =>
        new(_accepted[(int)PaddleContact.Dit],
            _accepted[(int)PaddleContact.Dah],
            _accepted[(int)PaddleContact.StraightKey]);

    /// <summary>
    /// Offers one raw sample to the debouncer.
    /// </summary>
    /// <param name="raw">Raw contact states read from the modem status register.</param>
    /// <param name="qpcTimestamp">QPC tick count captured when the sample was read (1.3).</param>
    /// <param name="accepted">The accepted states after applying the debounce rule.</param>
    /// <returns>
    /// <see langword="true"/> when at least one contact transition was accepted, meaning the
    /// caller should emit a state change (1.5); <see langword="false"/> when the sample
    /// matched the accepted state or was filtered as bounce.
    /// </returns>
    public bool TryAccept(ContactStates raw, long qpcTimestamp, out ContactStates accepted)
    {
        long window = Volatile.Read(ref _windowTicks);
        bool changed = false;

        for (int i = 0; i < ContactCount; i++)
        {
            bool rawState = raw[(PaddleContact)i];
            if (rawState == _accepted[i])
                continue;

            // First-ever transition on this contact is always accepted; afterwards the
            // contact's own window governs, independently of the other two contacts.
            if (_hasAccepted[i] && qpcTimestamp - _lastAcceptedTicks[i] < window)
                continue;

            _accepted[i] = rawState;
            _lastAcceptedTicks[i] = qpcTimestamp;
            _hasAccepted[i] = true;
            changed = true;
        }

        accepted = AcceptedStates;
        return changed;
    }

    /// <summary>
    /// Clears all accepted state and debounce history, as though no sample had been seen.
    /// Called when the port is opened or closed so a stale window cannot suppress the first
    /// contact of a new session.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_accepted);
        Array.Clear(_lastAcceptedTicks);
        Array.Clear(_hasAccepted);
    }
}
