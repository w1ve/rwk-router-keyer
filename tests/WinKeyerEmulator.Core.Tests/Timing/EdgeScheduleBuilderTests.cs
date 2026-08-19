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
using WinKeyerEmulator.Core.Protocol;
using WinKeyerEmulator.Core.Timing;
using Xunit;

namespace WinKeyerEmulator.Core.Tests.Timing;

/// <summary>
/// Property-based and example-based tests for the EdgeScheduleBuilder class.
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
    /// **Validates: Requirements 2.2, 2.3**
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
    /// **Validates: Requirements 2.2, 2.3**
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
    /// **Validates: Requirements 2.2, 2.3**
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
    /// **Validates: Requirements 2.2, 2.3**
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
    /// **Validates: Requirements 2.2, 2.3**
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
    /// P: 4 elements = 8 edges
    /// A: 2 elements = 4 edges
    /// R: 3 elements = 6 edges
    /// I: 2 elements = 4 edges
    /// S: 3 elements = 6 edges
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

        // Calculate expected total duration
        // dit = 10_000_000 * 1200 / (20 * 1000) = 600_000 ticks (60ms)
        long dit = TickFrequency * 1200L / (wpm * 1000L);
        long dah = 3 * dit;
        long intraCharGap = dit;
        long interCharGap = 3 * dit;

        // P: .--. = dit + intra + dah + intra + dah + intra + dit = 4 elements + 3 intra gaps
        //         = dit + dah + dah + dit + 3*intraCharGap = 8*dit + 3*dit = 11*dit
        long pDuration = dit + intraCharGap + dah + intraCharGap + dah + intraCharGap + dit;

        // A: .- = dit + intra + dah = 2 elements + 1 intra gap
        //       = dit + dah + intraCharGap = 4*dit + 1*dit = 5*dit
        long aDuration = dit + intraCharGap + dah;

        // R: .-. = dit + intra + dah + intra + dit = 3 elements + 2 intra gaps
        //        = dit + dah + dit + 2*intraCharGap = 7*dit
        long rDuration = dit + intraCharGap + dah + intraCharGap + dit;

        // I: .. = dit + intra + dit = 2 elements + 1 intra gap
        //       = dit + dit + intraCharGap = 3*dit
        long iDuration = dit + intraCharGap + dit;

        // S: ... = dit + intra + dit + intra + dit = 3 elements + 2 intra gaps
        //        = 3*dit + 2*intraCharGap = 5*dit
        long sDuration = dit + intraCharGap + dit + intraCharGap + dit;

        // Total = sum of character durations + 4 inter-character gaps (between P-A, A-R, R-I, I-S)
        long expectedTotalDuration = pDuration + interCharGap + aDuration + interCharGap +
                                     rDuration + interCharGap + iDuration + interCharGap + sDuration;

        // The last edge should be at the expected total duration
        long actualTotalDuration = edges[edges.Length - 1];
        Assert.Equal(expectedTotalDuration, actualTotalDuration);

        // Verify dit duration is 60ms worth of ticks at 20 WPM
        Assert.Equal(600_000L, dit);
    }
}
