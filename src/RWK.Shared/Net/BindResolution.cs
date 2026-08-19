using System.Net;

namespace RWK.Shared.Net;

/// <summary>
/// The result of resolving a forward rule's bind address against the host's local interfaces.
/// A discriminated union: exactly one of <see cref="Bound"/>, <see cref="Unavailable"/>,
/// or <see cref="Invalid"/> is returned by <see cref="BindAddressResolver.ResolveRuleBindAddress"/>.
/// </summary>
/// <remarks>
/// _Requirements: 10.15_
/// </remarks>
public abstract record BindResolution;

/// <summary>
/// The requested address is valid and available on this host. The caller may bind to it.
/// </summary>
/// <param name="Address">The resolved IP address.</param>
public sealed record Bound(IPAddress Address) : BindResolution;

/// <summary>
/// The requested address parses as a valid IP address but is not present on the host.
/// The caller MUST set the rule to Error status and MUST NOT substitute a different address.
/// </summary>
/// <param name="Message">A human-readable message naming the unavailable address.</param>
public sealed record Unavailable(string Message) : BindResolution;

/// <summary>
/// The requested address string does not parse as a valid IP address.
/// </summary>
/// <param name="Message">A human-readable message naming the invalid string.</param>
public sealed record Invalid(string Message) : BindResolution;
