/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using FsCheck;
using FsCheck.Xunit;
using RWK.Shared.Protocol;
using RWK.Shared.Timing;
using Xunit;

namespace RWK.Shared.Tests.Timing;

/// <summary>
/// Property-based and example-based tests for the EdgeScheduleBuilder class.
/// Ported verbatim (namespaces aside) from
/// tests/WinKeyerEmulator.Core.Tests/Timing/EdgeScheduleBuilderTests.cs so that the
/// RWK.Shared copy of EdgeScheduleBuilder is proven behavior-preserving.
/// </summary>
public class EdgeScheduleBuilderTests
{
    private const long TickFrequency = 10_000_000L; // 10 MHz (like Stopwatch.Frequency on most systems)

    /// <summary>
    /// Generator for valid WPM values (5-45).
    /// </summary>
    private static Gen<int> ValidWpmGen => Gen.Choose(5, 45);

    /// <summary>
    /// Generator for non-empty strings composed of characters that have Morse patterns.
    /// </summary>
    private static Gen<string> ValidMorseTextGen
    {
        get
        {
            var supportedChars = MorseTable.SupportedCharacters.ToArray();
            var charGen = Gen.Elements(supportedChars);
            return Gen.NonEmptyListOf(charGen).Select(chars => new string(chars.ToArray()));
        }
    }

    /// <summary>
    /// Property test: output is strictly monotonically increasing for all valid inputs (wpm 5-45, non-empty valid text).
    /// </summary>
    [Property]
    public Property Output_IsStrictlyMonotonicallyIncreasing()
    {
        var gen = from text in ValidMorseTextGen
                  from wpm in ValidWpmGen
                  select (text, wpm);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (text, wpm) = tuple;
            long[] edges = EdgeScheduleBuilder.Build(text, wpm, TickFrequency);

            if (edges.Length < 2)
                return true;

            for (int i = 0; i < edges.Length - 1; i++)
            {
                if (edges[i] >= edges[i + 1])
                    return false;
            }
            return true;
        });
    }

    /// <summary>
    /// Property test: output always has even length (each key-down has a matching key-up).
    /// </summary>
    [Property]
    public Property Output_AlwaysHasEvenLength()
    {
        var gen = from text in ValidMorseTextGen
                  from wpm in ValidWpmGen
                  select (text, wpm);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (text, wpm) = tuple;
            long[] edges = EdgeScheduleBuilder.Build(text, wpm, TickFrequency);
            return edges.Length % 2 == 0;
        });
    }

    /// <summary>
    /// Property test: for single-dit character 'E', edge[1]-edge[0] == ditTicks exactly.
    /// </summary>
    [Property]
    public Property SingleDitCharacter_E_HasExactDitDuration()
    {
        return Prop.ForAll(ValidWpmGen.ToArbitrary(), wpm =>
        {
            long[] edges = EdgeScheduleBuilder.Build("E", wpm, TickFrequency);
            long expectedDit = TickFrequency * 1200L / (wpm * 1000L);

            return edges.Length == 2 && edges[1] - edges[0] == expectedDit;
        });
    }

    /// <summary>
    /// Property test: for single-dah character 'T', edge[1]-edge[0] == 3*ditTicks exactly.
    /// </summary>
    [Property]
    public Property SingleDahCharacter_T_HasExactDahDuration()
    {
        return Prop.ForAll(ValidWpmGen.ToArbitrary(), wpm =>
        {
            long[] edges = EdgeScheduleBuilder.Build("T", wpm, TickFrequency);
            long dit = TickFrequency * 1200L / (wpm * 1000L);
            long expectedDah = 3 * dit;

            return edges.Length == 2 && edges[1] - edges[0] == expectedDah;
        });
    }

    /// <summary>
    /// Property test: same input always produces same output (determinism).
    /// </summary>
    [Property]
    public Property SameInput_AlwaysProduces_SameOutput()
    {
        var gen = from text in ValidMorseTextGen
                  from wpm in ValidWpmGen
                  select (text, wpm);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (text, wpm) = tuple;
            long[] edges1 = EdgeScheduleBuilder.Build(text, wpm, TickFrequency);
            long[] edges2 = EdgeScheduleBuilder.Build(text, wpm, TickFrequency);

            return edges1.SequenceEqual(edges2);
        });
    }

    /// <summary>
    /// Example test: "PARIS" at 20 WPM produces expected number of edges and total duration.
    /// PARIS in Morse: P(.--.) A(.-) R(.-.) I(..) S(...)
    /// Total: 28 edges (14 key-down + 14 key-up)
    /// </summary>
    [Fact]
    public void Paris_At20Wpm_ProducesExpectedEdgesAndDuration()
    {
        const int wpm = 20;
        long[] edges = EdgeScheduleBuilder.Build("PARIS", wpm, TickFrequency);

        // Verify expected edge count: 28 edges total
        Assert.Equal(28, edges.Length);

        // Verify even length (key-down/key-up pairs)
        Assert.Equal(0, edges.Length % 2);

        // Verify monotonically increasing
        for (int i = 0; i < edges.Length - 1; i++)
        {
            Assert.True(edges[i] < edges[i + 1],
                $"Edge {i} ({edges[i]}) should be less than edge {i + 1} ({edges[i + 1]})");
        }

        // dit = 10_000_000 * 1200 / (20 * 1000) = 600_000 ticks (60ms)
        long dit = TickFrequency * 1200L / (wpm * 1000L);
        long dah = 3 * dit;
        long intraCharGap = dit;
        long interCharGap = 3 * dit;

        long pDuration = dit + intraCharGap + dah + intraCharGap + dah + intraCharGap + dit;
        long aDuration = dit + intraCharGap + dah;
        long rDuration = dit + intraCharGap + dah + intraCharGap + dit;
        long iDuration = dit + intraCharGap + dit;
        long sDuration = dit + intraCharGap + dit + intraCharGap + dit;

        long expectedTotalDuration = pDuration + interCharGap + aDuration + interCharGap +
                                     rDuration + interCharGap + iDuration + interCharGap + sDuration;

        long actualTotalDuration = edges[edges.Length - 1];
        Assert.Equal(expectedTotalDuration, actualTotalDuration);

        // Verify dit duration is 60ms worth of ticks at 20 WPM
        Assert.Equal(600_000L, dit);
    }
}
