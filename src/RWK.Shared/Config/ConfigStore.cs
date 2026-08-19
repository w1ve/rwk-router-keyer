/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
using System.Text;
using System.Text.Json;

namespace RWK.Shared.Config;

/// <summary>
/// Locations and factory methods for the Client and Station profile stores (12.1).
/// </summary>
/// <remarks>
/// The Client and the Station keep separate profiles in separate folders under
/// <c>%AppData%</c>, following the v1 convention of a per-application folder holding a single
/// JSON settings file.
/// <para>
/// _Requirements: 12.1_
/// </para>
/// </remarks>
public static class ConfigStore
{
    /// <summary>Folder name under <c>%AppData%</c> holding the Client profile.</summary>
    public const string ClientFolderName = "RWK Client";

    /// <summary>Folder name under <c>%AppData%</c> holding the Station profile.</summary>
    public const string StationFolderName = "RWK Station";

    /// <summary>File name of the profile inside its application folder.</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>
    /// Builds the full path of a profile file under <c>%AppData%</c>.
    /// </summary>
    /// <param name="applicationFolderName">
    /// Folder name, normally <see cref="ClientFolderName"/> or <see cref="StationFolderName"/>.
    /// </param>
    /// <returns>The absolute path of the profile file.</returns>
    public static string GetConfigFilePath(string applicationFolderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationFolderName);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            applicationFolderName,
            ConfigFileName);
    }

    /// <summary>Creates the store for the Client profile at its standard location.</summary>
    /// <param name="protector">
    /// Secret protector; defaults to <see cref="SecretProtector.Default"/>.
    /// </param>
    /// <param name="diagnostics">Optional sink for load and save diagnostics.</param>
    /// <returns>A store bound to <c>%AppData%\RWK Client\config.json</c>.</returns>
    public static ConfigStore<ClientConfig> ForClient(
        ISecretProtector? protector = null,
        Action<string>? diagnostics = null)
        => new(GetConfigFilePath(ClientFolderName), protector, diagnostics);

    /// <summary>Creates the store for the Station profile at its standard location.</summary>
    /// <param name="protector">
    /// Secret protector; defaults to <see cref="SecretProtector.Default"/>.
    /// </param>
    /// <param name="diagnostics">Optional sink for load and save diagnostics.</param>
    /// <returns>A store bound to <c>%AppData%\RWK Station\config.json</c>.</returns>
    public static ConfigStore<StationConfig> ForStation(
        ISecretProtector? protector = null,
        Action<string>? diagnostics = null)
        => new(GetConfigFilePath(StationFolderName), protector, diagnostics);
}

/// <summary>
/// Loads and saves a profile record as JSON, with DPAPI-protected secrets and atomic writes
/// (12.1, 12.2, 12.3, 12.6).
/// </summary>
/// <typeparam name="T">
/// The profile record. Must be default-constructible, because a missing or corrupt file is
/// answered with <c>new T()</c>.
/// </typeparam>
/// <remarks>
/// <para><strong>Load never throws.</strong> A missing file, an empty file, malformed JSON, a
/// permissions failure, and a secret that cannot be decrypted all resolve to a usable profile
/// — defaults for the whole file, or an unset secret when only that field is bad — so that a
/// damaged profile can never prevent the application from starting (12.6). The next
/// <see cref="Save"/> replaces the damaged file with a valid one.</para>
/// <para><strong>Save is atomic.</strong> The JSON is written to a sibling temporary file,
/// flushed to disk, and then swapped over the target. An interrupted save therefore cannot
/// leave a truncated profile behind, which matters precisely because a truncated profile
/// would be indistinguishable from a corrupt one and would silently reset the user's
/// settings on the next start.</para>
/// <para>
/// _Requirements: 12.1, 12.2, 12.3, 12.6_
/// </para>
/// </remarks>
public sealed class ConfigStore<T>
    where T : class, new()
{
    private const string TempFileSuffix = ".tmp";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly JsonSerializerOptions _options;
    private readonly Action<string>? _diagnostics;

    /// <summary>Initializes a store bound to a profile file.</summary>
    /// <param name="filePath">Absolute path of the JSON profile file.</param>
    /// <param name="protector">
    /// Encrypts and decrypts the profile's secret fields; defaults to
    /// <see cref="SecretProtector.Default"/>.
    /// </param>
    /// <param name="diagnostics">
    /// Optional sink receiving a message when a profile is missing, unreadable, corrupt, or
    /// carries a secret that could not be decrypted. Never receives a secret value.
    /// </param>
    public ConfigStore(string filePath, ISecretProtector? protector = null, Action<string>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        FilePath = filePath;
        _diagnostics = diagnostics;
        _options = CreateOptions(protector ?? SecretProtector.Default, diagnostics);
    }

    /// <summary>Gets the path of the profile file this store reads and writes.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Reads the profile, falling back to defaults for anything that cannot be read (12.6).
    /// </summary>
    /// <returns>
    /// The persisted profile, or a default instance when the file is missing or unusable.
    /// This method does not throw.
    /// </returns>
    public T Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                _diagnostics?.Invoke($"Configuration file '{FilePath}' does not exist; using defaults.");
                return new T();
            }

            string json = File.ReadAllText(FilePath, Utf8NoBom);
            if (string.IsNullOrWhiteSpace(json))
            {
                _diagnostics?.Invoke($"Configuration file '{FilePath}' is empty; using defaults.");
                return new T();
            }

            T? loaded = JsonSerializer.Deserialize<T>(json, _options);
            if (loaded is null)
            {
                _diagnostics?.Invoke($"Configuration file '{FilePath}' contained no object; using defaults.");
                return new T();
            }

            return loaded;
        }
        catch (Exception ex)
        {
            // Deliberately broad. The contract for this method is that it always yields a
            // usable profile: nothing a damaged or inaccessible file can do is allowed to
            // stop the application from starting (12.6). The failure is reported, and the
            // next Save writes a valid file over the damaged one.
            _diagnostics?.Invoke(
                $"Configuration file '{FilePath}' could not be loaded ({ex.GetType().Name}: {ex.Message}); using defaults.");
            return new T();
        }
    }

    /// <summary>
    /// Writes the profile atomically, encrypting its secret fields (12.1, 12.2).
    /// </summary>
    /// <param name="config">The profile to persist.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="IOException">The file could not be written or swapped into place.</exception>
    public void Save(T config)
    {
        ArgumentNullException.ThrowIfNull(config);

        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(config, _options);
        string tempPath = FilePath + TempFileSuffix;

        // Write the complete document to a temporary file and force it to disk before the
        // swap, so an interruption leaves either the previous profile or the new one and
        // never a half-written file.
        using (var stream = new FileStream(
                   tempPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            byte[] bytes = Utf8NoBom.GetBytes(json);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        ReplaceFile(tempPath, FilePath);
    }

    /// <summary>
    /// Writes the profile atomically, reporting failure instead of throwing.
    /// </summary>
    /// <param name="config">The profile to persist.</param>
    /// <returns><see langword="true"/> if the profile was written.</returns>
    public bool TrySave(T config)
    {
        try
        {
            Save(config);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _diagnostics?.Invoke(
                $"Configuration file '{FilePath}' could not be saved ({ex.GetType().Name}: {ex.Message}).");
            return false;
        }
    }

    private static JsonSerializerOptions CreateOptions(ISecretProtector protector, Action<string>? diagnostics)
    {
        var options = new JsonSerializerOptions
        {
            // The source-generated context supplies all profile metadata; runtime converters
            // added below still take precedence over the generated metadata.
            TypeInfoResolver = ConfigJsonContext.Default,
            WriteIndented = true
        };

        options.Converters.Add(
            new TailscaleConfigJsonConverter(
                new DpapiProtectedStringJsonConverter(protector, diagnostics)));

        return options;
    }

    private static void ReplaceFile(string tempPath, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            try
            {
                File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException)
            {
                // File.Replace is unavailable on some volumes and network shares; fall through
                // to Move, which is still a single rename on NTFS.
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        File.Move(tempPath, targetPath, overwrite: true);
    }
}
