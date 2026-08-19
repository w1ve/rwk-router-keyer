namespace RWK.Client.Audio;

/// <summary>
/// An audio render endpoint offered to the operator for sidetone output (4.6).
/// </summary>
/// <param name="Id">
/// MMDevice identifier. The empty string represents "system default", which is stored in
/// configuration in preference to a concrete identifier when the operator has not chosen a
/// specific device — it survives hardware changes.
/// </param>
/// <param name="Name">Friendly name for display.</param>
public sealed record AudioDeviceInfo(string Id, string Name)
{
    /// <summary>Identifier value meaning "follow the system default render endpoint".</summary>
    public const string DefaultDeviceId = "";

    /// <summary>Display label for the default endpoint entry.</summary>
    public const string DefaultDeviceName = "(Default Device)";

    /// <summary>The synthetic entry representing the system default endpoint.</summary>
    public static AudioDeviceInfo Default { get; } = new(DefaultDeviceId, DefaultDeviceName);

    /// <summary>True when this entry is the synthetic default endpoint.</summary>
    public bool IsDefault => string.IsNullOrEmpty(Id);

    public override string ToString() => Name;
}
