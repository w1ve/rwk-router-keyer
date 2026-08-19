/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Text.Json.Serialization;

namespace RWK.Shared.Config;

/// <summary>
/// Classifies the traffic a forward rule is intended to carry.
/// </summary>
/// <remarks>
/// In this release only <see cref="FlexDiscovery"/> has protocol-aware behavior
/// (see the FlexRadio Discovery Broker components). <see cref="Cat"/>, <see cref="Audio"/>,
/// and <see cref="RemoteRig"/> are labels only: the port forward manager treats them
/// exactly as <see cref="Generic"/> for the same protocol (10.17). Protocol-aware
/// RemoteRig handling is out of scope for this release.
/// <para>
/// Numeric values are explicit and stable because they are persisted in the profile JSON.
/// Unknown or future values MUST deserialize to <see cref="Generic"/> for forward
/// compatibility — see <see cref="ForwardRuleTypeJsonConverter"/> — so a profile written
/// by a newer build still loads on an older one (10.16, 10.17, 12.6).
/// </para>
/// <para>
/// _Requirements: 10.16, 10.17_
/// </para>
/// </remarks>
[JsonConverter(typeof(ForwardRuleTypeJsonConverter))]
public enum ForwardRuleType
{
    /// <summary>Plain TCP or UDP relay with no payload awareness. The default.</summary>
    Generic = 0,

    /// <summary>Label for a CAT control rule. Behaves identically to <see cref="Generic"/>.</summary>
    Cat = 1,

    /// <summary>Label for an audio rule. Behaves identically to <see cref="Generic"/>.</summary>
    Audio = 2,

    /// <summary>
    /// Label for a RemoteRig RRC rule. Behaves identically to <see cref="Generic"/>;
    /// RRC compatibility is unverified against physical hardware (10.16, 10.18).
    /// </summary>
    RemoteRig = 3,

    /// <summary>
    /// FlexRadio discovery brokering. The only protocol-aware rule type in this release:
    /// the discovery emitter resolves the command-channel rule for a radio by this type.
    /// </summary>
    FlexDiscovery = 4
}
