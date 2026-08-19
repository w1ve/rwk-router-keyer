using RWK.Client.Audio;
using RWK.Shared.Config;
using Xunit;

namespace RWK.Client.Tests.Audio;

/// <summary>
/// Engine-level tests that do not open an audio device. Anything requiring a real WASAPI
/// endpoint (latency, actual playback) is out of reach of an automated test here.
/// </summary>
public class LocalSidetoneEngineTests
{
    [Fact]
    public void DefaultsMatchSidetoneConfig()
    {
        var config = new SidetoneConfig();
        using var engine = new LocalSidetoneEngine();

        Assert.Equal(config.FrequencyHz, engine.ToneFrequency);
        Assert.Equal(config.Volume, engine.Volume);
    }

    /// <summary>4.1: shared-mode buffer is 20ms.</summary>
    [Fact]
    public void BufferIsTwentyMilliseconds()
    {
        Assert.Equal(20, LocalSidetoneEngine.BufferMilliseconds);
    }

    /// <summary>4.3: frequency clamps to 300-1500 instead of throwing.</summary>
    [Theory]
    [InlineData(100, 300)]
    [InlineData(600, 600)]
    [InlineData(9000, 1500)]
    public void ToneFrequencyIsClamped(int requested, int expected)
    {
        using var engine = new LocalSidetoneEngine { ToneFrequency = requested };
        Assert.Equal(expected, engine.ToneFrequency);
    }

    /// <summary>4.5: volume clamps to 0.0-1.0 instead of throwing.</summary>
    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(0.75, 0.75)]
    [InlineData(3.0, 1.0)]
    public void VolumeIsClamped(double requested, double expected)
    {
        using var engine = new LocalSidetoneEngine { Volume = requested };
        Assert.Equal(expected, engine.Volume);
    }

    /// <summary>
    /// Keying an uninitialized engine is a no-op, not a crash — the keyer core calls KeyDown
    /// unconditionally and must never be coupled to audio availability (4.7).
    /// </summary>
    [Fact]
    public void KeyingBeforeInitializeIsHarmless()
    {
        using var engine = new LocalSidetoneEngine();

        engine.KeyDown();
        engine.KeyUp();
        engine.Stop();

        Assert.False(engine.IsPlaying);
        Assert.Null(engine.ActiveDeviceId);
    }

    [Fact]
    public void StopIsIdempotentAndDisposeAfterStopIsSafe()
    {
        var engine = new LocalSidetoneEngine();

        engine.Stop();
        engine.Stop();
        engine.Dispose();
        engine.Dispose();
    }

    /// <summary>4.6: the device list always offers "follow the system default".</summary>
    [Fact]
    public void GetOutputDevicesAlwaysOffersTheDefaultEntry()
    {
        var devices = LocalSidetoneEngine.GetOutputDevices();

        Assert.NotEmpty(devices);
        Assert.True(devices[0].IsDefault);
        Assert.Equal(AudioDeviceInfo.DefaultDeviceName, devices[0].Name);
    }
}
