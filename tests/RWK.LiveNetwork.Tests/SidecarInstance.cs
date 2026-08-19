/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RWK.LiveNetwork.Tests;

/// <summary>
/// Represents one running sidecar process and exposes its HTTP control API.
/// </summary>
public sealed class SidecarInstance : IAsyncDisposable
{
    private readonly Process _process;
    private readonly HttpClient _http;
    private readonly string _stateDir;
    private bool _disposed;

    public string Hostname { get; }
    public string ApiAddress { get; private set; } = string.Empty;
    public string Token { get; private set; } = string.Empty;
    public string EdgeLocalAddress { get; private set; } = string.Empty;
    public string EdgeTransport { get; private set; } = string.Empty;
    public int Pid { get; private set; }

    private SidecarInstance(Process process, string hostname, string stateDir)
    {
        _process = process;
        _stateDir = stateDir;
        Hostname = hostname;
        _http = new HttpClient();
    }

    /// <summary>
    /// Launches a sidecar instance with the given hostname and state directory.
    /// Reads the handshake line from stdout.
    /// </summary>
    public static async Task<SidecarInstance> LaunchAsync(
        string sidecarPath,
        string hostname,
        string stateDir,
        CancellationToken ct)
    {
        Directory.CreateDirectory(stateDir);

        var psi = new ProcessStartInfo
        {
            FileName = sidecarPath,
            Arguments = $"-api-addr 127.0.0.1:0 -edge-local-addr 127.0.0.1:0 -edge-tailnet-port 0 -hostname {hostname} -state-dir \"{stateDir}\" -watchdog 30s -ephemeral",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start sidecar at {sidecarPath}");

        var instance = new SidecarInstance(process, hostname, stateDir);

        // Read the single JSON handshake line from stdout
        var handshakeTask = process.StandardOutput.ReadLineAsync(ct);
        var completed = await Task.WhenAny(handshakeTask.AsTask(), Task.Delay(TimeSpan.FromSeconds(15), ct));

        if (completed != handshakeTask.AsTask())
        {
            process.Kill();
            throw new TimeoutException($"Sidecar '{hostname}' did not emit handshake within 15 seconds.");
        }

        var line = await handshakeTask;
        if (string.IsNullOrWhiteSpace(line))
        {
            process.Kill();
            throw new InvalidOperationException($"Sidecar '{hostname}' emitted empty handshake.");
        }

        var handshake = JsonSerializer.Deserialize<SidecarHandshake>(line)
            ?? throw new InvalidOperationException($"Sidecar '{hostname}' handshake deserialized to null.");

        if (handshake.Protocol != 1)
        {
            process.Kill();
            throw new InvalidOperationException($"Sidecar '{hostname}' reported unsupported protocol {handshake.Protocol}.");
        }

        instance.ApiAddress = handshake.ApiAddress;
        instance.Token = handshake.Token;
        instance.EdgeLocalAddress = handshake.EdgeLocalAddress;
        instance.EdgeTransport = handshake.EdgeTransport;
        instance.Pid = handshake.Pid;

        instance._http.BaseAddress = new Uri($"http://{instance.ApiAddress}");
        instance._http.DefaultRequestHeaders.Add("X-RWK-Token", instance.Token);

        return instance;
    }

    /// <summary>
    /// POST /v1/start with the auth key to join the tailnet.
    /// </summary>
    public async Task StartAsync(string authKey, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { authKey });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("/v1/start", content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// POST /v1/peer to set the peer address and edge port.
    /// </summary>
    public async Task SetPeerAsync(string address, int edgePort, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { address, edgePort });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("/v1/peer", content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// GET /v1/status — returns the full status document.
    /// </summary>
    public async Task<SidecarStatus> GetStatusAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync("/v1/status", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<SidecarStatus>(json)
            ?? throw new InvalidOperationException($"Status response from '{Hostname}' deserialized to null.");
    }

    /// <summary>
    /// POST /v1/stop to leave the tailnet.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        try
        {
            var response = await _http.PostAsync("/v1/stop", null, ct);
            // Best-effort; don't throw if the sidecar is already dying
        }
        catch
        {
            // Swallow — we're tearing down
        }
    }

    /// <summary>
    /// Polls /v1/status until the predicate is satisfied or timeout elapses.
    /// </summary>
    public async Task<SidecarStatus> WaitForStatusAsync(
        Func<SidecarStatus, bool> predicate,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                var status = await GetStatusAsync(cts.Token);
                if (predicate(status))
                    return status;
            }
            catch (HttpRequestException)
            {
                // API not ready yet
            }
            catch (TaskCanceledException) when (cts.Token.IsCancellationRequested)
            {
                break;
            }

            await Task.Delay(pollInterval, cts.Token);
        }

        throw new TimeoutException(
            $"Sidecar '{Hostname}' did not reach expected state within {timeout.TotalSeconds}s.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // 1. POST /v1/stop
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await StopAsync(cts.Token);

        // 2. Release stdin (triggers parent-death exit in sidecar)
        try { _process.StandardInput.Close(); } catch { }

        // 3. Wait for exit
        try
        {
            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(exitCts.Token);
        }
        catch
        {
            // 4. Force kill if still alive
            try { _process.Kill(entireProcessTree: true); } catch { }
        }

        _process.Dispose();
        _http.Dispose();

        // 5. Clean up state directory
        try
        {
            if (Directory.Exists(_stateDir))
                Directory.Delete(_stateDir, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }
}

/// <summary>
/// JSON model for the sidecar handshake line written to stdout.
/// </summary>
public sealed class SidecarHandshake
{
    [JsonPropertyName("protocol")]
    public int Protocol { get; set; }

    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("apiAddress")]
    public string ApiAddress { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("edgeLocalAddress")]
    public string EdgeLocalAddress { get; set; } = string.Empty;

    [JsonPropertyName("edgeTransport")]
    public string EdgeTransport { get; set; } = string.Empty;
}

/// <summary>
/// JSON model for the /v1/status response (relevant fields).
/// </summary>
public sealed class SidecarStatus
{
    [JsonPropertyName("protocol")]
    public int Protocol { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("backendState")]
    public string BackendState { get; set; } = string.Empty;

    [JsonPropertyName("userspace")]
    public bool Userspace { get; set; }

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("selfAddress")]
    public string SelfAddress { get; set; } = string.Empty;

    [JsonPropertyName("selfDnsName")]
    public string SelfDnsName { get; set; } = string.Empty;

    [JsonPropertyName("peerAddress")]
    public string PeerAddress { get; set; } = string.Empty;

    [JsonPropertyName("peerOnline")]
    public bool PeerOnline { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("roundTripMs")]
    public double RoundTripMs { get; set; }

    [JsonPropertyName("derpRegion")]
    public string DerpRegion { get; set; } = string.Empty;

    [JsonPropertyName("edge")]
    public SidecarEdgeStatus? Edge { get; set; }
}

/// <summary>
/// JSON model for the edge sub-object in the status document.
/// </summary>
public sealed class SidecarEdgeStatus
{
    [JsonPropertyName("transport")]
    public string Transport { get; set; } = string.Empty;

    [JsonPropertyName("jitterProfile")]
    public string JitterProfile { get; set; } = string.Empty;

    [JsonPropertyName("tailnetPort")]
    public int TailnetPort { get; set; }
}
