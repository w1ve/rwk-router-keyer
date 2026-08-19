/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Station.Replay;

/// <summary>
/// A fixed-capacity FIFO of value-typed items used on the Edge Replayer's inbound and pending
/// paths. Producers may be several threads; there is exactly one consumer, the replay thread.
/// </summary>
/// <remarks>
/// <para>
/// The point of this type is that neither enqueue nor dequeue allocates: storage is one array
/// allocated up front and items are copied into and out of its slots. A
/// <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/> would allocate segments while the
/// key is down, which is exactly what <c>GCLatencyMode.SustainedLowLatency</c> is trying to avoid on
/// this path (14.7).
/// </para>
/// <para>
/// <b>Synchronization.</b> Producers serialize among themselves on a short lock and publish the
/// tail with a release write. The consumer never takes that lock — it reads the published tail and
/// publishes its own head — so nothing on the TIME_CRITICAL replay thread can be made to wait on a
/// producer. Capacity is rounded up to a power of two so index wrapping is a mask.
/// </para>
/// <para>
/// Bounded on purpose: a full queue is reported to the caller rather than absorbed, because
/// unbounded buffering of key edges would mean replaying keying that is already far too late.
/// </para>
/// </remarks>
/// <typeparam name="T">A value type. Reference types are rejected so that enqueueing cannot extend
/// the lifetime of a graph the replayer does not own.</typeparam>
public sealed class ReplayRingBuffer<T>
    where T : struct
{
    private readonly object _producerGate = new();
    private readonly T[] _slots;
    private readonly int _mask;

    private long _head; // next index to read; owned by the consumer
    private long _tail; // next index to write; owned by the producers

    /// <summary>
    /// Creates a buffer holding at least <paramref name="capacity"/> items, rounded up to a power
    /// of two.
    /// </summary>
    public ReplayRingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        int rounded = 1;
        while (rounded < capacity)
        {
            rounded <<= 1;
        }

        _slots = new T[rounded];
        _mask = rounded - 1;
    }

    /// <summary>Number of items the buffer can hold.</summary>
    public int Capacity => _slots.Length;

    /// <summary>Items currently queued.</summary>
    public long Count => Volatile.Read(ref _tail) - Volatile.Read(ref _head);

    /// <summary>Whether the buffer currently holds no items.</summary>
    public bool IsEmpty => Count <= 0;

    /// <summary>
    /// Appends <paramref name="item"/>. Returns false when the buffer is full, in which case
    /// nothing is stored and the caller decides what to do about it.
    /// </summary>
    public bool TryEnqueue(in T item)
    {
        lock (_producerGate)
        {
            long tail = _tail;
            if (tail - Volatile.Read(ref _head) >= _slots.Length)
            {
                return false;
            }

            _slots[(int)(tail & _mask)] = item;

            // Release: the slot write above must be visible before the consumer can see the tail.
            Volatile.Write(ref _tail, tail + 1);
            return true;
        }
    }

    /// <summary>
    /// Reads the oldest item without removing it. Consumer-only.
    /// </summary>
    public bool TryPeek(out T item)
    {
        long head = _head;
        if (head >= Volatile.Read(ref _tail))
        {
            item = default;
            return false;
        }

        item = _slots[(int)(head & _mask)];
        return true;
    }

    /// <summary>
    /// Removes and returns the oldest item. Consumer-only.
    /// </summary>
    public bool TryDequeue(out T item)
    {
        long head = _head;
        if (head >= Volatile.Read(ref _tail))
        {
            item = default;
            return false;
        }

        item = _slots[(int)(head & _mask)];
        _slots[(int)(head & _mask)] = default; // do not keep a stale copy around
        Volatile.Write(ref _head, head + 1);
        return true;
    }

    /// <summary>
    /// Discards everything queued. Consumer-only; used when a fail-safe makes pending work
    /// meaningless.
    /// </summary>
    public void Clear()
    {
        long tail = Volatile.Read(ref _tail);
        for (long i = _head; i < tail; i++)
        {
            _slots[(int)(i & _mask)] = default;
        }

        Volatile.Write(ref _head, tail);
    }
}
