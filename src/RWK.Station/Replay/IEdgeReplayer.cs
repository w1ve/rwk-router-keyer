using RWK.Shared;
using RWK.Shared.Config;
using RWK.Shared.IO;

namespace RWK.Station.Replay;

/// <summary>
/// Receives edge datagrams, buffers them for jitter compensation, and schedules precise replay to
/// the keying output (design Component 7).
/// </summary>
/// <remarks>
/// <para>
/// Design Component 7 also lists <c>ProcessControlCommand(ControlCommand cmd)</c>. It is absent here
/// because the control-channel message types do not exist yet; it is added with the control channel
/// rather than guessed at now.
/// </para>
/// <para>
/// The fail-safe members are present but deliberately thin: the replayer forces key-up on any
/// condition it detects and owns the latch <i>state</i>, while the policy of which conditions latch
/// and how a latch clears belongs to the fail-safe monitor behind <see cref="IFailSafeSink"/>
/// (tasks 12.1 - 12.6).
/// </para>
/// <para>
/// _Requirements: 7.1, 7.2, 7.3, 7.4, 7.5, 9.11, 9.12_
/// </para>
/// </remarks>
public interface IEdgeReplayer : IDisposable
{
    /// <summary>Raised when the replayer's state or SAFE latch changes (13.5 - 13.8).</summary>
    event EventHandler<EdgeReplayerStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised after a fail-safe condition fired and key output has already been forced up
    /// (Requirement 9). Subscribers are observers, not part of the safety path.
    /// </summary>
    event EventHandler<FailSafeTriggeredEventArgs>? FailSafeTriggered;

    /// <summary>
    /// Starts the replay thread at THREAD_PRIORITY_TIME_CRITICAL with
    /// <c>GCLatencyMode.SustainedLowLatency</c> (7.4, 14.7).
    /// </summary>
    /// <param name="keyingOutput">The key line output.</param>
    /// <param name="pttOutput">
    /// The PTT output, or <see langword="null"/> when the PTT line is <c>None</c> (8.2).
    /// </param>
    void Start(IKeyingOutput keyingOutput, IPttOutput? pttOutput);

    /// <summary>
    /// Stops the replay thread, forcing key and PTT up first. Safe to call when not started.
    /// </summary>
    void Stop();

    /// <summary>
    /// Binds the replayer to a session epoch, clearing all sequence, anchor, and adaptation state.
    /// </summary>
    /// <remarks>
    /// Call this only for genuine session establishment or reconnect. It discards the verified
    /// sequence baseline, so the next edge — key-down included — is applied unconditionally; calling
    /// it mid-stream would defeat the F5 gap check of 9.5.
    /// </remarks>
    void BeginSession(ushort epoch);

    /// <summary>
    /// Ends the current session: forces key and PTT up and stops accepting edges until the next
    /// <see cref="BeginSession"/>.
    /// </summary>
    void EndSession();

    /// <summary>
    /// Hands one received RWK-PADDLE datagram to the replayer. Returns immediately: parsing,
    /// validation, and scheduling happen on the replay thread. Never throws for malformed input.
    /// </summary>
    void ProcessDatagram(ReadOnlySpan<byte> data);

    /// <summary>
    /// Records that a heartbeat arrived (6.8). The timestamp feeds the heartbeat-timeout conditions
    /// F1 and F2, which the fail-safe monitor evaluates (9.1, 9.2).
    /// </summary>
    void ProcessHeartbeat();

    /// <summary>Jitter buffer delays and adaptive mode (7.1).</summary>
    JitterBufferConfig JitterConfig { get; set; }

    /// <summary>Current operating state.</summary>
    EdgeReplayerState State { get; }

    /// <summary>
    /// Whether key output is locked by the SAFE latch. While latched, arriving edges are discarded
    /// rather than scheduled.
    /// </summary>
    bool IsSafeLatched { get; }

    /// <summary>
    /// Sets the SAFE latch and forces key and PTT up. Called by the fail-safe monitor for the
    /// conditions whose policy is to latch (9.11).
    /// </summary>
    void LatchSafe(FailSafeCondition condition, string message);

    /// <summary>
    /// Clears the SAFE latch so keying can resume: the Re-Arm action for a latching condition
    /// (9.11), or the automatic clear for a degraded session (9.12).
    /// </summary>
    void ClearSafeLatch();

    /// <summary>
    /// Forces key and PTT up immediately, discarding pending scheduled edges and the current
    /// anchor. Does not latch.
    /// </summary>
    void ForceKeyUp();

    /// <summary>
    /// The fail-safe monitor that decides latch policy, or <see langword="null"/> while none is
    /// installed (tasks 12.1 - 12.6).
    /// </summary>
    IFailSafeSink? FailSafeSink { get; set; }

    /// <summary>A snapshot of counters and timing measurements (7.5, 14.5).</summary>
    EdgeReplayerTelemetry Telemetry { get; }
}
