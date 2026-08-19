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
using System.Security.Cryptography;
using RWK.Shared.Config;
using Xunit;

namespace RWK.Shared.Tests.Config;

/// <summary>
/// Unit tests for <see cref="ConfigStore{T}"/>: JSON round-trip, DPAPI secret protection, and
/// recovery from a missing or corrupt profile.
/// </summary>
/// <remarks>
/// The tests exercise real Windows DPAPI rather than a stand-in, because the property that
/// matters — the plaintext secret does not appear in the file — is only meaningful against
/// real encryption. Tests that require DPAPI return early on a non-Windows host.
/// <para>
/// _Requirements: 12.1, 12.2, 12.3, 12.6_
/// </para>
/// </remarks>
public sealed class ConfigStoreTests : IDisposable
{
    private const string AuthKeySecret = "tskey-auth-PLAINTEXT-AUTH-KEY-0123456789";
    private const string PairingSecret = "PLAINTEXT-PAIRING-SECRET-abcdefghij";

    private readonly string _directory;
    private readonly string _filePath;
    private readonly List<string> _diagnostics = new();

    public ConfigStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "rwk-config-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "config.json");
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

    private ConfigStore<ClientConfig> CreateClientStore()
        => new(_filePath, SecretProtector.Default, _diagnostics.Add);

    private ConfigStore<StationConfig> CreateStationStore()
        => new(_filePath, SecretProtector.Default, _diagnostics.Add);

    [Fact]
    public void Load_MissingFile_ReturnsDefaultsAndDoesNotThrow()
    {
        ConfigStore<ClientConfig> store = CreateClientStore();

        ClientConfig loaded = store.Load();

        Assert.Equal(new ClientConfig(), loaded);
        Assert.False(File.Exists(_filePath));
        Assert.Contains(_diagnostics, message => message.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Save_AfterMissingFile_CreatesValidProfileThatReloads()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ConfigStore<ClientConfig> store = CreateClientStore();
        ClientConfig config = store.Load() with { SpeedWpm = 32 };

        store.Save(config);

        Assert.True(File.Exists(_filePath));
        Assert.Equal(32, store.Load().SpeedWpm);
    }

    [Fact]
    public void SaveThenLoad_ClientConfig_RoundTripsEveryField()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var rule = new ForwardRule(
            Guid.NewGuid(),
            "CAT",
            ForwardProtocol.Tcp,
            ClientPort: 4532,
            StationPort: 4532,
            Enabled: true,
            BindAddress: "0.0.0.0",
            RuleType: ForwardRuleType.Cat);

        var config = new ClientConfig
        {
            PaddlePortName = "COM3",
            WinKeyerPortName = "COM4",
            SpeedWpm = 38,
            Weight = 55,
            PaddleReverse = true,
            KeyerMode = KeyerMode.Ultimatic,
            DebounceTime = TimeSpan.FromMilliseconds(7),
            Sidetone = new SidetoneConfig { DeviceId = "dev-1", FrequencyHz = 850, Volume = 0.25 },
            Tailscale = new TailscaleConfig
            {
                AuthKey = AuthKeySecret,
                PairingSecret = PairingSecret,
                StationAddress = "100.64.1.2"
            },
            ForwardRules = ImmutableList.Create(rule)
        };

        ConfigStore<ClientConfig> store = CreateClientStore();
        store.Save(config);
        ClientConfig loaded = store.Load();

        // ImmutableList does not implement value equality, so the collection is compared
        // element-wise and excluded from the record comparison.
        Assert.Equal(config.ForwardRules, loaded.ForwardRules);
        Assert.Equal(
            config with { ForwardRules = ImmutableList<ForwardRule>.Empty },
            loaded with { ForwardRules = ImmutableList<ForwardRule>.Empty });
        Assert.Empty(_diagnostics);
    }

    [Fact]
    public void SaveThenLoad_StationConfig_RoundTripsSecretsAndSettings()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var config = new StationConfig
        {
            KeyingPortName = "COM9",
            KeyLine = KeyingLine.DTR,
            PttLine = KeyingLine.RTS,
            KeyInvert = true,
            PttInvert = true,
            PttTiming = new PttTimingConfig
            {
                LeadTime = TimeSpan.FromMilliseconds(20),
                TailTime = TimeSpan.FromMilliseconds(700)
            },
            Tailscale = new TailscaleConfig
            {
                AuthKey = AuthKeySecret,
                PairingSecret = PairingSecret
            },
            ForwardOverrides = ImmutableList.Create(
                new ForwardRuleOverride(Guid.NewGuid(), Allowed: false, TargetHostOverride: "192.168.1.50"))
        };

        ConfigStore<StationConfig> store = CreateStationStore();
        store.Save(config);
        StationConfig loaded = store.Load();

        Assert.Equal(config.ForwardOverrides, loaded.ForwardOverrides);
        Assert.Equal(
            config with { ForwardOverrides = ImmutableList<ForwardRuleOverride>.Empty },
            loaded with { ForwardOverrides = ImmutableList<ForwardRuleOverride>.Empty });
    }

    [Fact]
    public void Save_DoesNotWriteSecretsInPlaintext()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var config = new ClientConfig
        {
            Tailscale = new TailscaleConfig
            {
                AuthKey = AuthKeySecret,
                PairingSecret = PairingSecret,
                StationAddress = "100.64.1.2"
            }
        };

        ConfigStore<ClientConfig> store = CreateClientStore();
        store.Save(config);

        string fileText = File.ReadAllText(_filePath);

        Assert.DoesNotContain(AuthKeySecret, fileText, StringComparison.Ordinal);
        Assert.DoesNotContain(PairingSecret, fileText, StringComparison.Ordinal);

        // The non-secret peer address is expected to stay readable, which also proves the
        // absence of the secrets above is not simply an empty or unwritten file.
        Assert.Contains("100.64.1.2", fileText, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefaultsAndNextSaveWritesValidFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(_filePath, "{ this is not valid json");

        ConfigStore<ClientConfig> store = CreateClientStore();
        ClientConfig defaults = store.Load();

        Assert.Equal(new ClientConfig(), defaults);
        Assert.Contains(_diagnostics, message => message.Contains("could not be loaded", StringComparison.Ordinal));

        store.Save(defaults with { SpeedWpm = 21 });

        Assert.Equal(21, store.Load().SpeedWpm);
    }

    [Fact]
    public void Load_EmptyFile_ReturnsDefaults()
    {
        File.WriteAllText(_filePath, string.Empty);

        ClientConfig loaded = CreateClientStore().Load();

        Assert.Equal(new ClientConfig(), loaded);
        Assert.Contains(_diagnostics, message => message.Contains("is empty", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_SecretThatCannotBeDecrypted_ClearsThatFieldAndKeepsTheRest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var config = new ClientConfig
        {
            SpeedWpm = 29,
            Tailscale = new TailscaleConfig
            {
                AuthKey = AuthKeySecret,
                PairingSecret = PairingSecret,
                StationAddress = "100.64.1.2"
            }
        };

        ConfigStore<ClientConfig> store = CreateClientStore();
        store.Save(config);

        // Stand in for a profile copied to another machine or another user account: a
        // well-formed base64 string that is not a blob this user can decrypt.
        string foreignBlob = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        string tampered = ReplaceJsonStringValue(File.ReadAllText(_filePath), "AuthKey", foreignBlob);
        File.WriteAllText(_filePath, tampered);

        ClientConfig loaded = store.Load();

        Assert.Null(loaded.Tailscale.AuthKey);
        Assert.Equal(PairingSecret, loaded.Tailscale.PairingSecret);
        Assert.Equal("100.64.1.2", loaded.Tailscale.StationAddress);
        Assert.Equal(29, loaded.SpeedWpm);
        Assert.Contains(_diagnostics, message => message.Contains("could not be decrypted", StringComparison.Ordinal));
    }

    [Fact]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ConfigStore<ClientConfig> store = CreateClientStore();
        store.Save(new ClientConfig());
        store.Save(new ClientConfig { SpeedWpm = 30 });

        Assert.Equal(new[] { "config.json" }, Directory.GetFiles(_directory).Select(Path.GetFileName).ToArray());
        Assert.Equal(30, store.Load().SpeedWpm);
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string nestedPath = Path.Combine(_directory, "RWK Client", "config.json");
        var store = new ConfigStore<ClientConfig>(nestedPath, SecretProtector.Default, _diagnostics.Add);

        store.Save(new ClientConfig { SpeedWpm = 18 });

        Assert.True(File.Exists(nestedPath));
        Assert.Equal(18, store.Load().SpeedWpm);
    }

    [Fact]
    public void ConfigFilePaths_UseSeparateApplicationDataFolders()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.Equal(
            Path.Combine(appData, "RWK Client", "config.json"),
            ConfigStore.GetConfigFilePath(ConfigStore.ClientFolderName));
        Assert.Equal(
            Path.Combine(appData, "RWK Station", "config.json"),
            ConfigStore.GetConfigFilePath(ConfigStore.StationFolderName));
        Assert.NotEqual(ConfigStore.ForClient().FilePath, ConfigStore.ForStation().FilePath);
    }

    /// <summary>
    /// Rewrites the value of a top-level-nested JSON string property by locating the property
    /// name and replacing the quoted value that follows it. Sufficient for these fixtures.
    /// </summary>
    private static string ReplaceJsonStringValue(string json, string propertyName, string newValue)
    {
        int nameIndex = json.IndexOf($"\"{propertyName}\"", StringComparison.Ordinal);
        Assert.True(nameIndex >= 0, $"Property '{propertyName}' was not found in the saved profile.");

        int valueStart = json.IndexOf('"', json.IndexOf(':', nameIndex) + 1);
        int valueEnd = json.IndexOf('"', valueStart + 1);
        Assert.True(valueStart > 0 && valueEnd > valueStart, "Saved profile did not contain a quoted value.");

        return string.Concat(
            json.AsSpan(0, valueStart + 1),
            newValue,
            json.AsSpan(valueEnd));
    }
}
