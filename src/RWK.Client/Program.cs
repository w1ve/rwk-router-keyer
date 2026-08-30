/*
 * Copyright (c) 2026 Gerry Hull, W1VE
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction privileges...
 *
 * SPDX-License-Identifier: MIT
 */
namespace RWK.Client;

internal static class Program
{
    private const string MutexName = "Global\\RWK_Client_SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "RWK Client is already running.",
                "RWK Client",
                MessageBoxButtons.OK,
                MessageBoxIcon.Exclamation);
            return;
        }

        ApplicationConfiguration.Initialize();

        // Global exception traps — surface a toast and log rather than crashing silently.
        Application.ThreadException += (_, e) =>
        {
            try { RWK.Shared.IO.RotatingFileLog.Append("client.log", $"UNHANDLED UI EXCEPTION: {e.Exception}"); } catch { }
            MainForm.NotifySystemError();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { RWK.Shared.IO.RotatingFileLog.Append("client.log", $"UNHANDLED EXCEPTION: {e.ExceptionObject}"); } catch { }
            MainForm.NotifySystemError();
        };

        Application.Run(new MainForm());
    }
}
