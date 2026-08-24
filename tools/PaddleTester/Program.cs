/*
 * RWK Paddle Tester — Mouse-driven paddle simulator over serial port.
 * Left mouse = Dit (drives RTS), Right mouse = Dah (drives DTR).
 * Connect to a VSPE serial pair to feed the RWK Client paddle input.
 *
 * Copyright (c) 2026 Gerry Hull, W1VE — MIT License
 */
namespace PaddleTester;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
