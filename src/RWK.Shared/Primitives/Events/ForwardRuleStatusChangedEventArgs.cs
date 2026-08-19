namespace RWK.Shared;

/// <summary>
/// Reports a change to a forward rule's runtime status along with its byte counters.
/// </summary>
/// <remarks>
/// This record is the carrier of "status plus message" for a rule. A
/// <see cref="ForwardRuleStatus.Error"/> status caused by a bind address that is not
/// present on the Client host must name that address (10.15), and the enum cannot
/// carry text, so <see cref="Message"/> carries it. No separate status/message pair
/// type is introduced.
/// <para>
/// <see cref="Message"/> is optional and trails the shape given in the design so the
/// positional arity from the design document still binds.
/// </para>
/// _Requirements: 10.15_
/// </remarks>
/// <param name="RuleId">Identifier of the rule whose status changed.</param>
/// <param name="Status">The new status.</param>
/// <param name="BytesIn">Cumulative bytes relayed toward the Client for this rule.</param>
/// <param name="BytesOut">Cumulative bytes relayed toward the Station for this rule.</param>
/// <param name="Message">
/// Human-readable detail. Required in practice for <see cref="ForwardRuleStatus.Error"/>,
/// where it names the unavailable bind address (10.15); <see langword="null"/> otherwise.
/// </param>
public record ForwardRuleStatusChangedEventArgs(
    Guid RuleId,
    ForwardRuleStatus Status,
    long BytesIn,
    long BytesOut,
    string? Message = null
);
