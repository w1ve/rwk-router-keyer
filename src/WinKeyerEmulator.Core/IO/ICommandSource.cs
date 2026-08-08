namespace WinKeyerEmulator.Core.IO;

/// <summary>
/// Abstraction for a source of incoming WinKeyer protocol command bytes.
/// </summary>
public interface ICommandSource : IDisposable
{
    /// <summary>
    /// Raised when one or more command bytes are received from the source.
    /// </summary>
    event EventHandler<byte[]> DataReceived;

    /// <summary>
    /// Begins listening for incoming command data.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops listening and releases transport resources.
    /// </summary>
    void Stop();
}
