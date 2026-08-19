/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using RWK.Shared.Protocol;

namespace RWK.Shared.Timing;

/// <summary>
/// Pure-function schedule builder that converts text and WPM into an array of absolute
/// tick offsets representing key-down/key-up edge transitions.
/// Even-indexed entries are key-down timestamps, odd-indexed entries are key-up timestamps.
/// </summary>
/// <remarks>
/// Behavior-preserving copy of WinKeyerEmulator.Core.Timing.EdgeScheduleBuilder (RWK v1).
/// </remarks>
public static class EdgeScheduleBuilder
{
    /// <summary>
    /// Builds an array of absolute tick offsets for all key-down/key-up edges.
    /// Even indices are key-down, odd indices are key-up.
    /// </summary>
    /// <param name="text">The text to encode as Morse code.</param>
    /// <param name="wpm">Speed in words per minute (PARIS standard).</param>
    /// <param name="tickFrequency">Tick frequency of the timing source (ticks per second).</param>
    /// <param name="weight">Weight percentage (25-75, default 50). Higher = longer elements, shorter gaps.</param>
    /// <returns>Array of absolute tick offsets from t=0. Empty array if no valid characters.</returns>
    public static long[] Build(string text, int wpm, long tickFrequency, int weight = 50)
    {
        if (string.IsNullOrEmpty(text) || wpm <= 0 || tickFrequency <= 0)
            return Array.Empty<long>();

        // Clamp weight to valid range
        weight = Math.Clamp(weight, 25, 75);

        // Base dit duration (at 50% weight, element = 1 dit, gap = 1 dit)
        long baseDit = tickFrequency * 1200L / (wpm * 1000L);

        // Weight affects element duration vs gap duration
        // At 50% weight: element = baseDit, gap = baseDit (standard)
        // At 75% weight: element = 1.5 * baseDit, gap = 0.5 * baseDit (heavy)
        // At 25% weight: element = 0.5 * baseDit, gap = 1.5 * baseDit (light)
        // Formula: element = baseDit * (weight / 50), gap = baseDit * ((100 - weight) / 50)
        // This keeps total cycle time (element + gap) constant at 2 * baseDit
        double weightFactor = weight / 50.0;
        double gapFactor = (100 - weight) / 50.0;

        long dit = (long)(baseDit * weightFactor);
        long dah = 3 * dit;  // Dah is always 3x dit
        long intraCharGap = (long)(baseDit * gapFactor);  // Between elements within a character
        long interCharGap = 3 * (long)(baseDit * gapFactor);   // Between characters (uses gap timing)
        long wordGap = 7 * baseDit;  // Between words (space character) - uses standard timing

        var edges = new List<long>();
        long position = 0;
        bool previousCharEmitted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == ' ')
            {
                // Word gap: advance position, no edges emitted
                if (previousCharEmitted)
                {
                    position += wordGap;
                }
                previousCharEmitted = false;
                continue;
            }

            if (!MorseTable.TryGetPattern(c, out string pattern))
            {
                // Unknown character: skip entirely
                continue;
            }

            // Add inter-character gap before this character (if a previous character was emitted)
            if (previousCharEmitted)
            {
                position += interCharGap;
            }

            // Emit edges for each element in the pattern
            for (int j = 0; j < pattern.Length; j++)
            {
                // Add intra-character gap before this element (not before the first)
                if (j > 0)
                {
                    position += intraCharGap;
                }

                long elementDuration = pattern[j] == '.' ? dit : dah;

                // Key-down edge
                edges.Add(position);
                // Key-up edge
                edges.Add(position + elementDuration);

                position += elementDuration;
            }

            previousCharEmitted = true;
        }

        return edges.ToArray();
    }
}
