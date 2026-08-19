using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace RWK.Shared.Tests.Discovery;

/// <summary>
/// Locates and loads the FlexRadio 6000-series discovery datagram captured from physical
/// hardware, which requirement 15.20 requires the test suite to carry as a fixture.
/// </summary>
/// <remarks>
/// Task 27.1. The capture is a <b>prerequisite deliverable</b> for the payload codec
/// (task 27.2, design Component 12): the payload field layout is not established fact, and
/// every concrete value in <c>design.md</c> is marked <c>[VERIFY]</c>. Until the capture
/// lands, the codec is provisional and
/// <see cref="DiscoveryFixturePresenceTests"/> fails on purpose so the provisional state is
/// visible in CI rather than silent.
/// <para>
/// This type deliberately knows <b>nothing</b> about the payload layout — only where the
/// bytes live on disk. All layout knowledge belongs to the single
/// <c>IDiscoveryPayloadCodec</c> implementation, so that correcting it once the fixture
/// exists stays a one-file change.
/// </para>
/// _Requirements: 15.20_
/// </remarks>
public static class DiscoveryFixture
{
    /// <summary>The captured datagram body, byte-for-byte, with no Ethernet/IP/UDP headers.</summary>
    public const string PayloadFileName = "flexradio-6000-discovery.bin";

    /// <summary>Observed capture facts and the expected parse results, as JSON.</summary>
    public const string MetadataFileName = "flexradio-6000-discovery.metadata.json";

    /// <summary>The checked-in template to copy to <see cref="MetadataFileName"/>.</summary>
    public const string MetadataTemplateFileName = "flexradio-6000-discovery.metadata.template.json";

    /// <summary>Capture instructions, kept beside the fixture so it travels with it.</summary>
    public const string ReadmeFileName = "README.md";

    private const string ProjectFileName = "RWK.Shared.Tests.csproj";

    private const string FixturesFolderName = "Fixtures";

    /// <summary>
    /// Comments and trailing commas are tolerated so the metadata template can document each
    /// field inline and the filled-in copy can keep those notes.
    /// </summary>
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The fixture directory in the source tree, resolved from the test assembly location.</summary>
    public static string DirectoryPath { get; } = ResolveDirectory();

    /// <summary>Absolute path the captured datagram body must occupy.</summary>
    public static string PayloadPath => Path.Combine(DirectoryPath, PayloadFileName);

    /// <summary>Absolute path the filled-in metadata file must occupy.</summary>
    public static string MetadataPath => Path.Combine(DirectoryPath, MetadataFileName);

    /// <summary>Absolute path of the metadata template.</summary>
    public static string MetadataTemplatePath => Path.Combine(DirectoryPath, MetadataTemplateFileName);

    /// <summary>Absolute path of the capture instructions.</summary>
    public static string ReadmePath => Path.Combine(DirectoryPath, ReadmeFileName);

    /// <summary>Whether the captured datagram body is present.</summary>
    public static bool PayloadExists => File.Exists(PayloadPath);

    /// <summary>Whether the metadata file is present. Says nothing about it being filled in.</summary>
    public static bool MetadataExists => File.Exists(MetadataPath);

    /// <summary>
    /// Reads the captured datagram body, failing the calling test with actionable guidance
    /// when the capture has not landed yet.
    /// </summary>
    /// <remarks>
    /// Codec tests (task 27.3) call this rather than reading the file themselves, so that
    /// every test gated on the capture reports the same explanation.
    /// </remarks>
    public static byte[] RequirePayload()
    {
        Assert.True(PayloadExists, MissingPayloadMessage());
        return File.ReadAllBytes(PayloadPath);
    }

    /// <summary>
    /// Reads the capture metadata, failing the calling test when it is absent, unparseable,
    /// or still carrying unfilled placeholders.
    /// </summary>
    public static DiscoveryFixtureMetadata RequireMetadata()
    {
        Assert.True(TryLoadMetadata(out DiscoveryFixtureMetadata? metadata, out string? failureReason), failureReason);
        return metadata!;
    }

    /// <summary>
    /// Loads and checks the capture metadata without throwing.
    /// </summary>
    /// <param name="metadata">The metadata when it is present, parseable, and fully filled in.</param>
    /// <param name="failureReason">
    /// Actionable guidance naming what is missing; <c>null</c> on success.
    /// </param>
    public static bool TryLoadMetadata(out DiscoveryFixtureMetadata? metadata, out string? failureReason)
    {
        metadata = null;

        if (!MetadataExists)
        {
            failureReason = MissingMetadataMessage();
            return false;
        }

        DiscoveryFixtureMetadata? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<DiscoveryFixtureMetadata>(
                File.ReadAllText(MetadataPath),
                MetadataJsonOptions);
        }
        catch (JsonException ex)
        {
            failureReason =
                $"""
                Discovery fixture metadata is not valid JSON.

                  File  : {MetadataPath}
                  Error : {ex.Message}

                Comments and trailing commas are allowed, so the inline notes copied from
                {MetadataTemplateFileName} are fine to keep. Compare against that template.
                """;
            return false;
        }

        if (parsed is null)
        {
            failureReason = $"Discovery fixture metadata deserialized to null: {MetadataPath}";
            return false;
        }

        IReadOnlyList<string> unfilled = parsed.UnfilledFields();
        if (unfilled.Count > 0)
        {
            failureReason =
                $"""
                Discovery fixture metadata still has unfilled placeholders — requirement 15.20
                is not satisfied.

                  File       : {MetadataPath}
                  Unfilled   : {string.Join(", ", unfilled)}
                  Instructions: {ReadmePath}

                Every value in the template starts as null, meaning "not yet observed". Fill in
                each field listed above from the capture and from the radio itself. In
                particular, expectedParse must be read off the radio and SmartSDR independently
                of the codec's output — filling it in from what the codec happens to produce
                would make the task 27.3 assertions tautological.
                """;
            return false;
        }

        metadata = parsed;
        failureReason = null;
        return true;
    }

    /// <summary>
    /// The "fixture missing" explanation for the captured datagram body: what is expected,
    /// where the instructions are, why the test fails on purpose, and why fabricating a
    /// payload is worse than leaving it absent.
    /// </summary>
    public static string MissingPayloadMessage() =>
        $"""
        FLEXRADIO DISCOVERY FIXTURE MISSING — requirement 15.20 is not satisfied.

          Expected file : {PayloadPath}
          Instructions  : {ReadmePath}

        This test fails on purpose while the capture is absent. It is the CI signal that the
        discovery payload codec (task 27.2, IDiscoveryPayloadCodec) is PROVISIONAL: every byte
        offset, field name, encoding, and port number in it is a guess marked [VERIFY], because
        the payload field layout is not established fact anywhere in design.md.

        To satisfy it you need a physical FlexRadio 6000-series radio:

          1. On the STATION host, on the Station LAN, capture the radio's periodic UDP
             broadcast. Wireshark capture filter: "udp and (ip broadcast or ip multicast)".
             Do NOT filter on a guessed port — the broadcast port is one of the things this
             capture establishes.
          2. Select the UDP payload ("Data") node in the packet detail tree and use
             "Export Packet Bytes..." to save it as the expected file above. The datagram BODY
             only, byte-for-byte: no Ethernet, IP, or UDP headers. A header left on shifts every
             derived offset by 42 bytes.
          3. Copy {MetadataTemplateFileName} to {MetadataFileName} in the same
             directory and fill in every null: observed broadcast port, datagram length, source
             address, radio model, firmware version, capture date, and the expected parse
             results.

        Do NOT synthesize a fixture, and do NOT skip or delete this test to get a green build.
        A fabricated payload would let the codec's tests pass against invented offsets while the
        discovery feature fails against real hardware with the whole suite green. An absent
        fixture with a loud failing test is the better state.

        Full instructions, including how to verify you exported the body and not the headers,
        are in the README named above.
        """;

    /// <summary>The "metadata missing" explanation, naming the template to copy.</summary>
    public static string MissingMetadataMessage() =>
        $"""
        FLEXRADIO DISCOVERY FIXTURE METADATA MISSING — requirement 15.20 is not satisfied.

          Expected file : {MetadataPath}
          Copy from     : {MetadataTemplatePath}
          Instructions  : {ReadmePath}

        The captured datagram body is not self-describing: the observed broadcast port, the
        datagram length, the radio's source address, the radio model, and the firmware version
        the capture came from all live here, and the codec tests (task 27.3) assert against the
        expectedParse block.

        Copy the template beside it and replace every null. This test fails on purpose until
        then, so the provisional state of the codec stays visible in CI.
        """;

    /// <summary>
    /// Walks up from the test assembly to the test project directory so the fixture is read
    /// from the source tree, letting a contributor drop the capture in without a rebuild or a
    /// csproj change.
    /// </summary>
    private static string ResolveDirectory()
    {
        DirectoryInfo? candidate = new(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, ProjectFileName)))
            {
                return Path.Combine(candidate.FullName, FixturesFolderName);
            }

            candidate = candidate.Parent;
        }

        // Fall back to beside the assembly rather than throwing, so a missing fixture always
        // surfaces as the actionable test failure above instead of as a locator crash.
        return Path.Combine(AppContext.BaseDirectory, FixturesFolderName);
    }
}

/// <summary>
/// The facts recorded alongside the captured datagram: what was observed on the wire, where
/// the capture came from, and what the codec must parse out of it.
/// </summary>
/// <remarks>
/// Every field is nullable and starts as <c>null</c> in the template, so "not yet observed"
/// is representable and detectable — see <see cref="UnfilledFields"/>. This record carries no
/// payload layout knowledge: <see cref="ObservedBroadcastPort"/> is a transport-level fact
/// about the datagram, and <see cref="ExpectedParse"/> is stated in terms of
/// <c>DiscoveredRadio</c>, not byte offsets.
/// </remarks>
public sealed record DiscoveryFixtureMetadata
{
    /// <summary>Format version of this metadata file.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Name of the binary fixture this metadata describes.</summary>
    public string? FixtureFile { get; init; }

    /// <summary>
    /// UDP destination port of the captured broadcast: the port the Station listener binds
    /// and SmartSDR listens on. Distinct from <see cref="DiscoveryFixtureExpectedParse.StationCommandPort"/>.
    /// </summary>
    public int? ObservedBroadcastPort { get; init; }

    /// <summary>Length of the datagram body, which must equal the size of the binary fixture.</summary>
    public int? DatagramLengthBytes { get; init; }

    /// <summary>The radio's address on the Station LAN as seen in the IP source field.</summary>
    public string? SourceAddress { get; init; }

    /// <summary>The radio's UDP source port.</summary>
    public int? SourcePort { get; init; }

    /// <summary>The broadcast destination address observed.</summary>
    public string? DestinationAddress { get; init; }

    /// <summary>Radio model as printed on the radio and shown by SmartSDR.</summary>
    public string? RadioModel { get; init; }

    /// <summary>Radio serial number.</summary>
    public string? RadioSerial { get; init; }

    /// <summary>
    /// SmartSDR / radio firmware version the capture came from. A later revision may add
    /// payload fields, which the codec must preserve without interpreting.
    /// </summary>
    public string? FirmwareVersion { get; init; }

    /// <summary>Capture date and time, ISO-8601 UTC.</summary>
    public string? CapturedUtc { get; init; }

    /// <summary>Capture tool and version.</summary>
    public string? CaptureTool { get; init; }

    /// <summary>Who took the capture.</summary>
    public string? CapturedBy { get; init; }

    /// <summary>What the codec must return for this payload.</summary>
    public DiscoveryFixtureExpectedParse? ExpectedParse { get; init; }

    /// <summary>Free-form notes for a future reader. Never treated as unfilled.</summary>
    public string? Notes { get; init; }

    /// <summary>
    /// Names the fields still holding the template's <c>null</c> placeholder, so the gating
    /// test can say exactly what is outstanding rather than just "incomplete".
    /// </summary>
    public IReadOnlyList<string> UnfilledFields()
    {
        List<string> unfilled = [];

        if (ObservedBroadcastPort is null) unfilled.Add(nameof(ObservedBroadcastPort));
        if (DatagramLengthBytes is null) unfilled.Add(nameof(DatagramLengthBytes));
        if (string.IsNullOrWhiteSpace(SourceAddress)) unfilled.Add(nameof(SourceAddress));
        if (SourcePort is null) unfilled.Add(nameof(SourcePort));
        if (string.IsNullOrWhiteSpace(DestinationAddress)) unfilled.Add(nameof(DestinationAddress));
        if (string.IsNullOrWhiteSpace(RadioModel)) unfilled.Add(nameof(RadioModel));
        if (string.IsNullOrWhiteSpace(RadioSerial)) unfilled.Add(nameof(RadioSerial));
        if (string.IsNullOrWhiteSpace(FirmwareVersion)) unfilled.Add(nameof(FirmwareVersion));
        if (string.IsNullOrWhiteSpace(CapturedUtc)) unfilled.Add(nameof(CapturedUtc));
        if (string.IsNullOrWhiteSpace(CaptureTool)) unfilled.Add(nameof(CaptureTool));
        if (string.IsNullOrWhiteSpace(CapturedBy)) unfilled.Add(nameof(CapturedBy));

        if (ExpectedParse is null)
        {
            unfilled.Add(nameof(ExpectedParse));
        }
        else
        {
            foreach (string field in ExpectedParse.UnfilledFields())
            {
                unfilled.Add($"{nameof(ExpectedParse)}.{field}");
            }
        }

        return unfilled;
    }
}

/// <summary>
/// The values <c>IDiscoveryPayloadCodec.TryParse</c> must return for the captured payload:
/// the assertions task 27.3 makes.
/// </summary>
/// <remarks>
/// Read off the radio and SmartSDR independently of the codec. Deriving these from the
/// codec's own output would make the tests tautological and defeat the point of the capture.
/// </remarks>
public sealed record DiscoveryFixtureExpectedParse
{
    /// <summary><c>DiscoveredRadio.Serial</c>: the stable identity key (15.16).</summary>
    public string? Serial { get; init; }

    /// <summary><c>DiscoveredRadio.Model</c>, as advertised inside the payload.</summary>
    public string? Model { get; init; }

    /// <summary><c>DiscoveredRadio.StationAddress</c>: the address advertised inside the payload.</summary>
    public string? StationAddress { get; init; }

    /// <summary>
    /// <c>DiscoveredRadio.StationCommandPort</c>: the command port advertised inside the
    /// payload, and the field the emitter's rewrite replaces (15.4).
    /// </summary>
    public int? StationCommandPort { get; init; }

    /// <summary>Names the fields still holding the template's <c>null</c> placeholder.</summary>
    public IReadOnlyList<string> UnfilledFields()
    {
        List<string> unfilled = [];

        if (string.IsNullOrWhiteSpace(Serial)) unfilled.Add(nameof(Serial));
        if (string.IsNullOrWhiteSpace(Model)) unfilled.Add(nameof(Model));
        if (string.IsNullOrWhiteSpace(StationAddress)) unfilled.Add(nameof(StationAddress));
        if (StationCommandPort is null) unfilled.Add(nameof(StationCommandPort));

        return unfilled;
    }
}
