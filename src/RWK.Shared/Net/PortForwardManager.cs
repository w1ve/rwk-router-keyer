using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RWK.Shared.Config;

namespace RWK.Shared.Net;

/// <summary>
/// Manages TCP and UDP port forwarding rules carried over the Tailscale tunnel.
/// </summary>
/// <remarks>
/// This implementation covers rule CRUD, Start/Stop lifecycle, status tracking with byte
/// counters, and <see cref="SetRuleBindAddress"/> which restarts only the affected rule's
/// listener. The TCP relay pump connects accepted clients to Station-side targets via a
/// tunnel dial delegate and pumps data bidirectionally with half-close propagation (10.4).
/// <para>
/// Bind address validation uses <see cref="BindAddressResolver.ResolveRuleBindAddress"/>
/// (task 17.8) before creating a listener. On Unavailable or Invalid the rule enters
/// <see cref="ForwardRuleStatus.Error"/> with the listener left unbound — never falls back
/// to loopback or the any-address (10.15).
/// </para>
/// <para>
/// NetworkChange address/availability notifications trigger re-evaluation of all rule
/// bindings so that rules whose interface returns can be re-bound, and rules whose
/// interface has gone can be errored, without an application restart (10.15, task 17.10).
/// </para>
/// <para>
/// RuleType is a label only for Generic, Cat, Audio, and RemoteRig: all use the same
/// TCP/UDP relay code path. Only FlexDiscovery has protocol-aware behavior elsewhere
/// (10.16, 10.17, task 17.12).
/// </para>
/// <para>
/// _Requirements: 10.1, 10.2, 10.3, 10.4, 10.7, 10.11, 10.13, 10.15, 10.16, 10.17_
/// </para>
/// </remarks>
public sealed class PortForwardManager : IPortForwardManager
{
    private readonly object _lock = new();
    private readonly List<ForwardRule> _rules = new();
    private readonly ConcurrentDictionary<Guid, RuleRuntime> _runtimes = new();
    private Func<int, CancellationToken, Task<Stream>>? _tunnelDial;
    private readonly Func<IReadOnlyList<IPAddress>>? _hostAddressProvider;
    private bool _running;
    private bool _disposed;
    private bool _networkChangeSubscribed;

    /// <summary>
    /// Creates a PortForwardManager without a tunnel dial delegate (no relay — for testing
    /// the CRUD/lifecycle layer or until the tunnel is connected).
    /// </summary>
    public PortForwardManager() : this(null, null) { }

    /// <summary>
    /// Creates a PortForwardManager with a tunnel dial delegate that opens a stream to a
    /// given Station port via the Tailscale tunnel.
    /// </summary>
    /// <param name="tunnelDial">
    /// A delegate that, given a Station port and a cancellation token, returns a connected
    /// stream. Typically <c>(port, ct) => tailscaleNode.ConnectControlAsync(peer, port)</c>.
    /// When <see langword="null"/>, TCP connections are accepted but closed immediately (no relay).
    /// </param>
    /// <param name="hostAddressProvider">
    /// A delegate that returns the current list of IP addresses on the host's network
    /// interfaces. Used by <see cref="BindAddressResolver"/> to validate bind addresses.
    /// When <see langword="null"/>, defaults to <see cref="GetDefaultHostAddresses"/>.
    /// </param>
    public PortForwardManager(
        Func<int, CancellationToken, Task<Stream>>? tunnelDial,
        Func<IReadOnlyList<IPAddress>>? hostAddressProvider = null)
    {
        _tunnelDial = tunnelDial;
        _hostAddressProvider = hostAddressProvider;
    }

    /// <inheritdoc />
    public IReadOnlyList<ForwardRule> Rules
    {
        get
        {
            lock (_lock)
            {
                return _rules.ToList().AsReadOnly();
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ForwardRuleStatusChangedEventArgs>? RuleStatusChanged;

    /// <summary>
    /// Sets or replaces the tunnel dial delegate. Called by the controller once the
    /// tailnet peer address is known (after session establishment). Existing rules
    /// that are already listening will use the new delegate for subsequent connections.
    /// </summary>
    public Func<int, CancellationToken, Task<Stream>>? TunnelDial
    {
        get => _tunnelDial;
        set => _tunnelDial = value;
    }

    /// <summary>
    /// Delegate that creates an outbound UDP forward on the sidecar and returns the
    /// loopback endpoint to send datagrams to. The sidecar relays them over the tailnet.
    /// When null, UDP rules relay only locally (no tunnel).
    /// </summary>
    public Func<int, CancellationToken, Task<System.Net.IPEndPoint>>? UdpTunnelBind { get; set; }

    /// <inheritdoc />
    public void AddRule(ForwardRule rule)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(rule);

        ValidateRule(rule);

        lock (_lock)
        {
            if (_rules.Any(r => r.Id == rule.Id))
                throw new ArgumentException($"A rule with Id '{rule.Id}' already exists.", nameof(rule));

            // Check for port conflicts with existing rules (same ClientPort + Protocol + BindAddress).
            var conflict = _rules.FirstOrDefault(r =>
                r.ClientPort == rule.ClientPort &&
                r.Protocol == rule.Protocol &&
                string.Equals(r.BindAddress, rule.BindAddress, StringComparison.OrdinalIgnoreCase));

            if (conflict is not null)
            {
                throw new ArgumentException(
                    $"Port {rule.ClientPort}/{rule.Protocol} on {rule.BindAddress} is already used by rule '{conflict.Name}'.",
                    nameof(rule));
            }

            _rules.Add(rule);
            _runtimes[rule.Id] = new RuleRuntime(rule.Id);

            if (_running && rule.Enabled)
            {
                StartRuleListener(rule);
            }
        }
    }

    /// <summary>
    /// Ports reserved by the RWK application. Cannot be used for port forwards.
    /// </summary>
    public static readonly HashSet<int> ReservedPorts = new() { 7373, 41373 };

    /// <summary>
    /// Validates a forward rule before adding it. Throws <see cref="ArgumentException"/>
    /// if the rule uses a reserved port or has invalid port numbers.
    /// </summary>
    public static void ValidateRule(ForwardRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.ClientPort <= 0 || rule.ClientPort > 65535)
            throw new ArgumentException($"Client port must be 1-65535, got {rule.ClientPort}.", nameof(rule));

        if (rule.StationPort <= 0 || rule.StationPort > 65535)
            throw new ArgumentException($"Station port must be 1-65535, got {rule.StationPort}.", nameof(rule));

        if (ReservedPorts.Contains(rule.ClientPort))
            throw new ArgumentException(
                $"Client port {rule.ClientPort} is reserved by RWK and cannot be used for port forwards.",
                nameof(rule));

        if (ReservedPorts.Contains(rule.StationPort))
            throw new ArgumentException(
                $"Station port {rule.StationPort} is reserved by RWK and cannot be used for port forwards.",
                nameof(rule));
    }

    /// <inheritdoc />
    public void RemoveRule(Guid ruleId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            int index = _rules.FindIndex(r => r.Id == ruleId);
            if (index < 0)
                throw new KeyNotFoundException($"No rule with Id '{ruleId}' exists.");

            StopRuleListener(ruleId);
            _rules.RemoveAt(index);
            _runtimes.TryRemove(ruleId, out _);
        }
    }

    /// <inheritdoc />
    public void SetRuleEnabled(Guid ruleId, bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            int index = _rules.FindIndex(r => r.Id == ruleId);
            if (index < 0)
                throw new KeyNotFoundException($"No rule with Id '{ruleId}' exists.");

            ForwardRule existing = _rules[index];
            if (existing.Enabled == enabled)
                return;

            _rules[index] = existing with { Enabled = enabled };

            if (_running)
            {
                if (enabled)
                {
                    StartRuleListener(_rules[index]);
                }
                else
                {
                    StopRuleListener(ruleId);
                    RaiseStatusChanged(ruleId, ForwardRuleStatus.Idle);
                }
            }
        }
    }

    /// <inheritdoc />
    public void SetRuleBindAddress(Guid ruleId, string bindAddress)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindAddress);

        lock (_lock)
        {
            int index = _rules.FindIndex(r => r.Id == ruleId);
            if (index < 0)
                throw new KeyNotFoundException($"No rule with Id '{ruleId}' exists.");

            ForwardRule existing = _rules[index];
            if (string.Equals(existing.BindAddress, bindAddress, StringComparison.OrdinalIgnoreCase))
                return;

            _rules[index] = existing with { BindAddress = bindAddress };

            // Restart only the affected rule's listener (10.11, 10.13).
            if (_running && _rules[index].Enabled)
            {
                StopRuleListener(ruleId);
                StartRuleListener(_rules[index]);
            }
        }
    }

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_running) return;
            _running = true;

            // Subscribe to network change notifications so bindings re-evaluate when
            // interfaces come or go, without requiring an application restart (10.15, 17.10).
            SubscribeNetworkChange();

            foreach (ForwardRule rule in _rules)
            {
                if (rule.Enabled)
                {
                    StartRuleListener(rule);
                }
            }
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (!_running) return;
            _running = false;

            UnsubscribeNetworkChange();

            foreach (ForwardRule rule in _rules)
            {
                StopRuleListener(rule.Id);
                RaiseStatusChanged(rule.Id, ForwardRuleStatus.Idle);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnsubscribeNetworkChange();

        lock (_lock)
        {
            _running = false;
            foreach (ForwardRule rule in _rules)
            {
                StopRuleListener(rule.Id);
            }
        }
    }

    // ---- Byte counter accessors for downstream relay tasks ----

    /// <summary>
    /// Gets the current byte counters for a rule. Used by the relay pump implementations
    /// (tasks 17.2, 17.4) to report cumulative transfer.
    /// </summary>
    /// <param name="ruleId">The rule to query.</param>
    /// <returns>A tuple of (BytesIn, BytesOut), or (0, 0) if the rule is unknown.</returns>
    public (long BytesIn, long BytesOut) GetByteCounters(Guid ruleId)
    {
        if (_runtimes.TryGetValue(ruleId, out RuleRuntime? rt))
            return (Interlocked.Read(ref rt.BytesIn), Interlocked.Read(ref rt.BytesOut));
        return (0, 0);
    }

    /// <summary>
    /// Adds to the inbound byte counter for a rule and raises the status changed event.
    /// Called by relay pump implementations (tasks 17.2, 17.4).
    /// </summary>
    public void AddBytesIn(Guid ruleId, long count)
    {
        if (_runtimes.TryGetValue(ruleId, out RuleRuntime? rt))
        {
            Interlocked.Add(ref rt.BytesIn, count);
            RaiseStatusChanged(ruleId, ForwardRuleStatus.Active);
        }
    }

    /// <summary>
    /// Adds to the outbound byte counter for a rule and raises the status changed event.
    /// Called by relay pump implementations (tasks 17.2, 17.4).
    /// </summary>
    public void AddBytesOut(Guid ruleId, long count)
    {
        if (_runtimes.TryGetValue(ruleId, out RuleRuntime? rt))
        {
            Interlocked.Add(ref rt.BytesOut, count);
            RaiseStatusChanged(ruleId, ForwardRuleStatus.Active);
        }
    }

    // ---- Private lifecycle ----

    private void StartRuleListener(ForwardRule rule)
    {
        if (!_runtimes.TryGetValue(rule.Id, out RuleRuntime? rt))
            return;

        // Already listening — stop first (e.g., restart path).
        if (rt.Listener is not null || rt.UdpClient is not null)
            StopRuleListener(rule.Id);

        // Resolve the bind address via BindAddressResolver (task 17.8/17.10).
        // This is pure — opens no sockets and never substitutes a different address.
        IReadOnlyList<IPAddress> hostAddresses = GetHostAddresses();
        BindResolution resolution = BindAddressResolver.ResolveRuleBindAddress(rule, hostAddresses);

        switch (resolution)
        {
            case Invalid invalid:
                rt.Status = ForwardRuleStatus.Error;
                RaiseStatusChanged(rule.Id, ForwardRuleStatus.Error, invalid.Message);
                return;

            case Unavailable unavailable:
                rt.Status = ForwardRuleStatus.Error;
                RaiseStatusChanged(rule.Id, ForwardRuleStatus.Error, unavailable.Message);
                return;

            case Bound bound:
                // Proceed to bind with the validated address.
                // Dispatch on Protocol only (task 17.12): Generic, Cat, Audio, and RemoteRig
                // all use the same TCP or UDP relay. RuleType is a label only — no payload
                // inspection, no rewriting, no special handling. Only FlexDiscovery has
                // protocol-aware behavior, and that lives in the DiscoveryEmitter, not here.
                // (10.16, 10.17)
                try
                {
                    if (rule.Protocol == ForwardProtocol.Tcp)
                    {
                        StartTcpListener(rule, bound.Address, rt);
                    }
                    else
                    {
                        StartUdpListener(rule, bound.Address, rt);
                    }

                    rt.Status = ForwardRuleStatus.Listening;
                    RaiseStatusChanged(rule.Id, ForwardRuleStatus.Listening);
                }
                catch (SocketException ex)
                {
                    // Bind failure — could be port in use, etc.
                    rt.Listener?.Dispose();
                    rt.Listener = null;
                    rt.UdpClient?.Dispose();
                    rt.UdpClient = null;
                    rt.Status = ForwardRuleStatus.Error;
                    RaiseStatusChanged(rule.Id, ForwardRuleStatus.Error,
                        $"Cannot bind to {rule.BindAddress}:{rule.ClientPort} — {ex.SocketErrorCode}: {ex.Message}");
                }
                return;

            default:
                // Defensive: unknown BindResolution variant — treat as error.
                rt.Status = ForwardRuleStatus.Error;
                RaiseStatusChanged(rule.Id, ForwardRuleStatus.Error,
                    $"Unexpected bind resolution for '{rule.BindAddress}'.");
                return;
        }
    }

    private void StartTcpListener(ForwardRule rule, IPAddress address, RuleRuntime rt)
    {
        var listener = new TcpListener(address, rule.ClientPort);
        listener.Start();
        rt.Listener = listener;
        rt.ListenerCts = new CancellationTokenSource();
        // Begin accepting — relay each connection via TcpForwarder (10.2, 10.3, 10.4).
        _ = AcceptTcpConnectionsAsync(rule.Id, rule, listener, rt.ListenerCts.Token);
    }

    private void StartUdpListener(ForwardRule rule, IPAddress address, RuleRuntime rt)
    {
        var endpoint = new IPEndPoint(address, rule.ClientPort);
        var udpClient = new UdpClient(endpoint);
        rt.UdpClient = udpClient;
        rt.ListenerCts = new CancellationTokenSource();

        // Determine the destination: if UdpTunnelBind is available, ask the sidecar
        // for an outbound-udp forward and send datagrams there. Otherwise fall back
        // to a local relay (useful for testing without tailnet).
        IPEndPoint destination;
        if (UdpTunnelBind is not null)
        {
            try
            {
                destination = UdpTunnelBind(rule.StationPort, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch
            {
                // Sidecar not available — fall back to local destination.
                destination = new IPEndPoint(IPAddress.Loopback, rule.StationPort);
            }
        }
        else
        {
            destination = new IPEndPoint(IPAddress.Loopback, rule.StationPort);
        }

        var forwarder = new UdpForwarder(udpClient, destination, rule.Id, this);
        rt.UdpForwarder = forwarder;
        _ = forwarder.RunAsync(rt.ListenerCts.Token);
    }

    private async Task AcceptTcpConnectionsAsync(Guid ruleId, ForwardRule rule, TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);

                if (_tunnelDial is null)
                {
                    // No tunnel available — cannot relay.
                    client.Dispose();
                    continue;
                }

                // Spawn a relay task per connection (10.3: multiple simultaneous connections).
                _ = HandleTcpConnectionAsync(ruleId, rule, client, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Listener was stopped.
        }
        catch (SocketException)
        {
            // Listener socket error during accept.
        }
    }

    private async Task HandleTcpConnectionAsync(Guid ruleId, ForwardRule rule, TcpClient client, CancellationToken ct)
    {
        TcpForwarder? forwarder = null;
        try
        {
            // Dial the Station-side target port via the tunnel.
            Stream remoteStream = await _tunnelDial!(rule.StationPort, ct).ConfigureAwait(false);

            forwarder = new TcpForwarder(
                client,
                remoteStream,
                addBytesIn: count => AddBytesIn(ruleId, count),
                addBytesOut: count => AddBytesOut(ruleId, count),
                ct);

            // Track the connection for clean disposal when the rule is stopped.
            if (_runtimes.TryGetValue(ruleId, out RuleRuntime? rt))
            {
                rt.ActiveConnections.TryAdd(forwarder, 0);
                RaiseStatusChanged(ruleId, ForwardRuleStatus.Active);
            }

            await forwarder.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Rule stopped while dialing.
        }
        catch (Exception)
        {
            // Tunnel dial failure or unexpected error — close the client.
            client.Dispose();
        }
        finally
        {
            if (forwarder is not null && _runtimes.TryGetValue(ruleId, out RuleRuntime? rt2))
            {
                rt2.ActiveConnections.TryRemove(forwarder, out _);
                forwarder.Dispose();

                // If no more active connections, revert to Listening status.
                if (rt2.ActiveConnections.IsEmpty && rt2.Status == ForwardRuleStatus.Active)
                {
                    RaiseStatusChanged(ruleId, ForwardRuleStatus.Listening);
                }
            }
        }
    }

    private void StopRuleListener(Guid ruleId)
    {
        if (!_runtimes.TryGetValue(ruleId, out RuleRuntime? rt))
            return;

        rt.ListenerCts?.Cancel();
        rt.ListenerCts?.Dispose();
        rt.ListenerCts = null;

        if (rt.UdpForwarder is not null)
        {
            rt.UdpForwarder.Dispose();
            rt.UdpForwarder = null;
        }

        if (rt.Listener is not null)
        {
            try { rt.Listener.Stop(); } catch { /* best-effort cleanup */ }
            rt.Listener.Dispose();
            rt.Listener = null;
        }

        if (rt.UdpClient is not null)
        {
            try { rt.UdpClient.Close(); } catch { /* best-effort cleanup */ }
            rt.UdpClient.Dispose();
            rt.UdpClient = null;
        }

        // Dispose all active TCP relay connections for this rule (clean disposal on rule stop).
        foreach (TcpForwarder fwd in rt.ActiveConnections.Keys)
        {
            try { fwd.Dispose(); } catch { /* best-effort cleanup */ }
        }
        rt.ActiveConnections.Clear();

        rt.Status = ForwardRuleStatus.Idle;
    }

    private void RaiseStatusChanged(Guid ruleId, ForwardRuleStatus status, string? message = null)
    {
        if (!_runtimes.TryGetValue(ruleId, out RuleRuntime? rt))
            return;

        rt.Status = status;

        RuleStatusChanged?.Invoke(this, new ForwardRuleStatusChangedEventArgs(
            ruleId,
            status,
            Interlocked.Read(ref rt.BytesIn),
            Interlocked.Read(ref rt.BytesOut),
            message));
    }

    // ---- Network change subscription (task 17.10) ----

    /// <summary>
    /// Gets the current host addresses. Uses the injected provider if available,
    /// otherwise falls back to Dns.GetHostAddresses with the machine name.
    /// </summary>
    private IReadOnlyList<IPAddress> GetHostAddresses()
    {
        if (_hostAddressProvider is not null)
            return _hostAddressProvider();

        return GetDefaultHostAddresses();
    }

    /// <summary>
    /// Default host address provider: queries all local addresses via
    /// <see cref="NetworkInterface.GetAllNetworkInterfaces"/>.
    /// </summary>
    internal static IReadOnlyList<IPAddress> GetDefaultHostAddresses()
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;

                var props = iface.GetIPProperties();
                foreach (var unicast in props.UnicastAddresses)
                {
                    addresses.Add(unicast.Address);
                }
            }
        }
        catch
        {
            // Fallback: if enumeration fails, return empty — rules will get Unavailable.
        }
        return addresses;
    }

    private void SubscribeNetworkChange()
    {
        if (_networkChangeSubscribed) return;
        _networkChangeSubscribed = true;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    private void UnsubscribeNetworkChange()
    {
        if (!_networkChangeSubscribed) return;
        _networkChangeSubscribed = false;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        ReEvaluateBindings();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        ReEvaluateBindings();
    }

    /// <summary>
    /// Re-evaluates all enabled rule bindings after a network change.
    /// Rules whose interface has returned are re-bound; rules whose interface has gone
    /// are errored — without an application restart (10.15, task 17.10).
    /// </summary>
    public void ReEvaluateBindings()
    {
        lock (_lock)
        {
            if (!_running) return;

            IReadOnlyList<IPAddress> currentAddresses = GetHostAddresses();

            foreach (ForwardRule rule in _rules)
            {
                if (!rule.Enabled) continue;
                if (!_runtimes.TryGetValue(rule.Id, out RuleRuntime? rt)) continue;

                BindResolution resolution = BindAddressResolver.ResolveRuleBindAddress(rule, currentAddresses);

                if (resolution is Bound && rt.Status == ForwardRuleStatus.Error)
                {
                    // Address has come back — re-bind.
                    StartRuleListener(rule);
                }
                else if (resolution is Unavailable unavailable && rt.Status != ForwardRuleStatus.Error)
                {
                    // Address has gone — error the rule.
                    StopRuleListener(rule.Id);
                    rt.Status = ForwardRuleStatus.Error;
                    RaiseStatusChanged(rule.Id, ForwardRuleStatus.Error, unavailable.Message);
                }
                else if (resolution is Invalid invalid && rt.Status != ForwardRuleStatus.Error)
                {
                    StopRuleListener(rule.Id);
                    rt.Status = ForwardRuleStatus.Error;
                    RaiseStatusChanged(rule.Id, ForwardRuleStatus.Error, invalid.Message);
                }
            }
        }
    }

    // ---- Per-rule runtime state ----

    private sealed class RuleRuntime
    {
        public RuleRuntime(Guid ruleId)
        {
            RuleId = ruleId;
        }

        public Guid RuleId { get; }
        public ForwardRuleStatus Status = ForwardRuleStatus.Idle;
        public long BytesIn;
        public long BytesOut;
        public TcpListener? Listener;
        public UdpClient? UdpClient;
        public CancellationTokenSource? ListenerCts;
        public UdpForwarder? UdpForwarder;

        /// <summary>
        /// Active TCP relay connections for this rule. Used to dispose all connections
        /// when the rule is stopped (clean disposal requirement).
        /// </summary>
        public ConcurrentDictionary<TcpForwarder, byte> ActiveConnections = new();
    }
}
