using System.Security.Claims;
using Jellycheckr.Contracts;
using Jellycheckr.Server.Controllers;
using Jellycheckr.Server.Infrastructure;
using Jellycheckr.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellycheckr.Server.Tests;

public sealed class AyswControllerTests
{
    [Fact]
    public async Task Ack_ReturnsForbidden_WhenSessionOwnershipFails()
    {
        var controller = CreateController(allowMutations: false);

        var result = await controller.Ack("s1", new AckRequest { AckType = "continue" });

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    [Fact]
    public async Task Interaction_ReturnsForbidden_WhenSessionOwnershipFails()
    {
        var controller = CreateController(allowMutations: false);

        var result = await controller.Interaction("s1", new InteractionRequest { EventType = "keydown" });

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    [Fact]
    public async Task PromptShown_ReturnsForbidden_WhenSessionOwnershipFails()
    {
        var controller = CreateController(allowMutations: false);

        var result = await controller.PromptShown("s1", new PromptShownRequest { TimeoutSeconds = 30 });

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    private static AyswController CreateController(bool allowMutations)
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, "u1") },
                "test"));

        var controller = new AyswController(
            new StubConfigService(),
            new ThrowingAckService(),
            new StubSessionOwnershipValidator { AllowMutations = allowMutations },
            new StubAuthenticatedUserIdResolver("u1"),
            new FakeClock(DateTimeOffset.Parse("2026-03-01T10:00:00Z")),
            NullLogger<AyswController>.Instance);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = principal
            }
        };

        return controller;
    }

    private sealed class ThrowingAckService : IAckService
    {
        public AckResponse HandleAck(string sessionId, string? userId, AckRequest request, EffectiveConfigResponse config)
        {
            throw new InvalidOperationException("Ack service should not have been called.");
        }

        public InteractionResponse HandleInteraction(string sessionId, string? userId, InteractionRequest request)
        {
            throw new InvalidOperationException("Ack service should not have been called.");
        }

        public void MarkPromptActive(string sessionId, string? userId, DateTimeOffset promptDeadlineUtc, string? clientType)
        {
            throw new InvalidOperationException("Ack service should not have been called.");
        }
    }

    private sealed class StubAuthenticatedUserIdResolver : IAuthenticatedUserIdResolver
    {
        private readonly string? _userId;

        public StubAuthenticatedUserIdResolver(string? userId)
        {
            _userId = userId;
        }

        public ValueTask<string?> ResolveAsync(HttpContext? httpContext)
        {
            return ValueTask.FromResult(_userId);
        }
    }
}
