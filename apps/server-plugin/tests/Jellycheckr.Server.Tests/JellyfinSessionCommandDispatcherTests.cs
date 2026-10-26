using System.Reflection;
using Jellycheckr.Server.Services;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellycheckr.Server.Tests;

public sealed class JellyfinSessionCommandDispatcherTests
{
    [Fact]
    public void CanReceiveCommands_IsFalse_ForRestOnlySession()
    {
        var (dispatcher, _) = CreateDispatcher(CreateSession("s1"));

        Assert.False(dispatcher.CanReceiveCommands("s1"));
    }

    [Fact]
    public void CanReceiveCommands_IsFalse_WhenControllerCannotDeliver()
    {
        var (dispatcher, _) = CreateDispatcher(CreateSession("s1", new StubSessionController(supportsMediaControl: false)));

        Assert.False(dispatcher.CanReceiveCommands("s1"));
    }

    [Fact]
    public void CanReceiveCommands_IsTrue_WhenControllerCanDeliver()
    {
        var (dispatcher, _) = CreateDispatcher(CreateSession("s1", new StubSessionController(supportsMediaControl: true)));

        Assert.True(dispatcher.CanReceiveCommands("s1"));
        Assert.False(dispatcher.CanReceiveCommands("other"));
    }

    [Fact]
    public void CanReceiveCommands_IsFalse_WhenControllerIsDisposed()
    {
        var (dispatcher, _) = CreateDispatcher(CreateSession("s1", new DisposedSessionController()));

        Assert.False(dispatcher.CanReceiveCommands("s1"));
    }

    [Fact]
    public async Task TrySend_ReportsFailure_AndSendsNothing_ForRestOnlySession()
    {
        var (dispatcher, sessionManager) = CreateDispatcher(CreateSession("s1"));

        Assert.False(await dispatcher.TrySendMessageAsync("s1", null, "still watching?", CancellationToken.None));
        Assert.False(await dispatcher.TrySendPauseAsync("s1", null, CancellationToken.None));
        Assert.False(await dispatcher.TrySendStopAsync("s1", null, CancellationToken.None));
        Assert.Empty(sessionManager.SentCommands);
    }

    [Fact]
    public async Task TrySend_SendsCommands_WhenControllerCanDeliver()
    {
        var (dispatcher, sessionManager) = CreateDispatcher(CreateSession("s1", new StubSessionController(supportsMediaControl: true)));

        Assert.True(await dispatcher.TrySendMessageAsync("s1", null, "still watching?", CancellationToken.None));
        Assert.True(await dispatcher.TrySendPauseAsync("s1", null, CancellationToken.None));
        Assert.True(await dispatcher.TrySendStopAsync("s1", null, CancellationToken.None));

        Assert.Equal(
            new[] { nameof(ISessionManager.SendMessageCommand), nameof(ISessionManager.SendPlaystateCommand), nameof(ISessionManager.SendPlaystateCommand) },
            sessionManager.SentCommands);
    }

    private static SessionInfo CreateSession(string id, params ISessionController[] controllers)
        => new(null!, NullLogger.Instance) { Id = id, SessionControllers = controllers };

    private static (JellyfinSessionCommandDispatcher Dispatcher, FakeSessionManager SessionManager) CreateDispatcher(params SessionInfo[] sessions)
    {
        var sessionManager = DispatchProxy.Create<ISessionManager, FakeSessionManager>();
        var fake = (FakeSessionManager)(object)sessionManager;
        fake.Sessions = sessions;

        var dispatcher = new JellyfinSessionCommandDispatcher(
            new SingleServiceProvider(sessionManager),
            NullLogger<JellyfinSessionCommandDispatcher>.Instance);
        return (dispatcher, fake);
    }

    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly ISessionManager _sessionManager;

        public SingleServiceProvider(ISessionManager sessionManager)
        {
            _sessionManager = sessionManager;
        }

        public object? GetService(Type serviceType)
            => serviceType == typeof(ISessionManager) ? _sessionManager : null;
    }

    private sealed class StubSessionController : ISessionController
    {
        public StubSessionController(bool supportsMediaControl)
        {
            SupportsMediaControl = supportsMediaControl;
        }

        public bool IsSessionActive => SupportsMediaControl;

        public bool SupportsMediaControl { get; }

        public Task SendMessage<T>(SessionMessageType name, Guid messageId, T data, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    // Mirrors Jellyfin's WebSocketController after its session closed.
    private sealed class DisposedSessionController : ISessionController
    {
        public bool IsSessionActive => throw new ObjectDisposedException(nameof(DisposedSessionController));

        public bool SupportsMediaControl => throw new ObjectDisposedException(nameof(DisposedSessionController));

        public Task SendMessage<T>(SessionMessageType name, Guid messageId, T data, CancellationToken cancellationToken)
            => throw new ObjectDisposedException(nameof(DisposedSessionController));
    }

    // ISessionManager is large; only the members the dispatcher uses are implemented.
    public class FakeSessionManager : DispatchProxy
    {
        public IReadOnlyList<SessionInfo> Sessions { get; set; } = Array.Empty<SessionInfo>();

        public List<string> SentCommands { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_Sessions":
                    return Sessions;
                case nameof(ISessionManager.SendMessageCommand):
                case nameof(ISessionManager.SendPlaystateCommand):
                    SentCommands.Add(targetMethod.Name);
                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException($"{targetMethod?.Name} is not faked.");
            }
        }
    }
}
