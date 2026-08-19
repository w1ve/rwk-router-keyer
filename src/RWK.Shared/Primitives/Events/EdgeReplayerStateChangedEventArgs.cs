namespace RWK.Shared;

/// <summary>
/// Reports a change in the Station edge replayer's state, including SAFE latch
/// transitions, so the UI can render the SAFE/ARMED banner and enable or disable the
/// Re-Arm button (13.6, 13.7, 13.8).
/// </summary>
/// <remarks>
/// The design names this type on <c>IEdgeReplayer.StateChanged</c> without giving its
/// members. <see cref="IsSafeLatched"/> is carried explicitly rather than inferred from
/// <see cref="State"/> because the UI's latch rendering must not depend on a mapping
/// that could drift, and <see cref="LastCondition"/> lets the banner name the condition
/// that locked the key.
/// <para>
/// _Requirements: 9.11, 9.12, 13.5, 13.6, 13.7, 13.8_
/// </para>
/// </remarks>
/// <param name="State">The new replayer state.</param>
/// <param name="IsSafeLatched">
/// Whether key output is currently locked by the SAFE latch. When
/// <see langword="true"/>, keying stays blocked until a manual Re-Arm for a latching
/// condition (9.11), or until valid edges resume for a degraded session (9.12).
/// </param>
/// <param name="LastCondition">
/// The fail-safe condition responsible for the current state, or <see langword="null"/>
/// for a transition not caused by a fail-safe.
/// </param>
/// <param name="Message">Optional human-readable detail for the log and the UI.</param>
public record EdgeReplayerStateChangedEventArgs(
    EdgeReplayerState State,
    bool IsSafeLatched,
    FailSafeCondition? LastCondition = null,
    string? Message = null
);
