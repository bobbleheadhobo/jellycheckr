using System.Security.Claims;
using Jellycheckr.Server.Infrastructure;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellycheckr.Server.Services;

public interface IAuthenticatedUserIdResolver
{
    ValueTask<string?> ResolveAsync(HttpContext? httpContext);
}

public sealed class AuthenticatedUserIdResolver : IAuthenticatedUserIdResolver
{
    // Jellyfin's authentication handler stores the user id under this claim (InternalClaimTypes.UserId).
    private const string JellyfinUserIdClaimType = "Jellyfin-UserId";

    private static readonly string[] UserIdClaimTypes =
    {
        ClaimTypes.NameIdentifier,
        "sub",
        "uid",
        "user_id",
        "userId"
    };

    private readonly IAuthService _authService;
    private readonly ILogger<AuthenticatedUserIdResolver> _logger;

    public AuthenticatedUserIdResolver(IAuthService authService, ILogger<AuthenticatedUserIdResolver> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    public async ValueTask<string?> ResolveAsync(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return null;
        }

        var principal = httpContext.User;

        // The claim holds an "N"-formatted GUID; session snapshots use the "D" format, so normalize it.
        // API keys authenticate without a user and carry Guid.Empty.
        if (Guid.TryParse(principal.FindFirstValue(JellyfinUserIdClaimType), out var jellyfinUserId))
        {
            return FormatUserId(jellyfinUserId);
        }

        foreach (var claimType in UserIdClaimTypes)
        {
            var claimValue = principal.FindFirstValue(claimType);
            if (!string.IsNullOrWhiteSpace(claimValue))
            {
                return claimValue;
            }
        }

        // Jellyfin's authentication handler sets the claim above on every authenticated request; this only runs if that changes.
        try
        {
            var authorizationInfo = await _authService.Authenticate(httpContext.Request).ConfigureAwait(false);
            if (authorizationInfo is not null)
            {
                return FormatUserId(authorizationInfo.UserId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogJellycheckrDebug(ex, "[Jellycheckr] Failed to resolve authenticated user id from authorization context.");
        }

        return null;
    }

    private static string? FormatUserId(Guid userId)
    {
        return userId == Guid.Empty ? null : userId.ToString("D");
    }
}
