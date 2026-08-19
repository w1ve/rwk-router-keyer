/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using WinKeyerEmulator.Core.Keying;
using Xunit;

namespace WinKeyerEmulator.Core.Tests.Keying;

public class SoftKeyerTests
{
    [Fact]
    public void SoftKeyer_StartsAndStops_WithoutError()
    {
        using var keyer = new SoftKeyer();
        keyer.Start();
        Assert.True(keyer.IsRunning);
        keyer.Stop();
        Assert.False(keyer.IsRunning);
    }

    [Fact]
    public void SoftKeyer_WpmClamps_ToValidRange()
    {
        using var keyer = new SoftKeyer();
        
        keyer.Wpm = 100;
        Assert.Equal(60, keyer.Wpm);

        keyer.Wpm = 1;
        Assert.Equal(5, keyer.Wpm);

        keyer.Wpm = 25;
        Assert.Equal(25, keyer.Wpm);
    }

    [Fact]
    public void SoftKeyer_ModeProperty_SetAndGet()
    {
        using var keyer = new SoftKeyer();

        keyer.Mode = SoftKeyerMode.IambicA;
        Assert.Equal(SoftKeyerMode.IambicA, keyer.Mode);

        keyer.Mode = SoftKeyerMode.Bug;
        Assert.Equal(SoftKeyerMode.Bug, keyer.Mode);
    }

    [Fact]
    public void SoftKeyer_DecodesE_FromSingleDit()
    {
        using var keyer = new SoftKeyer { Wpm = 40 }; // Fast for quicker test
        var decoded = new List<char>();
        keyer.CharacterDecoded += (_, c) => decoded.Add(c);

        keyer.Start();

        // Press dit briefly — at 40 WPM, dit = 30ms
        keyer.DitPressed = true;
        Thread.Sleep(40);
        keyer.DitPressed = false;

        // Wait for decode (letter gap at 40 WPM = ~75ms, word gap = ~210ms)
        // Wait long enough for letter decode but not word space
        Thread.Sleep(120);

        keyer.Stop();

        // Should have decoded 'E' (first character, ignore any trailing space)
        Assert.Contains('E', decoded);
    }

    [Fact]
    public void SoftKeyer_DecodesT_FromSingleDah()
    {
        using var keyer = new SoftKeyer { Wpm = 40 };
        var decoded = new List<char>();
        keyer.CharacterDecoded += (_, c) => decoded.Add(c);

        keyer.Start();

        // Press dah briefly — at 40 WPM, dah = 90ms
        keyer.DahPressed = true;
        Thread.Sleep(100);
        keyer.DahPressed = false;

        // Wait for decode
        Thread.Sleep(120);

        keyer.Stop();

        // Should have decoded 'T'
        Assert.Contains('T', decoded);
    }

    [Fact]
    public void SoftKeyer_ElementStarted_FiresOnKeyDown()
    {
        using var keyer = new SoftKeyer { Wpm = 40 };
        bool? lastElement = null;
        keyer.ElementStarted += (_, isDit) => lastElement = isDit;

        keyer.Start();

        keyer.DitPressed = true;
        Thread.Sleep(50);

        Assert.True(lastElement);

        keyer.DitPressed = false;
        Thread.Sleep(100);

        keyer.DahPressed = true;
        Thread.Sleep(50);

        Assert.False(lastElement);

        keyer.Stop();
    }
}
