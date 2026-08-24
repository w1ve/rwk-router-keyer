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

namespace RWK.Shared;

/// <summary>
/// Direction of a port forwarding rule: which side initiates the connection
/// through the tunnel.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ClientToStation"/> (the default and the only direction in v1.0.1):
/// Client binds a local listener; traffic is tunneled to the Station side and
/// delivered to the Station Target address.
/// </para>
/// <para>
/// <see cref="StationToClient"/>: Station binds an inbound listener on its tailnet
/// address; traffic is tunneled to the Client side and delivered to the Client's
/// bind address. Use cases include N1MM+ broadcasts from Station logger to Client,
/// license servers, and cluster connections.
/// </para>
/// <para>
/// Numeric values are explicit and stable because they are persisted in the profile
/// JSON and pushed over the control channel. Unknown or future values MUST
/// deserialize to <see cref="ClientToStation"/> for backward compatibility.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ForwardDirection>))]
public enum ForwardDirection
{
    /// <summary>
    /// Client → Station. Client binds locally, Station's sidecar registers an inbound
    /// forward on its tailnet address. This is the default for all pre-1.0.3 rules.
    /// </summary>
    ClientToStation = 0,

    /// <summary>
    /// Station → Client. Station's sidecar registers an outbound forward that the
    /// Client's sidecar receives as an inbound forward. Use cases: N1MM+ broadcasts,
    /// license servers, cluster connections running on the Station.
    /// </summary>
    StationToClient = 1
}
