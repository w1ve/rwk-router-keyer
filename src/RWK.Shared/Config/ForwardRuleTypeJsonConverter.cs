/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RWK.Shared.Config;

/// <summary>
/// Serializes <see cref="ForwardRuleType"/> as its numeric value and, on read, maps any
/// value it does not recognize to <see cref="ForwardRuleType.Generic"/> instead of throwing.
/// </summary>
/// <remarks>
/// Forward compatibility requirement: a rule type written by a newer build (an unknown
/// number, a number outside the defined range, or an unrecognized name) must not fail the
/// whole profile load, and the safe interpretation of an unrecognized type is the generic
/// relay, which inspects nothing (10.16, 10.17, 12.6).
/// <para>
/// Known names are also accepted on read, case-insensitively, so a hand-edited profile
/// using <c>"FlexDiscovery"</c> loads as expected.
/// </para>
/// <para>
/// _Requirements: 10.16, 10.17_
/// </para>
/// </remarks>
public sealed class ForwardRuleTypeJsonConverter : JsonConverter<ForwardRuleType>
{
    /// <inheritdoc />
    public override ForwardRuleType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                // Read as long so that values outside the int range do not throw, then
                // accept only values that name a defined member.
                if (reader.TryGetInt64(out long numeric)
                    && numeric is >= int.MinValue and <= int.MaxValue
                    && Enum.IsDefined(typeof(ForwardRuleType), (ForwardRuleType)(int)numeric))
                {
                    return (ForwardRuleType)(int)numeric;
                }

                return ForwardRuleType.Generic;

            case JsonTokenType.String:
                string? name = reader.GetString();
                return Enum.TryParse(name, ignoreCase: true, out ForwardRuleType parsed)
                       && Enum.IsDefined(typeof(ForwardRuleType), parsed)
                    ? parsed
                    : ForwardRuleType.Generic;

            case JsonTokenType.Null:
                return ForwardRuleType.Generic;

            default:
                // Any other shape (object, array, boolean) is a value this build does not
                // understand; fall back rather than failing the profile load.
                reader.Skip();
                return ForwardRuleType.Generic;
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ForwardRuleType value, JsonSerializerOptions options)
        => writer.WriteNumberValue((int)value);
}
