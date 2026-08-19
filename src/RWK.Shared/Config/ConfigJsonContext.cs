using System.Text.Json.Serialization;

namespace RWK.Shared.Config;

/// <summary>
/// Source-generated serialization metadata for the persisted profiles (12.1).
/// </summary>
/// <remarks>
/// Source generation is used rather than reflection so that profile serialization stays
/// trim- and AOT-friendly and does not depend on runtime code generation. New properties
/// added to <see cref="ClientConfig"/> or <see cref="StationConfig"/> are picked up
/// automatically on rebuild; nothing here needs editing for a new field.
/// <para>
/// The DPAPI converter is not registered here. It carries an
/// <see cref="ISecretProtector"/> instance, so it is added to the
/// <see cref="System.Text.Json.JsonSerializerOptions"/> that <see cref="ConfigStore{T}"/>
/// builds; the generated metadata honors converters registered at runtime.
/// </para>
/// <para>
/// _Requirements: 12.1_
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ClientConfig))]
[JsonSerializable(typeof(StationConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext
{
}
