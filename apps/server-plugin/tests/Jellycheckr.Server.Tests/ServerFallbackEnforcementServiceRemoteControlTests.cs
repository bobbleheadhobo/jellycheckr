using System.Reflection;
using Jellycheckr.Contracts;
using Jellycheckr.Server.Models;
using Jellycheckr.Server.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellycheckr.Server.Tests;

public sealed class ServerFallbackEnforcementServiceRemoteControlTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-02-26T18:00:00Z");

    [Fact]
    public async Task Trigger_SkipsSessionWithoutRemoteControl_AndKeepsCounters()
    {
        var store = new SessionStateStore(NullLogger<SessionStateStore>.Instance);
        var state = CreateTriggeredState(store);
        var dispatcher = new StubJellyfinSessionCommandDispatcher { CanReceive = false };
        var logger = new ListLogger<ServerFallbackEnforcementService>();
        var service = CreateService(store, dispatcher, logger);
        var ticksBefore = state.ServerFallbackPlaybackTicksSinceReset;

        await InvokeEvaluateSessionsAsync(service, CreateConfig(dryRun: false));
        await InvokeEvaluateSessionsAsync(service, CreateConfig(dryRun: false));

        Assert.Empty(dispatcher.SentCommands);
        Assert.Equal(ServerFallbackPhase.Monitoring, state.FallbackPhase);
        Assert.Null(state.LastFallbackAction);
        Assert.Equal(ticksBefore, state.ServerFallbackPlaybackTicksSinceReset);
        Assert.Equal("skip:no_remote_control", state.LastFallbackDecisionKey);
        var decisionLog = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message.Contains("Fallback decision session=", StringComparison.Ordinal));
        Assert.Contains("trigger=False reason=no_remote_control", decisionLog.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("Server fallback trigger", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Trigger_ResumesOnNextTick_OnceRemoteControlIsAvailable()
    {
        var store = new SessionStateStore(NullLogger<SessionStateStore>.Instance);
        var state = CreateTriggeredState(store);
        var dispatcher = new StubJellyfinSessionCommandDispatcher { CanReceive = false };
        var service = CreateService(store, dispatcher, new ListLogger<ServerFallbackEnforcementService>());
        var config = CreateConfig(dryRun: false);

        await InvokeEvaluateSessionsAsync(service, config);
        dispatcher.CanReceive = true;
        await InvokeEvaluateSessionsAsync(service, config);

        Assert.Equal(new[] { "message", "pause" }, dispatcher.SentCommands);
        Assert.Equal(ServerFallbackPhase.PauseGracePending, state.FallbackPhase);
        Assert.Equal("sent", state.LastFallbackActionResult);
    }

    [Fact]
    public async Task FailedPause_DoesNotScheduleStop()
    {
        var store = new SessionStateStore(NullLogger<SessionStateStore>.Instance);
        var state = CreateTriggeredState(store);
        var dispatcher = new StubJellyfinSessionCommandDispatcher();
        dispatcher.FailingCommands.Add("pause");
        var service = CreateService(store, dispatcher, new ListLogger<ServerFallbackEnforcementService>());

        await InvokeEvaluateSessionsAsync(service, CreateConfig(dryRun: false));

        Assert.Equal(new[] { "message", "pause" }, dispatcher.SentCommands);
        Assert.Equal(ServerFallbackPhase.Monitoring, state.FallbackPhase);
        Assert.Null(state.PauseGraceDeadlineUtc);
        Assert.Equal("failed", state.LastFallbackActionResult);
        Assert.Equal(Now.AddMinutes(30), state.NextEligiblePromptUtc);
    }

    [Theory]
    [InlineData(true, "sent_after_grace")]
    [InlineData(false, "failed_after_grace")]
    public async Task GraceExpiry_ReportsWhetherStopWasDelivered(bool canReceive, string expectedResult)
    {
        var store = new SessionStateStore(NullLogger<SessionStateStore>.Instance);
        var state = CreateTriggeredState(store);
        state.IsPaused = true;
        state.FallbackPhase = ServerFallbackPhase.PauseGracePending;
        state.PauseIssuedUtc = Now.AddSeconds(-60);
        state.PauseGraceDeadlineUtc = Now.AddSeconds(-1);
        state.LastInferredActivityUtc = Now.AddMinutes(-2);
        var dispatcher = new StubJellyfinSessionCommandDispatcher { CanReceive = canReceive };
        var service = CreateService(store, dispatcher, new ListLogger<ServerFallbackEnforcementService>());

        await InvokeEvaluateSessionsAsync(service, CreateConfig(dryRun: false));

        Assert.Equal(new[] { "stop" }, dispatcher.SentCommands);
        Assert.Equal(expectedResult, state.LastFallbackActionResult);
        Assert.Equal(ServerFallbackPhase.Monitoring, state.FallbackPhase);
        Assert.Equal(canReceive ? DateTimeOffset.MinValue : Now.AddMinutes(30), state.NextEligiblePromptUtc);
    }

    [Fact]
    public async Task DryRun_StillReportsTrigger_ForSessionWithoutRemoteControl()
    {
        var store = new SessionStateStore(NullLogger<SessionStateStore>.Instance);
        var state = CreateTriggeredState(store);
        var dispatcher = new StubJellyfinSessionCommandDispatcher { CanReceive = false };
        var logger = new ListLogger<ServerFallbackEnforcementService>();
        var service = CreateService(store, dispatcher, logger);

        await InvokeEvaluateSessionsAsync(service, CreateConfig(dryRun: true));

        Assert.Empty(dispatcher.SentCommands);
        Assert.Equal("dry_run", state.LastFallbackAction);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("action=dry_run", StringComparison.Ordinal)
            && entry.Message.Contains("remoteControl=False", StringComparison.Ordinal));
    }

    private static SessionState CreateTriggeredState(SessionStateStore store)
    {
        var state = store.GetOrCreate("s1");
        state.ClientName = "Android TV";
        state.DeviceName = "Living Room";
        state.CurrentItemId = "item1";
        state.IsPaused = false;
        state.LastSeenUtc = Now;
        state.ServerFallbackPlaybackTicksSinceReset = TimeSpan.FromSeconds(30).Ticks;
        return state;
    }

    private static EffectiveConfigResponse CreateConfig(bool dryRun)
        => new StubConfigService(new PluginConfig
        {
            DeveloperMode = true,
            DeveloperPromptAfterSeconds = 15,
            ServerFallbackDryRun = dryRun
        }).GetEffectiveConfig(null);

    private static ServerFallbackEnforcementService CreateService(
        SessionStateStore store,
        StubJellyfinSessionCommandDispatcher dispatcher,
        ListLogger<ServerFallbackEnforcementService> logger)
    {
        return new ServerFallbackEnforcementService(
            store,
            new StubConfigService(),
            new StubSessionSnapshotProvider(),
            new SessionStateObserver(NullLogger<SessionStateObserver>.Instance),
            new ServerFallbackDecisionEngine(),
            dispatcher,
            new FakeClock(Now),
            logger);
    }

    private static Task InvokeEvaluateSessionsAsync(ServerFallbackEnforcementService service, EffectiveConfigResponse config)
    {
        var method = typeof(ServerFallbackEnforcementService).GetMethod(
            "EvaluateSessionsAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        return Assert.IsAssignableFrom<Task>(method!.Invoke(service, new object[] { Now, config, CancellationToken.None }));
    }
}
