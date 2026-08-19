/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Shared.Keying;

/// <summary>
/// The mode-dependent element decision state machine: paddle contacts and dit/dah
/// memory in, one <see cref="KeyerElement"/> out (3.1-3.6).
/// </summary>
/// <remarks>
/// Ported from the RWK v1 <c>WinKeyerEmulator.Core.Keying.SoftKeyer</c>. What carried
/// over verbatim is the part v1 got right: the Iambic A/B, Ultimatic, and Bug decision
/// logic and the dit/dah memory that records a tap made during the previous element.
/// <para>
/// Three things were deliberately left behind:
/// </para>
/// <list type="bullet">
///   <item><description>
///     The pattern buffer and the inverted Morse table that decoded elements back into
///     ASCII. v2 sends edges to the Station, so nothing downstream needs a decoded
///     character, and the letter/word gap heuristics that fed the decoder were the
///     source of v1's timing-sensitive behavior.
///   </description></item>
///   <item><description>
///     The internal timing thread and its <c>Thread.Sleep</c>/<c>WaitMs</c> element
///     waits. This type never blocks: the caller asks for an element and is responsible
///     for scheduling it (see <see cref="KeyerElementPump"/>), so timing comes from the
///     QPC scheduler instead of from sleeping inside the state machine.
///   </description></item>
///   <item><description>
///     The ASCII-emitting transport coupling (the <c>CharacterDecoded</c> event).
///   </description></item>
/// </list>
/// <para>
/// <see cref="KeyerMode.Straight"/> is new in v2 — v1 had no straight-key mode. It
/// generates no elements at all (3.6); the contact is passed through by the caller,
/// which is why <see cref="RequestNextElement"/> always returns
/// <see cref="KeyerElement.None"/> in that mode.
/// </para>
/// <para>
/// Not thread-safe for concurrent mutation. The intended arrangement is one producer
/// (the paddle poller calling <see cref="SetPaddleState"/>) and one consumer (the keyer
/// thread calling <see cref="RequestNextElement"/>); paddle state is stored in volatile
/// fields so the consumer observes presses promptly, matching v1.
/// </para>
/// _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6_
/// </remarks>
public sealed class KeyerElementEngine
{
    private volatile bool _ditPressed;
    private volatile bool _dahPressed;
    private volatile bool _straightPressed;
    private volatile bool _ditMemory;
    private volatile bool _dahMemory;
    private volatile KeyerMode _mode = KeyerMode.IambicB;

    private KeyerElement _lastElement = KeyerElement.None;

    /// <summary>
    /// Gets or sets the keying mode (3.1).
    /// </summary>
    public KeyerMode Mode
    {
        get => _mode;
        set => _mode = value;
    }

    /// <summary>Gets whether the dit contact is currently closed.</summary>
    public bool DitPressed => _ditPressed;

    /// <summary>Gets whether the dah contact is currently closed.</summary>
    public bool DahPressed => _dahPressed;

    /// <summary>Gets whether the straight-key contact is currently closed.</summary>
    public bool StraightPressed => _straightPressed;

    /// <summary>
    /// Gets whether a dit tap is remembered from during the previous element.
    /// </summary>
    public bool DitMemory => _ditMemory;

    /// <summary>
    /// Gets whether a dah tap is remembered from during the previous element.
    /// </summary>
    public bool DahMemory => _dahMemory;

    /// <summary>
    /// Gets the element most recently returned by <see cref="RequestNextElement"/>,
    /// which drives squeeze alternation.
    /// </summary>
    public KeyerElement LastElement => _lastElement;

    /// <summary>
    /// Applies a debounced paddle state, recording dit/dah memory on press transitions.
    /// </summary>
    /// <remarks>
    /// Memory is set on the released-to-pressed transition only, exactly as v1 did in its
    /// <c>DitPressed</c>/<c>DahPressed</c> setters. That is what makes a tap during an
    /// in-progress element survive to the next element decision.
    /// </remarks>
    /// <param name="dit">Dit contact closed.</param>
    /// <param name="dah">Dah contact closed.</param>
    /// <param name="straight">Straight-key contact closed.</param>
    public void SetPaddleState(bool dit, bool dah, bool straight)
    {
        bool ditWasPressed = _ditPressed;
        bool dahWasPressed = _dahPressed;

        _ditPressed = dit;
        _dahPressed = dah;
        _straightPressed = straight;

        if (dit && !ditWasPressed)
            _ditMemory = true;

        if (dah && !dahWasPressed)
            _dahMemory = true;
    }

    /// <summary>
    /// Clears remembered taps without disturbing the current contact state.
    /// </summary>
    /// <remarks>
    /// Needed when the caller abandons whatever the memory was queued for — a mode
    /// change, or an abort — so that a tap recorded under the old regime does not
    /// surface as a spurious element later. Contacts are left alone because they
    /// describe physical reality, which an abort does not change.
    /// </remarks>
    public void ClearMemory()
    {
        _ditMemory = false;
        _dahMemory = false;
    }

    /// <summary>
    /// Clears paddle state, memory, and alternation history.
    /// </summary>
    public void Reset()
    {
        _ditPressed = false;
        _dahPressed = false;
        _straightPressed = false;
        _ditMemory = false;
        _dahMemory = false;
        _lastElement = KeyerElement.None;
    }

    /// <summary>
    /// Decides and commits the next element to send.
    /// </summary>
    /// <remarks>
    /// Committing means the memory flag for the chosen element is consumed and
    /// <see cref="LastElement"/> is updated, so calling this twice without an
    /// intervening <see cref="SetPaddleState"/> can legitimately return two different
    /// elements (that is squeeze alternation). Returns
    /// <see cref="KeyerElement.None"/> when nothing is wanted — the caller should idle
    /// briefly and ask again rather than busy-spin.
    /// </remarks>
    /// <returns>The element to send, or <see cref="KeyerElement.None"/> if idle.</returns>
    public KeyerElement RequestNextElement()
    {
        KeyerElement element = DetermineNextElement();
        if (element == KeyerElement.None)
            return KeyerElement.None;

        // Clear the memory for the element we are about to send. Memory captures taps
        // made during element generation; it is not an auto-repeat latch.
        if (element == KeyerElement.Dit)
            _ditMemory = false;
        else
            _dahMemory = false;

        _lastElement = element;
        return element;
    }

    /// <summary>
    /// The mode-specific decision, ported from v1 <c>SoftKeyer.DetermineNextElement</c>.
    /// </summary>
    private KeyerElement DetermineNextElement()
    {
        // Check both current paddle state AND memory.
        // Memory captures "paddle was tapped during previous element";
        // current state captures "paddle is still held down".
        bool ditWanted = _ditPressed || _ditMemory;
        bool dahWanted = _dahPressed || _dahMemory;

        switch (_mode)
        {
            case KeyerMode.Straight:
                // Straight key generates no elements; the contact is passed through
                // by the caller (3.6).
                return KeyerElement.None;

            case KeyerMode.Bug:
                // Bug: dit paddle auto-repeats while held, dah is single-shot per press.
                if (_ditPressed)
                    return KeyerElement.Dit;
                if (_dahMemory)
                {
                    // Single dah per press - clear memory immediately.
                    _dahMemory = false;
                    return KeyerElement.Dah;
                }
                return KeyerElement.None;
        }

        if (!ditWanted && !dahWanted)
            return KeyerElement.None;

        switch (_mode)
        {
            case KeyerMode.IambicB:
                // Iambic B: alternate when both are pressed/wanted, otherwise send what is
                // wanted. Memory persists through the inter-element gap for a squeeze, so a
                // release during an element still yields the queued opposite element (3.2).
                if (ditWanted && dahWanted)
                    return _lastElement == KeyerElement.Dit ? KeyerElement.Dah : KeyerElement.Dit;
                return ditWanted ? KeyerElement.Dit : KeyerElement.Dah;

            case KeyerMode.IambicA:
                // Iambic A: only alternate while BOTH are CURRENTLY pressed (not just
                // memory). Once one is released, alternation ceases after the current
                // element (3.3).
                if (_ditPressed && _dahPressed)
                    return _lastElement == KeyerElement.Dit ? KeyerElement.Dah : KeyerElement.Dit;
                if (ditWanted && !dahWanted) return KeyerElement.Dit;
                if (dahWanted && !ditWanted) return KeyerElement.Dah;
                // Both wanted but only one pressed - use the one that is pressed.
                if (_ditPressed) return KeyerElement.Dit;
                if (_dahPressed) return KeyerElement.Dah;
                return KeyerElement.None;

            case KeyerMode.Ultimatic:
                // Ultimatic: the most recently pressed paddle wins and repeats (3.4).
                if (ditWanted && dahWanted)
                {
                    // Most recent press wins - memory is set on the press transition.
                    if (_ditMemory && !_dahMemory) return KeyerElement.Dit;
                    if (_dahMemory && !_ditMemory) return KeyerElement.Dah;
                    // Both or neither remembered - continue the last element.
                    return _lastElement != KeyerElement.None ? _lastElement : KeyerElement.Dit;
                }
                return ditWanted ? KeyerElement.Dit : KeyerElement.Dah;

            default:
                return ditWanted ? KeyerElement.Dit : KeyerElement.Dah;
        }
    }
}
