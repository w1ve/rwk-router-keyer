using Xunit;

namespace RWK.LiveNetwork.Tests;

/// <summary>
/// Live tailnet integration tests. These require a real Tailscale pre-auth key
/// in the RWK_TEST_AUTHKEY environment variable and a built sidecar binary.
/// Run explicitly with: dotnet test --filter Category=LiveNetwork
/// </summary>
[Trait("Category", "LiveNetwork")]
public class LiveNetworkTests : IClassFixture<LiveNetworkFixture>
{
    private readonly LiveNetworkFixture _fixture;

    public LiveNetworkTests(LiveNetworkFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Skip = "Requires RWK_TEST_AUTHKEY environment variable")]
    [Trait("Category", "LiveNetwork")]
    public void HarnessSkipsGracefully_WhenAuthKeyAbsent()
    {
        // This test demonstrates the skip mechanism.
        // When the auth key IS present, it will be skipped by the Skip attribute.
        // Real tests below use the SkipUnlessReady helper.
    }

    [SkippableFact]
    [Trait("Category", "LiveNetwork")]
    public async Task BothSidecars_ReachConnectedState()
    {
        SkipUnlessReady();

        var stationStatus = await _fixture.Station!.GetStatusAsync(CancellationToken.None);
        var clientStatus = await _fixture.Client!.GetStatusAsync(CancellationToken.None);

        Assert.Equal("Connected", stationStatus.State);
        Assert.Equal("Connected", clientStatus.State);
    }

    [SkippableFact]
    [Trait("Category", "LiveNetwork")]
    public async Task BothSidecars_ReportDirectPath()
    {
        SkipUnlessReady();

        var stationStatus = await _fixture.Station!.GetStatusAsync(CancellationToken.None);
        var clientStatus = await _fixture.Client!.GetStatusAsync(CancellationToken.None);

        Assert.Equal("Direct", stationStatus.Path);
        Assert.Equal("Direct", clientStatus.Path);
    }

    [SkippableFact]
    [Trait("Category", "LiveNetwork")]
    public async Task BothSidecars_ReportUserspaceMode()
    {
        SkipUnlessReady();

        var stationStatus = await _fixture.Station!.GetStatusAsync(CancellationToken.None);
        var clientStatus = await _fixture.Client!.GetStatusAsync(CancellationToken.None);

        Assert.True(stationStatus.Userspace, "Station should run in userspace mode");
        Assert.True(clientStatus.Userspace, "Client should run in userspace mode");
    }

    [SkippableFact]
    [Trait("Category", "LiveNetwork")]
    public async Task BothSidecars_ReportUdpEdgeTransport()
    {
        SkipUnlessReady();

        var stationStatus = await _fixture.Station!.GetStatusAsync(CancellationToken.None);
        var clientStatus = await _fixture.Client!.GetStatusAsync(CancellationToken.None);

        Assert.Equal("udp", stationStatus.Edge?.Transport);
        Assert.Equal("udp", clientStatus.Edge?.Transport);
    }

    private void SkipUnlessReady()
    {
        Skip.If(!_fixture.IsReady, _fixture.SkipReason ?? "Fixture not ready");
    }
}
