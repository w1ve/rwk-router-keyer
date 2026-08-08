namespace WinKeyerEmulator.Core.IO;

/// <summary>
/// Abstraction for sending WinKeyer protocol response bytes back to the host.
/// </summary>
public interface ICommandSink
{
    /// <summary>
    /// Sends response data back to the connected host.
    /// </summary>
    void SendResponse(byte[] data);
}
