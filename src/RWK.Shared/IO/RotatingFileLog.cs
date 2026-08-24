/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Shared.IO;

/// <summary>
/// Simple file-based logger with size-based rotation. When the current log exceeds
/// <see cref="MaxSizeBytes"/>, it is renamed with a numeric suffix (-1, -2, etc.) and
/// a fresh file is started. Thread-safe via lock.
/// </summary>
public static class RotatingFileLog
{
    /// <summary>Maximum log file size before rotation (default 10 KB).</summary>
    public const long MaxSizeBytes = 10 * 1024;

    /// <summary>Maximum number of rotated files to keep per log name.</summary>
    public const int MaxRotatedFiles = 5;

    private static readonly object _lock = new();

    /// <summary>
    /// Appends a timestamped message to the specified log file, rotating if necessary.
    /// </summary>
    /// <param name="logFileName">Log file name (e.g. "client.log"). Placed in AppContext.BaseDirectory.</param>
    /// <param name="message">The message to log (no trailing newline needed).</param>
    public static void Append(string logFileName, string message)
    {
        string dir = AppContext.BaseDirectory;
        string path = Path.Combine(dir, logFileName);

        lock (_lock)
        {
            try
            {
                // Rotate if current file exceeds max size
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if (info.Length >= MaxSizeBytes)
                    {
                        Rotate(dir, logFileName);
                    }
                }

                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            }
            catch
            {
                // Best effort — never crash on logging
            }
        }
    }

    /// <summary>
    /// Deletes all log files matching a given base name pattern (base + rotated variants).
    /// </summary>
    /// <param name="logFileNames">Base log file names (e.g. "client.log", "winkeyer.log").</param>
    /// <returns>Number of files deleted.</returns>
    public static int DeleteAll(params string[] logFileNames)
    {
        string dir = AppContext.BaseDirectory;
        int deleted = 0;

        lock (_lock)
        {
            foreach (string baseName in logFileNames)
            {
                string nameWithoutExt = Path.GetFileNameWithoutExtension(baseName);
                string ext = Path.GetExtension(baseName);

                // Delete the base file
                string basePath = Path.Combine(dir, baseName);
                if (TryDeleteFile(basePath)) deleted++;

                // Delete rotated files: name-1.log, name-2.log, etc.
                for (int i = 1; i <= MaxRotatedFiles + 2; i++)
                {
                    string rotatedPath = Path.Combine(dir, $"{nameWithoutExt}-{i}{ext}");
                    if (TryDeleteFile(rotatedPath)) deleted++;
                }
            }
        }

        return deleted;
    }

    /// <summary>
    /// Finds and returns all log files (base + rotated) for the given file names.
    /// </summary>
    public static string[] FindAll(params string[] logFileNames)
    {
        string dir = AppContext.BaseDirectory;
        var found = new List<string>();

        foreach (string baseName in logFileNames)
        {
            string nameWithoutExt = Path.GetFileNameWithoutExtension(baseName);
            string ext = Path.GetExtension(baseName);

            string basePath = Path.Combine(dir, baseName);
            if (File.Exists(basePath)) found.Add(basePath);

            for (int i = 1; i <= MaxRotatedFiles + 2; i++)
            {
                string rotatedPath = Path.Combine(dir, $"{nameWithoutExt}-{i}{ext}");
                if (File.Exists(rotatedPath)) found.Add(rotatedPath);
            }
        }

        return found.ToArray();
    }

    private static void Rotate(string dir, string logFileName)
    {
        string nameWithoutExt = Path.GetFileNameWithoutExtension(logFileName);
        string ext = Path.GetExtension(logFileName);
        string basePath = Path.Combine(dir, logFileName);

        // Shift existing rotated files: -4 → -5, -3 → -4, etc.
        for (int i = MaxRotatedFiles; i >= 1; i--)
        {
            string src = Path.Combine(dir, $"{nameWithoutExt}-{i}{ext}");
            string dst = Path.Combine(dir, $"{nameWithoutExt}-{i + 1}{ext}");
            if (File.Exists(src))
            {
                if (i == MaxRotatedFiles)
                    TryDeleteFile(src); // Delete the oldest
                else
                    try { File.Move(src, dst, overwrite: true); } catch { }
            }
        }

        // Move current log to -1
        string first = Path.Combine(dir, $"{nameWithoutExt}-1{ext}");
        try { File.Move(basePath, first, overwrite: true); } catch { }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch { }
        return false;
    }
}
