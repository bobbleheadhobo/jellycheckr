using System.Security.Claims;
using Jellycheckr.Server.Services;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellycheckr.Server.Tests;

public sealed class AuthenticatedUserIdResolverTests
{
    [Fact]
    public async Task Resolve_UsesJellyfinUserIdClaim_NormalizedToSessionFormat()
    {
        var userId = Guid.NewGuid();
        var authService = new RecordingAuthService(_ => throw new InvalidOperationException("should not be called"));
        var resolver = CreateResolver(authService);

        var resolved = await resolver.ResolveAsync(CreateContext(new Claim("Jellyfin-UserId", userId.ToString("N"))));

        Assert.Equal(userId.ToString("D"), resolved);
        Assert.Equal(0, authService.CallCount);
    }

    [Fact]
    public async Task Resolve_ReturnsNull_ForApiKeyWithoutUser()
    {
        var authService = new RecordingAuthService(_ => throw new InvalidOperationException("should not be called"));
        var resolver = CreateResolver(authService);

        var resolved = await resolver.ResolveAsync(CreateContext(new Claim("Jellyfin-UserId", Guid.Empty.ToString("N"))));

        Assert.Null(resolved);
        Assert.Equal(0, authService.CallCount);
    }

    [Fact]
    public async Task Resolve_NormalizesGuidFromOtherUserIdClaims()
    {
        var userId = Guid.NewGuid();
        var authService = new RecordingAuthService(_ => throw new InvalidOperationException("should not be called"));
        var resolver = CreateResolver(authService);

        var resolved = await resolver.ResolveAsync(CreateContext(new Claim(ClaimTypes.NameIdentifier, userId.ToString("N"))));

        Assert.Equal(userId.ToString("D"), resolved);
        Assert.Equal(0, authService.CallCount);
    }

    [Fact]
    public async Task Resolve_SkipsNonGuidUserIdClaims()
    {
        var user = new User("viewer", "auth-provider", "reset-provider");
        var authService = new RecordingAuthService(_ => new AuthorizationInfo { User = user });
        var resolver = CreateResolver(authService);

        var resolved = await resolver.ResolveAsync(CreateContext(new Claim(ClaimTypes.NameIdentifier, "viewer")));

        Assert.Equal(user.Id.ToString("D"), resolved);
        Assert.Equal(1, authService.CallCount);
    }

    [Fact]
    public async Task Resolve_FallsBackToAuthService_WhenClaimMissing()
    {
        var user = new User("viewer", "auth-provider", "reset-provider");
        var authService = new RecordingAuthService(_ => new AuthorizationInfo { User = user });
        var resolver = CreateResolver(authService);

        var resolved = await resolver.ResolveAsync(CreateContext());

        Assert.Equal(user.Id.ToString("D"), resolved);
        Assert.Equal(1, authService.CallCount);
    }

    [Fact]
    public async Task Resolve_AwaitsAuthService_InsteadOfBlocking()
    {
        var user = new User("viewer", "auth-provider", "reset-provider");
        var pending = new TaskCompletionSource<AuthorizationInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = CreateResolver(new PendingAuthService(pending.Task));

        var resolving = resolver.ResolveAsync(CreateContext());
        Assert.False(resolving.IsCompleted);

        pending.SetResult(new AuthorizationInfo { User = user });

        Assert.Equal(user.Id.ToString("D"), await resolving);
    }

    [Fact]
    public async Task Resolve_ReturnsNull_WhenAuthServiceHasNoUser()
    {
        var resolver = CreateResolver(new RecordingAuthService(_ => new AuthorizationInfo()));

        Assert.Null(await resolver.ResolveAsync(CreateContext()));
    }

    [Fact]
    public async Task Resolve_ReturnsNull_WhenAuthServiceThrows()
    {
        var resolver = CreateResolver(new RecordingAuthService(_ => throw new InvalidOperationException("auth failed")));

        Assert.Null(await resolver.ResolveAsync(CreateContext()));
    }

    private static AuthenticatedUserIdResolver CreateResolver(IAuthService authService)
        => new(authService, NullLogger<AuthenticatedUserIdResolver>.Instance);

    private static DefaultHttpContext CreateContext(params Claim[] claims)
        => new() { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };

    private sealed class RecordingAuthService : IAuthService
    {
        private readonly Func<HttpRequest, AuthorizationInfo> _authenticate;

        public RecordingAuthService(Func<HttpRequest, AuthorizationInfo> authenticate)
        {
            _authenticate = authenticate;
        }

        public int CallCount { get; private set; }

        public Task<AuthorizationInfo> Authenticate(HttpRequest request)
        {
            CallCount++;
            return Task.FromResult(_authenticate(request));
        }
    }

    private sealed class PendingAuthService : IAuthService
    {
        private readonly Task<AuthorizationInfo> _result;

        public PendingAuthService(Task<AuthorizationInfo> result)
        {
            _result = result;
        }

        public Task<AuthorizationInfo> Authenticate(HttpRequest request) => _result;
    }
}
