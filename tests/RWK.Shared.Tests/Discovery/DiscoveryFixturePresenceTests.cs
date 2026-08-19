using RWK.Shared.Discovery;
using Xunit;

namespace RWK.Shared.Tests.Discovery;

/// <summary>
/// Gates the discovery payload codec on a datagram captured from physical FlexRadio
/// 6000-series hardware, as requirement 15.20 demands.
/// </summary>
/// <remarks>
/// Task 27.1. <b>These tests fail on purpose while the capture is absent.</b> That is the
/// deliverable: the payload field layout is not established fact, every concrete value in
/// <c>design.md</c> is marked <c>[VERIFY]</c>, and the codec written against it (task 27.2)
/// is provisional until a real datagram confirms the layout. A failing test makes that
/// provisional state loud in CI instead of silent.
/// <para>
/// They are not <c>[Fact(Skip = ...)]</c> and must not become skips or no-ops. A skipped test
/// is invisible in most CI summaries, which is the failure mode this exists to prevent. They
/// go green with no code change the moment the two fixture files land.
/// </para>
/// _Requirements: 15.20_
/// </remarks>
public class DiscoveryFixturePresenceTests
{
    [Fact]
    public void Captured_discovery_datagram_is_present_as_a_fixture()
    {
        // FAILS UNTIL THE CAPTURE LANDS. See DiscoveryFixture.MissingPayloadMessage for what
        // to do; the assertion message names the exact path and the README beside it.
        Assert.True(DiscoveryFixture.PayloadExists, DiscoveryFixture.MissingPayloadMessage());

        byte[] payload = DiscoveryFixture.RequirePayload();

        // A zero-length file means the export produced nothing — worse than an absent file,
        // because it looks like the capture landed.
        Assert.True(
            payload.Length > 0,
            $"""
            The discovery fixture exists but is empty: {DiscoveryFixture.PayloadPath}

            "Export Packet Bytes..." was likely run against the wrong node in the packet detail
            tree, or against a packet with no UDP payload. Re-export the UDP payload ("Data")
            node of a datagram sent by the radio.
            """);

        // The fixture has to be able to cross the control channel as a DiscoveryAnnounce
        // (15.2). If it cannot, either the export included the Ethernet/IP/UDP headers or it
        // is not a single datagram body.
        Assert.True(
            payload.Length <= DiscoveryAnnounce.MaxRawPayloadBytes,
            $"""
            The discovery fixture is {payload.Length} bytes, above the
            {DiscoveryAnnounce.MaxRawPayloadBytes}-byte control-channel payload cap
            (DiscoveryAnnounce.MaxRawPayloadBytes), so it could never be forwarded to the Client.

            A single discovery datagram body sits far below that bound. Check that the export is
            the UDP payload alone — no Ethernet, IP, or UDP headers — and covers one datagram
            rather than a whole capture file.
              File: {DiscoveryFixture.PayloadPath}
            """);
    }

    [Fact]
    public void Capture_metadata_is_present_and_fully_filled_in()
    {
        // FAILS UNTIL THE CAPTURE LANDS. The failure reason names each field still holding the
        // template's null placeholder.
        Assert.True(
            DiscoveryFixture.TryLoadMetadata(out DiscoveryFixtureMetadata? metadata, out string? failureReason),
            failureReason);

        // The metadata's own account of the datagram must agree with the bytes checked in
        // beside it, otherwise the recorded length or the fixture is from a different capture.
        Assert.True(DiscoveryFixture.PayloadExists, DiscoveryFixture.MissingPayloadMessage());

        byte[] payload = DiscoveryFixture.RequirePayload();
        Assert.True(
            metadata!.DatagramLengthBytes == payload.Length,
            $"""
            Recorded datagram length disagrees with the checked-in fixture.

              {DiscoveryFixture.MetadataFileName} datagramLengthBytes : {metadata.DatagramLengthBytes}
              {DiscoveryFixture.PayloadFileName} actual size          : {payload.Length}

            The recorded length is the Wireshark UDP "Length" field minus the 8-byte UDP header.
            A mismatch usually means the export included headers, or the metadata describes a
            different datagram than the one saved.
            """);

        Assert.True(
            metadata.FixtureFile == DiscoveryFixture.PayloadFileName,
            $"Metadata fixtureFile is '{metadata.FixtureFile}', expected '{DiscoveryFixture.PayloadFileName}'.");
    }

    [Fact]
    public void Fixture_directory_carries_the_capture_instructions_and_the_metadata_template()
    {
        // These two are checked in already, so this passes today. It guards them: without the
        // README and the template, the two failing tests above lose the guidance that makes
        // them actionable, and the fixture directory becomes an unexplained empty folder.
        Assert.True(
            File.Exists(DiscoveryFixture.ReadmePath),
            $"Capture instructions are missing: {DiscoveryFixture.ReadmePath}");

        Assert.True(
            File.Exists(DiscoveryFixture.MetadataTemplatePath),
            $"Metadata template is missing: {DiscoveryFixture.MetadataTemplatePath}");
    }
}
