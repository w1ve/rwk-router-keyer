namespace RWK.Shared;

/// <summary>
/// Runtime status of a single port forwarding rule.
/// </summary>
/// <remarks>
/// The status alone cannot explain an <see cref="Error"/>, and 10.15 requires the
/// unavailable bind address to be named. The accompanying message is therefore
/// carried by <see cref="ForwardRuleStatusChangedEventArgs.Message"/>, which is the
/// single carrier of status plus message for a rule.
/// <para>
/// _Requirements: 10.15_
/// </para>
/// </remarks>
public enum ForwardRuleStatus
{
    /// <summary>Rule exists but is disabled or the manager is stopped. No listener.</summary>
    Idle = 0,

    /// <summary>Listener is bound to the rule's bind address, with no active flow.</summary>
    Listening = 1,

    /// <summary>At least one connection or UDP session is currently relaying.</summary>
    Active = 2,

    /// <summary>
    /// The rule could not be brought up. The listener is left unbound — never bound
    /// to a substituted address. The reason, including the name of an unavailable
    /// bind address, travels in the status-changed event message (10.15).
    /// </summary>
    Error = 3
}
