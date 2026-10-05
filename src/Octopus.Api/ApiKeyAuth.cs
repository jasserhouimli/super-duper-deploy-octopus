using Octopus.Deployments;
using Octopus.Deployments.ApiKeys;

namespace Octopus.Api;

/// <summary>
/// Bearer API-key gate for the control plane. Rules live in
/// <see cref="ApiKeyGate"/> (unit-tested); this is the thin HTTP adapter.
/// Raw keys never hit logs — only key IDs are recorded.
/// </summary>
public static class ApiKeyAuth
{
    public const string ItemKey = "Octopus.ApiKeyId";

    internal static string? BearerToken(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = header["Bearer ".Length..].Trim();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        return null;
    }
}

public static class ApiKeyAuthExtensions
{
    public static IApplicationBuilder UseApiKeyAuth(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            OctopusDbContext db;
            try
            {
                db = ctx.RequestServices.GetRequiredService<OctopusDbContext>();
            }
            catch (InvalidOperationException)
            {
                await next();
                return;
            }
            var decision = await ApiKeyGate.AuthorizeAsync(
                db, ctx.Request.Method, ctx.Request.Path.Value ?? "/", ApiKeyAuth.BearerToken(ctx), ctx.RequestAborted);
            if (decision.Allowed)
            {
                if (decision.KeyId is not null)
                    ctx.Items[ApiKeyAuth.ItemKey] = decision.KeyId;
                await next();
                return;
            }
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "Valid API key required (Authorization: Bearer oct_...)." });
        });
}
