using Xunit;

namespace RWK.LiveNetwork.Tests;

/// <summary>
/// xUnit class fixture that launches two sidecar instances, joins them to the same
/// tailnet, sets each as the other's peer, and waits for Connected + Direct path.
/// Disposes cleanly on teardown.
/// </summary>
public sealed class LiveNetworkFixture : IAsyncLifetime
{
    private const string AuthKeyEnvVar = "RWK_TEST_AUTHKEY";
    private const string SidecarRelativePath = @"src\RWK.TailscaleSidecar\rwk-tailscale-sidecar.exe";
    private const string StationHostname = "rwk-test-station";
    private const string ClientHostname = "rwk-test-client";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DirectTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>The "station" sidecar instance.</summary>
    public SidecarInstance? Station { get; private set; }

    /// <summary>The "client" sidecar instance.</summary>
    public SidecarInstance? Client { get; private set; }

    /// <summary>Whether the fixture was initialized successfully (both connected with Direct path).</summary>
    public bool IsReady { get; private set; }

    /// <summary>Reason the fixture skipped (null when ready).</summary>
    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        // Check for auth key
        var authKey = Environment.GetEnvironmentVariable(AuthKeyEnvVar);
        if (string.IsNullOrWhiteSpace(authKey))
        {
            SkipReason = $"Environment variable '{AuthKeyEnvVar}' is not set. Live network tests require a Tailscale pre-auth key.";
            return;
        }

        // Locate sidecar binary relative to the solution root
        var sidecarPath = ResolveSidecarPath();
        if (!File.Exists(sidecarPath))
        {
            SkipReason = $"Sidecar binary not found at '{sidecarPath}'. Build the Go sidecar first (see src/RWK.TailscaleSidecar/README.md).";
            return;
        }

        // Create unique state directories in temp
        var baseTempDir = Path.Combine(Path.GetTempPath(), "rwk-live-tests", Guid.NewGuid().ToString("N"));
        var stationStateDir = Path.Combine(baseTempDir, "station");
        var clientStateDir = Path.Combine(baseTempDir, "client");

        var ct = CancellationToken.None;

        // Launch both sidecars
        Station = await SidecarInstance.LaunchAsync(sidecarPath, StationHostname, stationStateDir, ct);
        Client = await SidecarInstance.LaunchAsync(sidecarPath, ClientHostname, clientStateDir, ct);

        // POST /v1/start to both with the auth key
        await Station.StartAsync(authKey, ct);
        await Client.StartAsync(authKey, ct);

        // Wait for both to report Connected
        await Station.WaitForStatusAsync(
            s => string.Equals(s.State, "Connected", StringComparison.OrdinalIgnoreCase),
            ConnectTimeout, PollInterval, ct);

        await Client.WaitForStatusAsync(
            s => string.Equals(s.State, "Connected", StringComparison.OrdinalIgnoreCase),
            ConnectTimeout, PollInterval, ct);

        // Get addresses for peer setup
        var stationStatus = await Station.GetStatusAsync(ct);
        var clientStatus = await Client.GetStatusAsync(ct);

        // Set each as the other's peer
        var stationEdgePort = stationStatus.Edge?.TailnetPort ?? 0;
        var clientEdgePort = clientStatus.Edge?.TailnetPort ?? 0;

        await Client.SetPeerAsync(stationStatus.SelfAddress, stationEdgePort, ct);
        await Station.SetPeerAsync(clientStatus.SelfAddress, clientEdgePort, ct);

        // Wait for Direct path on both
        await Station.WaitForStatusAsync(
            s => string.Equals(s.Path, "Direct", StringComparison.OrdinalIgnoreCase),
            DirectTimeout, PollInterval, ct);

        await Client.WaitForStatusAsync(
            s => string.Equals(s.Path, "Direct", StringComparison.OrdinalIgnoreCase),
            DirectTimeout, PollInterval, ct);

        IsReady = true;
    }

    public async Task DisposeAsync()
    {
        if (Station != null)
            await Station.DisposeAsync();

        if (Client != null)
            await Client.DisposeAsync();
    }

    /// <summary>
    /// Resolves the sidecar binary path relative to the solution root.
    /// Walks up from the test assembly's location until it finds RWK.sln.
    /// </summary>
    private static string ResolveSidecarPath()
    {
        var dir = AppContext.BaseDirectory;

        // Walk up to find the solution root (contains RWK.sln)
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "RWK.sln")))
                return Path.Combine(dir, SidecarRelativePath);

            dir = Path.GetDirectoryName(dir);
        }

        // Fallback: assume we're running from the repo root
        return SidecarRelativePath;
    }
}
