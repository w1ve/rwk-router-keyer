using System.Text.Json;
using System.Text.Json.Serialization;

namespace RWK.Shared.Config;

/// <summary>
/// Serializes <see cref="TailscaleConfig"/>, encrypting
/// <see cref="TailscaleConfig.AuthKey"/> and <see cref="TailscaleConfig.PairingSecret"/> with
/// DPAPI on write and decrypting them on read (12.2, 12.3).
/// </summary>
/// <remarks>
/// The converter is applied to the whole record rather than to the two properties because
/// <see cref="TailscaleConfig"/> stores its secrets as plain <c>string?</c> at runtime — the
/// record is deliberately unaware of encryption. Converting here keeps every layout decision
/// about which fields are sensitive in one place, next to the store that owns the file.
/// <para>
/// Reads are lenient: unknown properties are skipped, a value of the wrong shape falls back
/// to unset, and a payload that is not an object yields defaults. A profile written by a
/// newer build, or one that has been hand-edited, must not fail the load (12.6).
/// </para>
/// <para>
/// _Requirements: 12.2, 12.3, 12.6_
/// </para>
/// </remarks>
public sealed class TailscaleConfigJsonConverter : JsonConverter<TailscaleConfig>
{
    private const string AuthKeyProperty = "AuthKey";
    private const string PairingSecretProperty = "PairingSecret";
    private const string StationAddressProperty = "StationAddress";

    private readonly DpapiProtectedStringJsonConverter _secretConverter;

    /// <summary>
    /// Initializes a converter using <see cref="SecretProtector.Default"/> and no
    /// diagnostics sink.
    /// </summary>
    public TailscaleConfigJsonConverter()
        : this(new DpapiProtectedStringJsonConverter())
    {
    }

    /// <summary>Initializes a converter with an explicit secret converter.</summary>
    /// <param name="secretConverter">Handles the two DPAPI-protected fields.</param>
    public TailscaleConfigJsonConverter(DpapiProtectedStringJsonConverter secretConverter)
    {
        ArgumentNullException.ThrowIfNull(secretConverter);
        _secretConverter = secretConverter;
    }

    /// <inheritdoc />
    public override TailscaleConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new TailscaleConfig();
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return new TailscaleConfig();
        }

        string? authKey = null;
        string? pairingSecret = null;
        string? stationAddress = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                reader.Skip();
                continue;
            }

            string propertyName = reader.GetString() ?? string.Empty;
            reader.Read();

            if (propertyName.Equals(AuthKeyProperty, StringComparison.OrdinalIgnoreCase))
            {
                authKey = _secretConverter.Read(ref reader, typeof(DpapiProtectedString), options).Plaintext;
            }
            else if (propertyName.Equals(PairingSecretProperty, StringComparison.OrdinalIgnoreCase))
            {
                pairingSecret = _secretConverter.Read(ref reader, typeof(DpapiProtectedString), options).Plaintext;
            }
            else if (propertyName.Equals(StationAddressProperty, StringComparison.OrdinalIgnoreCase))
            {
                stationAddress = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Null))
                {
                    reader.Skip();
                }
            }
            else
            {
                reader.Skip();
            }
        }

        return new TailscaleConfig
        {
            AuthKey = authKey,
            PairingSecret = pairingSecret,
            StationAddress = stationAddress
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TailscaleConfig value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();

        writer.WritePropertyName(AuthKeyProperty);
        _secretConverter.Write(writer, new DpapiProtectedString(value.AuthKey), options);

        writer.WritePropertyName(PairingSecretProperty);
        _secretConverter.Write(writer, new DpapiProtectedString(value.PairingSecret), options);

        writer.WritePropertyName(StationAddressProperty);
        if (value.StationAddress is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value.StationAddress);
        }

        writer.WriteEndObject();
    }
}
