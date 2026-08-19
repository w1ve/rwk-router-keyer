/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Shared.Config;

/// <summary>
/// PTT lead and tail timing for the Station keying output (8.4, 8.5).
/// </summary>
/// <remarks>
/// _Requirements: 12.5_
/// </remarks>
public record PttTimingConfig
{
    /// <summary>
    /// How far ahead of key-down PTT is asserted. Default 15ms (8.4).
    /// </summary>
    public TimeSpan LeadTime { get; init; } = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// How long PTT is held after key-up before it is de-asserted; extended by each
    /// subsequent key-down. Default 500ms (8.5, 8.6).
    /// </summary>
    public TimeSpan TailTime { get; init; } = TimeSpan.FromMilliseconds(500);
}
