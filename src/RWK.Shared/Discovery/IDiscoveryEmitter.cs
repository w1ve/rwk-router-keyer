/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Shared.Discovery;

/// <summary>
/// Client-side re-emission of forwarded discovery payloads, rewritten to point at the
/// Client-side tunnel endpoint so SmartSDR lists the remote radio.
/// </summary>
/// <remarks>
/// Design Component 11. Every payload broadcast by an implementation has had its radio
/// address and command port replaced, via
/// <see cref="IDiscoveryPayloadCodec.TryRewriteEndpoint"/>, with the bind address and Client
/// port of the enabled forward rule serving that radio's command channel (15.4). There is no
/// code path that broadcasts an unrewritten payload: a verbatim payload advertises a
/// Station-network address SmartSDR cannot reach and the connection attempt fails (15.5).
/// <para>
/// The emitter is report-driven, never timer-driven: at most one broadcast per radio per
/// inbound announce, so it cannot amplify (15.15). Radios are tracked independently, keyed by
/// serial (15.16), and expire on their own schedule (15.14).
/// </para>
/// <para>
/// Runs at <b>normal thread priority</b>; the Client's keyer thread keeps
/// THREAD_PRIORITY_HIGHEST (15.18).
/// </para>
/// _Requirements: 15.3, 15.4, 15.5, 15.6, 15.8, 15.11, 15.13, 15.14, 15.15, 15.16, 15.17, 15.18_
/// </remarks>
public interface IDiscoveryEmitter : IDisposable
{
    /// <summary>
    /// Raised whenever the per-radio table changes, for the Client UI's advertised-radio
    /// list (13.18, 13.20).
    /// </summary>
    event EventHandler<AdvertisedRadiosChangedEventArgs>? AdvertisedRadiosChanged;

    /// <summary>
    /// Starts the emitter.
    /// </summary>
    /// <param name="config">Broadcast endpoint, expiry interval, and command-rule resolver.</param>
    /// <remarks>
    /// Called only while the Client-side re-emission control is on (15.8). Never started by
    /// default (15.6).
    /// </remarks>
    void Start(DiscoveryEmitterConfig config);

    /// <summary>Stops the emitter and ceases all broadcasting.</summary>
    void Stop();

    /// <summary>
    /// Handles one <see cref="DiscoveryAnnounce"/> from the Station.
    /// </summary>
    /// <param name="announce">The forwarded capture, carrying the verbatim payload.</param>
    /// <returns>
    /// The resulting advertisability decision for that radio:
    /// <see cref="RadioAdvertiseState.Advertising"/> when a rewritten payload was broadcast,
    /// otherwise the reason it was withheld.
    /// </returns>
    /// <remarks>
    /// Announces are discarded outright while the re-emission control is off (15.8).
    /// Payloads that cannot be parsed, or that lack the address and port fields the rewrite
    /// needs, are discarded with a log entry naming the reason and yield
    /// <see cref="RadioAdvertiseState.WithheldRewriteFailed"/> (15.17). At most one datagram
    /// is broadcast per call, and only if the rewrite succeeded; no other radio's entry is
    /// modified.
    /// </remarks>
    RadioAdvertiseState OnDiscoveryAnnounce(DiscoveryAnnounce announce);

    /// <summary>
    /// Called when the tunnel session drops.
    /// </summary>
    /// <remarks>
    /// Clears the entire radio table and stops all broadcasting so every radio disappears
    /// from SmartSDR rather than lingering as apparently reachable (15.13). Nothing is
    /// retained across session loss: the table refills from fresh announces after
    /// reconnection.
    /// </remarks>
    void OnSessionLost();

    /// <summary>Gets whether the emitter is started.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Gets every tracked radio with its current advertise state, including withheld ones so
    /// the UI can explain their absence (13.20).
    /// </summary>
    IReadOnlyList<AdvertisedRadio> AdvertisedRadios { get; }
}
