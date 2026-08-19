using System.Net;
using RWK.Shared.Config;

namespace RWK.Shared.Net;

/// <summary>
/// Pure function that resolves a forward rule's <see cref="ForwardRule.BindAddress"/> to
/// a <see cref="BindResolution"/>. Opens no sockets, mutates no state, and never
/// substitutes a different address — the "no silent fallback" guarantee of requirement 10.15.
/// </summary>
/// <remarks>
/// Design Function 6. The caller is responsible for acting on the result:
/// <list type="bullet">
///   <item><see cref="Bound"/>: proceed to bind the listener.</item>
///   <item><see cref="Unavailable"/>: set the rule's status to Error with the message.</item>
///   <item><see cref="Invalid"/>: set the rule's status to Error with the message.</item>
/// </list>
/// <para>
/// _Requirements: 10.15_
/// </para>
/// </remarks>
public static class BindAddressResolver
{
    /// <summary>
    /// Resolves the bind address of a forward rule against the addresses present on the host.
    /// </summary>
    /// <param name="rule">The forward rule whose <see cref="ForwardRule.BindAddress"/> is resolved.</param>
    /// <param name="hostAddresses">
    /// The IP addresses currently assigned to the host's network interfaces.
    /// Typically obtained from <see cref="Dns.GetHostAddresses(string)"/> or a network-change cache.
    /// </param>
    /// <returns>
    /// A <see cref="BindResolution"/> indicating whether the address is bindable, unavailable, or invalid.
    /// </returns>
    public static BindResolution ResolveRuleBindAddress(ForwardRule rule, IReadOnlyList<IPAddress> hostAddresses)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(hostAddresses);

        string addressString = rule.BindAddress;

        // Step 1: Try to parse the bind address string as an IP address.
        if (!IPAddress.TryParse(addressString, out IPAddress? parsed))
        {
            return new Invalid($"'{addressString}' is not a valid IP address");
        }

        // Step 2: Loopback is always available (127.0.0.1, ::1, etc.).
        if (IPAddress.IsLoopback(parsed))
        {
            return new Bound(parsed);
        }

        // Step 3: The any-address is always bindable (0.0.0.0 or [::]).
        if (parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any))
        {
            return new Bound(parsed);
        }

        // Step 4: Check if the parsed address is present in the host's interface list.
        for (int i = 0; i < hostAddresses.Count; i++)
        {
            if (parsed.Equals(hostAddresses[i]))
            {
                return new Bound(parsed);
            }
        }

        // Step 5: Address is valid but not on this host — never substitute.
        return new Unavailable($"{addressString} is not an address on this host");
    }
}
