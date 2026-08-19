using Xunit;

namespace RWK.Shared.Tests.Primitives;

/// <summary>
/// Guards the shared primitive contracts declared by task 3.2: the members each enum is
/// required to expose, and the numeric values that are persisted in configuration or
/// carried across the wire and therefore must not be renumbered.
/// </summary>
public class PrimitiveContractTests
{
    [Fact]
    public void KeyerMode_declares_the_five_specified_modes()
    {
        Assert.Equal(
            new[] { "IambicB", "IambicA", "Ultimatic", "Bug", "Straight" },
            Enum.GetNames<KeyerMode>());
    }

    [Fact]
    public void KeyingLine_preserves_v1_numeric_values_and_adds_None()
    {
        // v1 (WinKeyerEmulator.Core.IO.KeyingLine) declared DTR then RTS, so DTR is 0.
        // Persisted v1 settings and the existing SerialKeyingOutput mapping depend on it.
        Assert.Equal(0, (int)KeyingLine.DTR);
        Assert.Equal(1, (int)KeyingLine.RTS);
        Assert.Equal(2, (int)KeyingLine.None);
    }

    [Fact]
    public void TailscaleState_declares_the_four_specified_states()
    {
        Assert.Equal(
            new[] { "Disconnected", "Connecting", "Connected", "Fault" },
            Enum.GetNames<TailscaleState>());
    }

    [Fact]
    public void PathType_declares_None_Direct_and_Derp()
    {
        Assert.Equal(new[] { "None", "Direct", "Derp" }, Enum.GetNames<PathType>());
        Assert.Equal(0, (int)PathType.None);
    }

    [Fact]
    public void SessionState_declares_the_four_specified_states()
    {
        Assert.Equal(
            new[] { "Authenticating", "Active", "Degraded", "Closed" },
            Enum.GetNames<SessionState>());
    }

    [Fact]
    public void ForwardProtocol_declares_Tcp_and_Udp()
    {
        Assert.Equal(new[] { "Tcp", "Udp" }, Enum.GetNames<ForwardProtocol>());
    }

    [Fact]
    public void ForwardRuleStatus_declares_Idle_Listening_Active_and_Error()
    {
        Assert.Equal(
            new[] { "Idle", "Listening", "Active", "Error" },
            Enum.GetNames<ForwardRuleStatus>());
    }

    [Fact]
    public void FailSafeCondition_declares_F1_through_F10_with_matching_numeric_values()
    {
        var expected = Enumerable.Range(1, 10).Select(n => $"F{n}").ToArray();
        Assert.Equal(expected, Enum.GetNames<FailSafeCondition>());

        foreach (var value in Enum.GetValues<FailSafeCondition>())
        {
            var fNumber = int.Parse(value.ToString().AsSpan(1));
            Assert.Equal(fNumber, (int)value);
        }
    }

    [Fact]
    public void EdgeSource_declares_Paddle_Host_and_Immediate()
    {
        Assert.Equal(new[] { "Paddle", "Host", "Immediate" }, Enum.GetNames<EdgeSource>());
    }

    [Fact]
    public void ForwardRuleStatusChangedEventArgs_carries_a_message_for_an_error_status()
    {
        // 10.15: an unavailable bind address must be named. The status enum cannot carry
        // text, so this record is the status-plus-message carrier.
        var ruleId = Guid.NewGuid();
        var args = new ForwardRuleStatusChangedEventArgs(
            ruleId,
            ForwardRuleStatus.Error,
            BytesIn: 0,
            BytesOut: 0,
            Message: "Bind address 192.168.1.50 is not present on this host.");

        Assert.Equal(ruleId, args.RuleId);
        Assert.Equal(ForwardRuleStatus.Error, args.Status);
        Assert.Contains("192.168.1.50", args.Message);
    }

    [Fact]
    public void ForwardRuleStatusChangedEventArgs_message_is_optional()
    {
        var args = new ForwardRuleStatusChangedEventArgs(Guid.NewGuid(), ForwardRuleStatus.Listening, 0, 0);
        Assert.Null(args.Message);
    }

    [Fact]
    public void EdgeEvent_compares_by_value()
    {
        var a = new EdgeEvent(123_456L, KeyDown: true, EdgeSource.Paddle);
        var b = new EdgeEvent(123_456L, KeyDown: true, EdgeSource.Paddle);
        var c = a with { Source = EdgeSource.Host };

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void PaddleStateChangedEventArgs_reports_all_three_contacts()
    {
        var args = new PaddleStateChangedEventArgs(99L, DitPressed: true, DahPressed: false, StraightKeyPressed: true);

        Assert.Equal(99L, args.QpcTimestamp);
        Assert.True(args.DitPressed);
        Assert.False(args.DahPressed);
        Assert.True(args.StraightKeyPressed);
    }

    [Fact]
    public void EdgeReplayerStateChangedEventArgs_defaults_to_no_condition_and_no_message()
    {
        var args = new EdgeReplayerStateChangedEventArgs(EdgeReplayerState.SafeLatched, IsSafeLatched: true);

        Assert.Null(args.LastCondition);
        Assert.Null(args.Message);
        Assert.True(args.IsSafeLatched);
    }

    [Fact]
    public void TailscaleStateChangedEventArgs_reports_no_path_on_fault()
    {
        var args = new TailscaleStateChangedEventArgs(
            TailscaleState.Fault,
            PathType.None,
            TimeSpan.Zero,
            DerpRegion: null,
            Message: "path lost");

        Assert.Equal(TailscaleState.Fault, args.State);
        Assert.Equal(PathType.None, args.Path);
        Assert.Null(args.DerpRegion);
    }

    [Fact]
    public void SessionEventArgs_carries_state_and_optional_reason()
    {
        var now = DateTime.UtcNow;
        var args = new SessionEventArgs("100.64.0.2", "OP1", SessionState.Closed, now, "owner disconnect");

        Assert.Equal(SessionState.Closed, args.State);
        Assert.Equal(now, args.TimestampUtc);
        Assert.Equal("owner disconnect", args.Reason);

        var started = new SessionEventArgs("100.64.0.2", "OP1", SessionState.Active, now);
        Assert.Null(started.Reason);
    }

    [Fact]
    public void FailSafeTriggeredEventArgs_carries_condition_and_message()
    {
        var args = new FailSafeTriggeredEventArgs(FailSafeCondition.F10, "scheduler overrun 312ms");

        Assert.Equal(FailSafeCondition.F10, args.Condition);
        Assert.Contains("312ms", args.Message);
    }
}
