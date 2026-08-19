/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Shared;

/// <summary>
/// Reports that a fail-safe condition fired. Key output has already been forced up by
/// the time this is raised — subscribers are observers, not part of the safety path.
/// </summary>
/// <remarks>
/// _Requirements: 9.1, 9.2, 9.3, 9.4, 9.5, 9.6, 9.7, 9.8, 9.9, 9.10_
/// </remarks>
/// <param name="Condition">Which of the ten enumerated conditions fired.</param>
/// <param name="Message">
/// Human-readable detail naming the measured values that tripped the condition, for the
/// log and the Station UI.
/// </param>
public record FailSafeTriggeredEventArgs(
    FailSafeCondition Condition,
    string Message
);
