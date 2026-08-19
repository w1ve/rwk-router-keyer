namespace RWK.Shared.Keying;

/// <summary>
/// A single Morse element decided by <see cref="KeyerElementEngine"/>.
/// </summary>
/// <remarks>
/// Replaces the <c>char</c> ('.', '-', '\0') element encoding used by the RWK v1
/// <c>SoftKeyer</c>. v1 needed characters because it accumulated a pattern string and
/// decoded it back into ASCII; v2 emits edges rather than decoded characters, so the
/// element only has to name a duration.
/// <para>
/// _Requirements: 3.1, 3.10_
/// </para>
/// </remarks>
public enum KeyerElement
{
    /// <summary>No element is wanted: the paddles are idle (or the mode generates no elements).</summary>
    None = 0,

    /// <summary>A dit: one unit of key-down.</summary>
    Dit = 1,

    /// <summary>A dah: three units of key-down.</summary>
    Dah = 2
}
