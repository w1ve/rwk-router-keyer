using System.Net;
using RWK.Shared.Config;
using RWK.Shared.Net;
using Xunit;

namespace RWK.Shared.Tests.Net;

/// <summary>
/// Unit tests for <see cref="BindAddressResolver.ResolveRuleBindAddress"/>.
/// Validates requirement 10.15: never substitute a different address.
/// </summary>
public class BindAddressResolverTests
{
    /// <summary>
    /// Helper to create a rule with the given bind address. Other fields are irrelevant
    /// to the resolver so we use arbitrary valid values.
    /// </summary>
    private static ForwardRule MakeRule(string bindAddress) =>
        new(Guid.NewGuid(), "Test", ForwardProtocol.Tcp, 5000, 5000, true, bindAddress);

    /// <summary>
    /// A representative host address list for tests that need a specific address to be present.
    /// </summary>
    private static readonly IReadOnlyList<IPAddress> HostAddresses = new[]
    {
        IPAddress.Parse("192.168.1.100"),
        IPAddress.Parse("10.0.0.5"),
        IPAddress.Parse("fe80::1"),
    };

    #region Loopback — always Bound

    [Fact]
    public void Loopback_IPv4_ReturnsBound()
    {
        var rule = MakeRule("127.0.0.1");

        var result = BindAddressResolver.ResolveRuleBindAddress(rule, HostAddresses);

        var bound = Assert.IsType<Bound>(result);
        Assert.True(IPAddress.IsLoopback(bound.Address));
        Assert.Equal(IPAddress.Parse("127.0.0.1"), bound.Address);
    }

    [Fact]
    public void Loopback_IPv6_ReturnsBound()
    {
        var rule = MakeRule("::1");

        var result = BindAddressResolver.ResolveRuleBindAddress(rule, HostAddresses);

        var bound = Assert.IsType<Bound>(result);
        Assert.True(IPAddress.IsLoopback(bound.Address));
        Assert.Equal(IPAddress.IPv6Loopback, bound.Address);
    }

    #endregion

    #region Any-address — always Bound

    [Fact]
    public void AnyAddress_IPv4_ReturnsBound()
    {
        var rule = MakeRule("0.0.0.0");

        var result = BindAddressResolver.ResolveRuleBindAddress(rule, HostAddresses);

        var bound = Assert.IsType<Bound>(result);
        Assert.Equal(IPAddress.Any, bound.Address);
    }

    [Fact]
    public void AnyAddress_IPv6_ReturnsBound()
    {
        var rule = MakeRule("::");

        var result = BindAddressResolver.ResolveRuleBindAddress(rule, HostAddresses);

        var bound = Assert.IsType<Bound>(result);
        Assert.Equal(IPAddress.IPv6Any, bound.Address);
    }

    #endregion

    #region Address present in hostAddresses — Bound

    [Fact]
    public void AddressPresentOnHost_ReturnsBound()
    {
        var rule = MakeRule("192.168.1.100");

        var result = BindAddressResolver.ResolveRuleBindAddress(rule, HostAddresses);

        var bound = Assert.IsType<Bound>(result);
        Assert.Equal(IPAddress.Parse("192.168.1.100"), bound.Address);
    }

    [Fact]
    public void AddressPresentOnHost_IPv6_ReturnsBound()
    {
        var rule = MakeRule("fe80::1");

        var result = BindAddressResolver.ResolveRuleBindAddress(rule, HostAddresses);

        var bound = Assert.IsType<Bound>(result);
        Assert.Equal(IPAddress.Parse("fe80::1"), bound.Address);
    }

    #endregion

    #region Address absent from hostAddresses — Unavailable

    [Fact]
    public void AddressAbsentFromHost_ReturnsUnavailable()
    {
        var rule = MakeRule("172.16.0.99");

        var result = BindAddressResolver.ResolveRuleBindAddress(rule, HostAddresses);

        var unavailable = Assert.IsType<Unavailable>(result);
        Assert.Contains("172.16.0.99", unavailable.Message);
        Assert.Contains("not an address on this host", unavailable.Message);
    }

    [Fact]
    public void AddressAbsentFromHost_EmptyHostList_ReturnsUnavailable()
    {
        var rule = MakeRule("192.168.1.100");
        var emptyList = Array.Empty<IPAddress>();

        var result = BindAddressResolver.ResolveRuleBindAddress(rule, emptyList);

        var unavailable = Assert.IsType<Unavailable>(result);
        Assert.Contains("192.168.1.100", unavailable.Message);
    }

    #endregion

    #region Unparseable strings — Invalid

    [Fact]
    public void UnparseableString_NotAnAddress_ReturnsInvalid()
    {
        var rule = MakeRule("not-an-address");

        var result = BindAddressResolver.ResolveRuleBindAddress(rule, HostAddresses);

        var invalid = Assert.IsType<Invalid>(result);
        Assert.Contains("not-an-address", invalid.Message);
        Assert.Contains("not a valid IP address", invalid.Message);
    }

    [Fact]
    public void UnparseableString_Empty_ReturnsInvalid()
    {
        var rule = MakeRule("");

        var result = BindAddressResolver.ResolveRuleBindAddress(rule, HostAddresses);

        var invalid = Assert.IsType<Invalid>(result);
        Assert.Contains("not a valid IP address", invalid.Message);
    }

    [Fact]
    public void UnparseableString_InterfaceName_ReturnsInvalid()
    {
        var rule = MakeRule("eth0");

        var result = BindAddressResolver.ResolveRuleBindAddress(rule, HostAddresses);

        var invalid = Assert.IsType<Invalid>(result);
        Assert.Contains("eth0", invalid.Message);
        Assert.Contains("not a valid IP address", invalid.Message);
    }

    #endregion

    #region No substitution guarantee

    [Fact]
    public void NeverSubstitutes_UnavailableAddressIsNotReplaced()
    {
        // The critical 10.15 guarantee: if the address is absent, the result
        // names THAT address — it does not silently substitute loopback or any.
        var rule = MakeRule("10.99.99.99");
        var hostList = new[] { IPAddress.Parse("192.168.1.1") };

        var result = BindAddressResolver.ResolveRuleBindAddress(rule, hostList);

        var unavailable = Assert.IsType<Unavailable>(result);
        // Message must name the REQUESTED address, not any substitute
        Assert.Contains("10.99.99.99", unavailable.Message);
    }

    #endregion

    #region Null argument handling

    [Fact]
    public void NullRule_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => BindAddressResolver.ResolveRuleBindAddress(null!, HostAddresses));
    }

    [Fact]
    public void NullHostAddresses_ThrowsArgumentNullException()
    {
        var rule = MakeRule("127.0.0.1");

        Assert.Throws<ArgumentNullException>(
            () => BindAddressResolver.ResolveRuleBindAddress(rule, null!));
    }

    #endregion
}
