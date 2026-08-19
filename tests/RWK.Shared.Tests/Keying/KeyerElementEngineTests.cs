/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared;
using RWK.Shared.Keying;
using Xunit;

namespace RWK.Shared.Tests.Keying;

/// <summary>
/// Unit tests for the mode-dependent element decision state machine
/// (Requirements 3.1-3.6).
/// </summary>
/// <remarks>
/// The engine holds no clock and never blocks, so every case here is a plain sequence of
/// state pokes and element reads. These are the decisions that the RWK v1 <c>SoftKeyer</c>
/// could only be observed making indirectly, by decoding the characters that fell out the
/// far end of a sleeping timing loop.
/// </remarks>
public class KeyerElementEngineTests
{
    private static KeyerElementEngine EngineIn(KeyerMode mode) => new() { Mode = mode };

    [Fact]
    public void DefaultMode_IsIambicB()
    {
        Assert.Equal(KeyerMode.IambicB, new KeyerElementEngine().Mode);
    }

    [Fact]
    public void IdlePaddles_ProduceNoElement()
    {
        var engine = EngineIn(KeyerMode.IambicB);

        Assert.Equal(KeyerElement.None, engine.RequestNextElement());
    }

    [Theory]
    [InlineData(KeyerMode.IambicA)]
    [InlineData(KeyerMode.IambicB)]
    [InlineData(KeyerMode.Ultimatic)]
    public void SingleContact_ProducesMatchingElement(KeyerMode mode)
    {
        var engine = EngineIn(mode);

        engine.SetPaddleState(dit: true, dah: false, straight: false);
        Assert.Equal(KeyerElement.Dit, engine.RequestNextElement());

        engine.SetPaddleState(dit: false, dah: false, straight: false);
        engine.SetPaddleState(dit: false, dah: true, straight: false);
        Assert.Equal(KeyerElement.Dah, engine.RequestNextElement());
    }

    [Fact]
    public void IambicB_Squeeze_Alternates()
    {
        var engine = EngineIn(KeyerMode.IambicB);
        engine.SetPaddleState(dit: true, dah: true, straight: false);

        Assert.Equal(KeyerElement.Dit, engine.RequestNextElement());
        Assert.Equal(KeyerElement.Dah, engine.RequestNextElement());
        Assert.Equal(KeyerElement.Dit, engine.RequestNextElement());
        Assert.Equal(KeyerElement.Dah, engine.RequestNextElement());
    }

    /// <summary>
    /// Iambic B honours the remembered tap after release: the queued opposite element still
    /// comes out (3.2).
    /// </summary>
    [Fact]
    public void IambicB_ReleaseDuringElement_StillSendsRememberedOpposite()
    {
        var engine = EngineIn(KeyerMode.IambicB);
        engine.SetPaddleState(dit: true, dah: true, straight: false);
        Assert.Equal(KeyerElement.Dit, engine.RequestNextElement());

        // Dah released, but its tap is remembered.
        engine.SetPaddleState(dit: true, dah: false, straight: false);

        Assert.Equal(KeyerElement.Dah, engine.RequestNextElement());
    }

    /// <summary>
    /// Iambic A stops alternating as soon as a paddle is released: only the contact that is
    /// still closed keeps generating (3.3). This is the behavior that distinguishes it from
    /// Iambic B, which is why the two modes are decided separately rather than sharing a case.
    /// </summary>
    [Fact]
    public void IambicA_ReleaseDuringElement_CeasesAlternation()
    {
        var engine = EngineIn(KeyerMode.IambicA);
        engine.SetPaddleState(dit: true, dah: true, straight: false);
        Assert.Equal(KeyerElement.Dit, engine.RequestNextElement());

        engine.SetPaddleState(dit: true, dah: false, straight: false);

        Assert.Equal(KeyerElement.Dit, engine.RequestNextElement());
        Assert.Equal(KeyerElement.Dit, engine.RequestNextElement());
    }

    [Fact]
    public void IambicA_Squeeze_AlternatesWhileBothHeld()
    {
        var engine = EngineIn(KeyerMode.IambicA);
        engine.SetPaddleState(dit: true, dah: true, straight: false);

        Assert.Equal(KeyerElement.Dit, engine.RequestNextElement());
        Assert.Equal(KeyerElement.Dah, engine.RequestNextElement());
        Assert.Equal(KeyerElement.Dit, engine.RequestNextElement());
    }

    /// <summary>
    /// Ultimatic: the paddle pressed most recently wins the squeeze and then repeats (3.4).
    /// </summary>
    [Fact]
    public void Ultimatic_LastPressedWinsAndRepeats()
    {
        var engine = EngineIn(KeyerMode.Ultimatic);

        engine.SetPaddleState(dit: true, dah: false, straight: false);
        Assert.Equal(KeyerElement.Dit, engine.RequestNextElement());

        // Dah added to the squeeze: it is the most recent press, so it wins.
        engine.SetPaddleState(dit: true, dah: true, straight: false);
        Assert.Equal(KeyerElement.Dah, engine.RequestNextElement());

        // No new press: the winner repeats rather than alternating.
        Assert.Equal(KeyerElement.Dah, engine.RequestNextElement());
        Assert.Equal(KeyerElement.Dah, engine.RequestNextElement());
    }

    /// <summary>
    /// Bug mode: dits repeat while the contact is closed (3.5).
    /// </summary>
    [Fact]
    public void Bug_DitContact_RepeatsWhileHeld()
    {
        var engine = EngineIn(KeyerMode.Bug);
        engine.SetPaddleState(dit: true, dah: false, straight: false);

        Assert.Equal(KeyerElement.Dit, engine.RequestNextElement());
        Assert.Equal(KeyerElement.Dit, engine.RequestNextElement());

        engine.SetPaddleState(dit: false, dah: false, straight: false);
        Assert.Equal(KeyerElement.None, engine.RequestNextElement());
    }

    /// <summary>
    /// Bug mode: one dah per press, even while the contact stays closed — the operator times
    /// dahs, so the keyer must not run them together (3.5).
    /// </summary>
    [Fact]
    public void Bug_DahContact_IsSingleShotPerPress()
    {
        var engine = EngineIn(KeyerMode.Bug);
        engine.SetPaddleState(dit: false, dah: true, straight: false);

        Assert.Equal(KeyerElement.Dah, engine.RequestNextElement());
        Assert.Equal(KeyerElement.None, engine.RequestNextElement());

        // Release and press again: a new press earns a new dah.
        engine.SetPaddleState(dit: false, dah: false, straight: false);
        engine.SetPaddleState(dit: false, dah: true, straight: false);

        Assert.Equal(KeyerElement.Dah, engine.RequestNextElement());
        Assert.Equal(KeyerElement.None, engine.RequestNextElement());
    }

    /// <summary>
    /// Straight mode generates nothing at all; the contact is passed through by the caller (3.6).
    /// </summary>
    [Fact]
    public void Straight_GeneratesNoElements()
    {
        var engine = EngineIn(KeyerMode.Straight);
        engine.SetPaddleState(dit: true, dah: true, straight: true);

        Assert.Equal(KeyerElement.None, engine.RequestNextElement());
        Assert.Equal(KeyerElement.None, engine.RequestNextElement());
        Assert.True(engine.StraightPressed);
    }

    [Fact]
    public void Memory_RecordsTapMadeAndReleasedBetweenDecisions()
    {
        var engine = EngineIn(KeyerMode.IambicB);

        engine.SetPaddleState(dit: true, dah: false, straight: false);
        engine.SetPaddleState(dit: false, dah: false, straight: false);

        Assert.True(engine.DitMemory);
        Assert.Equal(KeyerElement.Dit, engine.RequestNextElement());
        Assert.False(engine.DitMemory);
        Assert.Equal(KeyerElement.None, engine.RequestNextElement());
    }

    [Fact]
    public void ClearMemory_DropsRememberedTapsButKeepsContacts()
    {
        var engine = EngineIn(KeyerMode.IambicB);
        engine.SetPaddleState(dit: true, dah: true, straight: false);
        engine.SetPaddleState(dit: true, dah: false, straight: false);

        engine.ClearMemory();

        Assert.False(engine.DahMemory);
        Assert.True(engine.DitPressed);
        Assert.Equal(KeyerElement.Dit, engine.RequestNextElement());
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var engine = EngineIn(KeyerMode.IambicB);
        engine.SetPaddleState(dit: true, dah: true, straight: true);
        engine.RequestNextElement();

        engine.Reset();

        Assert.False(engine.DitPressed);
        Assert.False(engine.DahPressed);
        Assert.False(engine.StraightPressed);
        Assert.False(engine.DitMemory);
        Assert.False(engine.DahMemory);
        Assert.Equal(KeyerElement.None, engine.LastElement);
        Assert.Equal(KeyerElement.None, engine.RequestNextElement());
    }
}
