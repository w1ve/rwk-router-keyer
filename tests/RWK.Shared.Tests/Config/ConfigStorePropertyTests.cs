/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using RWK.Shared.Config;
using Xunit;

namespace RWK.Shared.Tests.Config;

/// <summary>
/// Property-based tests for <see cref="ConfigStore{T}"/>: round-trip fidelity, DPAPI secret
/// protection, and recovery from a profile that cannot be read (Properties 40, 41, 42).
/// </summary>
/// <remarks>
/// These complement the example-based tests in <c>ConfigStoreTests</c> rather than repeating
/// them: the examples pin down specific behaviors (missing file, tampered secret, temp-file
/// cleanup), while these sweep the field space of both profiles.
/// <para>
/// Real Windows DPAPI is used, not a stand-in, because the property that matters — the
/// plaintext secret does not appear in the file — is only meaningful against real encryption.
/// Properties that need DPAPI degenerate to a trivially true property on a non-Windows host so
/// the suite stays portable.
/// </para>
/// <para>
/// _Requirements: 12.1, 12.2, 12.3, 12.6_
/// </para>
/// </remarks>
public sealed class ConfigStorePropertyTests : IDisposable
{
    private readonly string _directory;

    public ConfigStorePropertyTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "rwk-config-property-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// A fresh profile path for one generated case, so that cases cannot interfere through
    /// leftover state on disk.
    /// </summary>
    private string NewFilePath() => Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");

    // ------------------------------------------------------------------
    // Property 40: Configuration Round-Trip
    // ------------------------------------------------------------------

    /// <summary>
    /// Property 40: for any Client profile, saving then loading yields an equivalent profile
    /// with every field preserved.
    /// </summary>
    /// <remarks>**Validates: Requirements 12.1**</remarks>
    [Property]
    public Property Property40_ClientConfig_SurvivesSaveThenLoad()
    {
        if (!OperatingSystem.IsWindows())
        {
            return TriviallyTrue();
        }

        return Prop.ForAll(ClientConfigGen.ToArbitrary(), config =>
        {
            var store = new ConfigStore<ClientConfig>(NewFilePath(), SecretProtector.Default);

            store.Save(config);
            ClientConfig loaded = store.Load();

            AssertClientConfigsEquivalent(config, loaded);
            return true;
        });
    }

    /// <summary>
    /// Property 40: for any Station profile, saving then loading yields an equivalent profile
    /// with every field preserved.
    /// </summary>
    /// <remarks>**Validates: Requirements 12.1**</remarks>
    [Property]
    public Property Property40_StationConfig_SurvivesSaveThenLoad()
    {
        if (!OperatingSystem.IsWindows())
        {
            return TriviallyTrue();
        }

        return Prop.ForAll(StationConfigGen.ToArbitrary(), config =>
        {
            var store = new ConfigStore<StationConfig>(NewFilePath(), SecretProtector.Default);

            store.Save(config);
            StationConfig loaded = store.Load();

            AssertStationConfigsEquivalent(config, loaded);
            return true;
        });
    }

    // ------------------------------------------------------------------
    // Property 41: DPAPI Secret Protection
    // ------------------------------------------------------------------

    /// <summary>
    /// Property 41: for any pair of secrets, the saved file holds ciphertext rather than the
    /// plaintext values, and loading restores them exactly.
    /// </summary>
    /// <remarks>
    /// The assertion that the non-secret station address <em>is</em> present is the control:
    /// without it, an empty or unwritten file would satisfy the absence checks vacuously.
    /// <para>**Validates: Requirements 12.2, 12.3**</para>
    /// </remarks>
    [Property]
    public Property Property41_SecretsAreEncryptedOnDiskAndRestoredOnLoad()
    {
        if (!OperatingSystem.IsWindows())
        {
            return TriviallyTrue();
        }

        Gen<(string AuthKey, string PairingSecret, string StationAddress)> gen =
            from authKey in SecretGen
            from pairingSecret in SecretGen
            from stationAddress in StationAddressGen
            select (authKey, pairingSecret, stationAddress);

        return Prop.ForAll(gen.ToArbitrary(), input =>
        {
            (string authKey, string pairingSecret, string stationAddress) = input;

            string path = NewFilePath();
            var store = new ConfigStore<ClientConfig>(path, SecretProtector.Default);
            var config = new ClientConfig
            {
                Tailscale = new TailscaleConfig
                {
                    AuthKey = authKey,
                    PairingSecret = pairingSecret,
                    StationAddress = stationAddress
                }
            };

            store.Save(config);
            string fileText = File.ReadAllText(path);

            Assert.DoesNotContain(authKey, fileText, StringComparison.Ordinal);
            Assert.DoesNotContain(pairingSecret, fileText, StringComparison.Ordinal);

            // Control: a non-secret field stays readable, so the two absence assertions above
            // cannot be satisfied by an empty or unwritten file.
            Assert.Contains(stationAddress, fileText, StringComparison.Ordinal);

            AssertStoredSecretIsCiphertext(fileText, "AuthKey", authKey);
            AssertStoredSecretIsCiphertext(fileText, "PairingSecret", pairingSecret);

            ClientConfig loaded = store.Load();

            Assert.Equal(authKey, loaded.Tailscale.AuthKey);
            Assert.Equal(pairingSecret, loaded.Tailscale.PairingSecret);
            Assert.Equal(stationAddress, loaded.Tailscale.StationAddress);
            return true;
        });
    }

    // ------------------------------------------------------------------
    // Property 42: Default Configuration Recovery
    // ------------------------------------------------------------------

    /// <summary>
    /// Property 42: for any file content that is not a valid profile, both profiles load as
    /// defaults without throwing, and the next save produces a file that loads back correctly.
    /// </summary>
    /// <remarks>**Validates: Requirements 12.6**</remarks>
    [Property]
    public Property Property42_UnreadableProfileLoadsDefaultsAndNextSaveIsValid()
    {
        Gen<(byte[] Content, int SpeedWpm, int LeadTimeMs)> gen =
            from content in InvalidProfileContentGen
            from speedWpm in Gen.Choose(5, 60)
            from leadTimeMs in Gen.Choose(0, 100)
            select (content, speedWpm, leadTimeMs);

        return Prop.ForAll(gen.ToArbitrary(), input =>
        {
            (byte[] content, int speedWpm, int leadTimeMs) = input;

            string clientPath = NewFilePath();
            string stationPath = NewFilePath();
            File.WriteAllBytes(clientPath, content);
            File.WriteAllBytes(stationPath, content);

            var clientStore = new ConfigStore<ClientConfig>(clientPath, SecretProtector.Default);
            var stationStore = new ConfigStore<StationConfig>(stationPath, SecretProtector.Default);

            // Load never throws: an unreadable profile resolves to defaults so the
            // application can still start.
            ClientConfig clientDefaults = clientStore.Load();
            StationConfig stationDefaults = stationStore.Load();

            Assert.Equal(new ClientConfig(), clientDefaults);
            Assert.Equal(new StationConfig(), stationDefaults);

            // The next save replaces the damaged file with a valid one.
            ClientConfig client = clientDefaults with { SpeedWpm = speedWpm };
            StationConfig station = stationDefaults with
            {
                PttTiming = new PttTimingConfig { LeadTime = TimeSpan.FromMilliseconds(leadTimeMs) }
            };

            clientStore.Save(client);
            stationStore.Save(station);

            AssertClientConfigsEquivalent(client, clientStore.Load());
            AssertStationConfigsEquivalent(station, stationStore.Load());
            return true;
        });
    }

    // ------------------------------------------------------------------
    // Comparison helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Compares two Client profiles. <see cref="ImmutableList{T}"/> has no value equality, so
    /// the rule list is compared element-wise and normalized out of the record comparison.
    /// </summary>
    private static void AssertClientConfigsEquivalent(ClientConfig expected, ClientConfig actual)
    {
        Assert.Equal(expected.ForwardRules, actual.ForwardRules);
        Assert.Equal(
            expected with { ForwardRules = ImmutableList<ForwardRule>.Empty },
            actual with { ForwardRules = ImmutableList<ForwardRule>.Empty });
    }

    /// <summary>Compares two Station profiles, normalizing the override list as above.</summary>
    private static void AssertStationConfigsEquivalent(StationConfig expected, StationConfig actual)
    {
        Assert.Equal(expected.ForwardOverrides, actual.ForwardOverrides);
        Assert.Equal(
            expected with { ForwardOverrides = ImmutableList<ForwardRuleOverride>.Empty },
            actual with { ForwardOverrides = ImmutableList<ForwardRuleOverride>.Empty });
    }

    /// <summary>
    /// Asserts that the named secret inside the saved <c>Tailscale</c> object is a non-empty
    /// base64 blob that is not the plaintext.
    /// </summary>
    private static void AssertStoredSecretIsCiphertext(string fileText, string propertyName, string plaintext)
    {
        using JsonDocument document = JsonDocument.Parse(fileText);

        Assert.True(
            document.RootElement.TryGetProperty("Tailscale", out JsonElement tailscale),
            "The saved profile did not contain a Tailscale object.");
        Assert.True(
            tailscale.TryGetProperty(propertyName, out JsonElement secret),
            $"The saved Tailscale object did not contain '{propertyName}'.");
        Assert.Equal(JsonValueKind.String, secret.ValueKind);

        string stored = secret.GetString()!;
        Assert.NotEqual(plaintext, stored);
        Assert.NotEmpty(stored);

        // A DPAPI blob is persisted base64-encoded; anything else means the field was not
        // encrypted the way the store claims.
        byte[] cipher = Convert.FromBase64String(stored);
        Assert.NotEmpty(cipher);
    }

    /// <summary>A property that holds by construction, used to skip DPAPI-dependent cases.</summary>
    private static Property TriviallyTrue()
        => Prop.ForAll(Gen.Choose(0, 0).ToArbitrary(), _ => true);

    // ------------------------------------------------------------------
    // Generators
    //
    // The generators are constrained rather than fully arbitrary, deliberately:
    //   * strings are drawn from characters that survive UTF-8 and JSON escaping, so a lone
    //     surrogate cannot masquerade as a round-trip defect in the store;
    //   * TimeSpans are whole milliseconds, which the JSON TimeSpan format represents exactly;
    //   * volume is a hundredth, avoiding any question about double formatting;
    //   * identifiers are derived from the generated seed rather than Guid.NewGuid, so a
    //     counterexample can be replayed.
    // Each remaining field sweeps its full documented range.
    // ------------------------------------------------------------------

    private const string SecretChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_";

    private const string LabelChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .-_";

    /// <summary>
    /// Printable characters used for garbage file content, excluding the braces that could
    /// make the content parse as a JSON object.
    /// </summary>
    private const string GarbageChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 \t\r\n:,.-_/\\'()<>=+*#!?@$%&|^~;";

    /// <summary>
    /// Shortest secret the generator produces. A one- or two-character secret could occur by
    /// chance inside the base64 ciphertext, which would falsify Property 41 for a reason that
    /// has nothing to do with the store. Real auth keys and pairing codes are long.
    /// </summary>
    private const int MinimumSecretLength = 12;

    private static readonly string[] BindAddresses =
    {
        ForwardRule.LoopbackAddress,
        ForwardRule.AnyAddress,
        "127.0.0.5",
        "192.168.1.10",
        "10.0.0.5"
    };

    private static readonly string[] BroadcastAddresses =
    {
        "255.255.255.255",
        "192.168.1.255",
        "10.0.0.255"
    };

    /// <summary>
    /// A well-formed profile document used as the source for truncation cases. Any proper
    /// prefix of it is an unterminated object, and therefore not a valid profile.
    /// </summary>
    private const string ReferenceProfileJson = """
        {
          "PaddlePortName": "COM3",
          "WinKeyerPortName": "COM4",
          "SpeedWpm": 30,
          "Weight": 50,
          "Sidetone": { "DeviceId": null, "FrequencyHz": 800, "Volume": 0.4 },
          "Tailscale": { "AuthKey": null, "PairingSecret": null, "StationAddress": "100.64.1.2" },
          "ForwardRules": []
        }
        """;

    /// <summary>Valid JSON documents whose root is not a profile object.</summary>
    private static readonly string[] WrongShapeDocuments =
    {
        "null",
        "true",
        "123",
        "-4.5",
        "\"a profile\"",
        "[]",
        "[1, 2, 3]",
        "[{ \"SpeedWpm\": 30 }]"
    };

    /// <summary>Documents carrying no content at all.</summary>
    private static readonly string[] BlankDocuments =
    {
        "",
        " ",
        "   \t  ",
        "\r\n\r\n"
    };

    private static Gen<bool> BoolGen => Gen.Choose(0, 1).Select(value => value == 1);

    private static Gen<TimeSpan> MillisecondsGen(int minimum, int maximum)
        => Gen.Choose(minimum, maximum).Select(ms => TimeSpan.FromMilliseconds(ms));

    /// <summary>Guids derived from the generated seed so a counterexample is reproducible.</summary>
    private static Gen<Guid> GuidGen
        => Gen.Choose(1, int.MaxValue).Select(seed => new Guid(seed, 0, 0, new byte[8]));

    private static Gen<string> TextGen(string alphabet, int minimumLength)
        => Gen.NonEmptyListOf(Gen.Elements(alphabet.ToCharArray()))
            .Select(chars => Repeat(new string(chars.ToArray()), minimumLength));

    private static Gen<string> SecretGen => TextGen(SecretChars, MinimumSecretLength);

    private static Gen<string> LabelGen => TextGen(LabelChars, minimumLength: 1);

    private static Gen<string?> OptionalTextGen(Gen<string> textGen)
        => from text in textGen
           from present in Gen.Choose(0, 3)
           select present == 0 ? null : text;

    private static Gen<string?> OptionalSecretGen => OptionalTextGen(SecretGen);

    private static Gen<string?> OptionalPortNameGen
        => OptionalTextGen(Gen.Choose(1, 64).Select(number => "COM" + number));

    /// <summary>
    /// Tailnet peer addresses. Dotted quads keep every digit run to three characters, so a
    /// generated secret of at least <see cref="MinimumSecretLength"/> characters can never be a
    /// substring of the address — the control assertion in Property 41 and the plaintext
    /// absence assertions cannot collide.
    /// </summary>
    private static Gen<string> StationAddressGen
        => from third in Gen.Choose(0, 255)
           from fourth in Gen.Choose(0, 255)
           select $"100.64.{third}.{fourth}";

    private static Gen<string?> OptionalBindAddressGen
        => OptionalTextGen(Gen.Elements(BindAddresses));

    private static Gen<SidetoneConfig> SidetoneGen
        => from deviceId in OptionalTextGen(LabelGen)
           from frequencyHz in Gen.Choose(300, 1500)
           from volumePercent in Gen.Choose(0, 100)
           select new SidetoneConfig
           {
               DeviceId = deviceId,
               FrequencyHz = frequencyHz,
               Volume = volumePercent / 100.0
           };

    private static Gen<TailscaleConfig> TailscaleGen
        => from authKey in OptionalSecretGen
           from pairingSecret in OptionalSecretGen
           from stationAddress in OptionalTextGen(StationAddressGen)
           select new TailscaleConfig
           {
               AuthKey = authKey,
               PairingSecret = pairingSecret,
               StationAddress = stationAddress
           };

    private static Gen<ForwardRule> ForwardRuleGen
        => from id in GuidGen
           from name in LabelGen
           from protocol in Gen.Elements(Enum.GetValues<ForwardProtocol>())
           from clientPort in Gen.Choose(1, 65535)
           from stationPort in Gen.Choose(1, 65535)
           from enabled in BoolGen
           from bindAddress in Gen.Elements(BindAddresses)
           from ruleType in Gen.Elements(Enum.GetValues<ForwardRuleType>())
           select new ForwardRule(id, name, protocol, clientPort, stationPort, enabled, bindAddress, ruleType);

    private static Gen<ImmutableList<ForwardRule>> ForwardRulesGen
        => from first in ForwardRuleGen
           from second in ForwardRuleGen
           from third in ForwardRuleGen
           from count in Gen.Choose(0, 3)
           select ImmutableList.CreateRange(new[] { first, second, third }.Take(count));

    private static Gen<ImmutableList<ForwardRuleOverride>> ForwardOverridesGen
        => from firstId in GuidGen
           from secondId in GuidGen
           from allowed in BoolGen
           from targetHost in OptionalBindAddressGen
           from count in Gen.Choose(0, 2)
           select ImmutableList.CreateRange(
               new[]
               {
                   new ForwardRuleOverride(firstId, allowed, targetHost),
                   new ForwardRuleOverride(secondId, !allowed, null)
               }.Take(count));

    private static Gen<JitterBufferConfig> JitterBufferGen
        => from directDelay in MillisecondsGen(30, 150)
           from derpDelay in MillisecondsGen(100, 500)
           from adaptiveMode in BoolGen
           select new JitterBufferConfig(directDelay, derpDelay, adaptiveMode);

    private static Gen<PttTimingConfig> PttTimingGen
        => from leadTime in MillisecondsGen(0, 100)
           from tailTime in MillisecondsGen(0, 2000)
           select new PttTimingConfig { LeadTime = leadTime, TailTime = tailTime };

    private static Gen<ClientConfig> ClientConfigGen
        => from paddlePortName in OptionalPortNameGen
           from winKeyerPortName in OptionalPortNameGen
           from speedWpm in Gen.Choose(5, 60)
           from weight in Gen.Choose(25, 75)
           from paddleReverse in BoolGen
           from keyerMode in Gen.Elements(Enum.GetValues<KeyerMode>())
           from debounceTime in MillisecondsGen(0, 50)
           from sidetone in SidetoneGen
           from tailscale in TailscaleGen
           from forwardRules in ForwardRulesGen
           from discoveryEmitEnabled in BoolGen
           from discoveryExpiry in MillisecondsGen(1_000, 60_000)
           from discoveryBroadcastPort in Gen.Choose(1, 65535)
           from discoveryBroadcastAddress in Gen.Elements(BroadcastAddresses)
           select new ClientConfig
           {
               PaddlePortName = paddlePortName,
               WinKeyerPortName = winKeyerPortName,
               SpeedWpm = speedWpm,
               Weight = weight,
               PaddleReverse = paddleReverse,
               KeyerMode = keyerMode,
               DebounceTime = debounceTime,
               Sidetone = sidetone,
               Tailscale = tailscale,
               ForwardRules = forwardRules,
               DiscoveryEmitEnabled = discoveryEmitEnabled,
               DiscoveryExpiryInterval = discoveryExpiry,
               DiscoveryBroadcastPort = discoveryBroadcastPort,
               DiscoveryBroadcastAddress = discoveryBroadcastAddress
           };

    private static Gen<StationConfig> StationConfigGen
        => from keyingPortName in OptionalPortNameGen
           from keyLine in Gen.Elements(new[] { KeyingLine.DTR, KeyingLine.RTS })
           from pttLine in Gen.Elements(Enum.GetValues<KeyingLine>())
           from keyInvert in BoolGen
           from pttInvert in BoolGen
           from jitterBuffer in JitterBufferGen
           from pttTiming in PttTimingGen
           from tailscale in TailscaleGen
           from forwardOverrides in ForwardOverridesGen
           from discoveryCaptureEnabled in BoolGen
           from discoveryListenPort in Gen.Choose(1, 65535)
           from discoveryBindAddress in OptionalBindAddressGen
           from discoveryExpiry in MillisecondsGen(1_000, 60_000)
           select new StationConfig
           {
               KeyingPortName = keyingPortName,
               KeyLine = keyLine,
               PttLine = pttLine,
               KeyInvert = keyInvert,
               PttInvert = pttInvert,
               JitterBuffer = jitterBuffer,
               PttTiming = pttTiming,
               Tailscale = tailscale,
               ForwardOverrides = forwardOverrides,
               DiscoveryCaptureEnabled = discoveryCaptureEnabled,
               DiscoveryListenPort = discoveryListenPort,
               DiscoveryBindAddress = discoveryBindAddress,
               DiscoveryExpiryInterval = discoveryExpiry
           };

    /// <summary>
    /// File content that is not a valid profile: random bytes, a truncated profile document,
    /// valid JSON of the wrong shape, an empty or whitespace-only file, and printable garbage.
    /// No branch can produce a JSON object at the root, so every generated case must resolve
    /// to defaults.
    /// </summary>
    private static Gen<byte[]> InvalidProfileContentGen
        => from kind in Gen.Choose(0, 4)
           from rawBytes in Gen.NonEmptyListOf(Gen.Choose(0, 255))
           from cut in Gen.Choose(1, ReferenceProfileJson.Length - 1)
           from wrongShape in Gen.Elements(WrongShapeDocuments)
           from blank in Gen.Elements(BlankDocuments)
           from garbage in Gen.NonEmptyListOf(Gen.Elements(GarbageChars.ToCharArray()))
           select kind switch
           {
               0 => rawBytes.Select(value => (byte)value).ToArray(),
               1 => Encoding.UTF8.GetBytes(ReferenceProfileJson[..cut]),
               2 => Encoding.UTF8.GetBytes(wrongShape),
               3 => Encoding.UTF8.GetBytes(blank),
               _ => Encoding.UTF8.GetBytes(new string(garbage.ToArray()))
           };

    /// <summary>
    /// Repeats <paramref name="value"/> until it reaches <paramref name="minimumLength"/>,
    /// keeping the result derived entirely from generated content.
    /// </summary>
    private static string Repeat(string value, int minimumLength)
    {
        if (value.Length >= minimumLength)
        {
            return value;
        }

        var builder = new StringBuilder(value);
        while (builder.Length < minimumLength)
        {
            builder.Append(value);
        }

        return builder.ToString();
    }
}
