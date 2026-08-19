namespace RWK.Shared.Config;

/// <summary>
/// A Station-side decision about a forward rule pushed by the Client (10.9, 10.10).
/// </summary>
/// <param name="RuleId">The <see cref="ForwardRule.Id"/> this override applies to.</param>
/// <param name="Allowed">Whether the Station owner permits the rule (10.9).</param>
/// <param name="TargetHostOverride">
/// Replacement target host on the Station's network, for redirecting a rule to a LAN
/// device rather than the Station host itself (10.10). <see langword="null"/> keeps the
/// default target.
/// </param>
/// <remarks>
/// _Requirements: 10.9, 10.10, 12.5_
/// </remarks>
public record ForwardRuleOverride(
    Guid RuleId,
    bool Allowed,
    string? TargetHostOverride);
