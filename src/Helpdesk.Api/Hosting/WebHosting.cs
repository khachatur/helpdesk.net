using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;

namespace Helpdesk.Api.Hosting;

/// <summary>Cross-cutting web concerns: sign-in rate limiting, security headers, and serving the web app.</summary>
public static class WebHosting
{
    public const string SignInRateLimit = "sign-in";

    /// <summary>
    /// What a page served by this application may load. Scripts come only from the application
    /// itself, which is the second line of defence if unsafe HTML from an email ever got past the
    /// sanitiser. Inline styles are allowed because the UI component library needs them.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public static IServiceCollection AddHelpdeskRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        // Account lockout stops guessing one account's password. This stops one client from
        // trying many accounts, or hammering the password hasher.
        var attemptsPerMinute = configuration.GetValue("Auth:SignInAttemptsPerMinute", 10);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(SignInRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = attemptsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await Results.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Too many sign-in attempts",
                    detail: "Wait a minute and try again.").ExecuteAsync(context.HttpContext);
            };
        });

        return services;
    }

    /// <summary>
    /// Ends a request quietly when the caller has gone away. A browser that leaves a page cancels
    /// its requests, which cancels the database query underneath and surfaces as an exception.
    /// That is not a server fault, so it is answered with 499 and not logged as an error.
    /// </summary>
    public static IApplicationBuilder UseQuietClientDisconnects(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception) when (context.RequestAborted.IsCancellationRequested)
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
                }
            }
        });

    /// <summary>Adds headers that tell the browser to be strict with everything this application serves.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "same-origin";

            // The jobs dashboard is a third-party page with its own inline scripts; it is admin-only.
            if (!context.Request.Path.StartsWithSegments(Jobs.BackgroundJobs.DashboardPath))
            {
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
            }

            await next();
        });

    /// <summary>
    /// Serves the built web app, when there is one, so that one deployment carries both the API and
    /// the pages. The folder is Web:Root (default "wwwroot" under the content root). In development
    /// the Vite dev server serves the pages instead and this does nothing.
    /// </summary>
    public static void UseWebApp(this WebApplication app)
    {
        var configured = app.Configuration["Web:Root"] ?? "wwwroot";
        var root = Path.GetFullPath(configured, app.Environment.ContentRootPath);
        if (!File.Exists(Path.Combine(root, "index.html")))
        {
            return;
        }

        var files = new PhysicalFileProvider(root);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = context =>
            {
                // Vite puts a content hash in every file name under /assets, so those never change.
                // index.html must always be fetched fresh, or a deployment would not reach open browsers.
                context.Context.Response.Headers.CacheControl =
                    context.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache";
            },
        });

        // Any other path that is not the API or the jobs dashboard is a page of the web app, which
        // does its own routing. The shell is public; the data behind it is not.
        // Paths that look like a file (they have an extension) are left to the static files above,
        // which only serve when no route has claimed the request.
        RequestDelegate shell = async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.SendFileAsync(files.GetFileInfo("index.html"));
        };
        app.MapGet("/", shell).AllowAnonymous().ExcludeFromDescription();
        app.MapFallback("{*path:nonfile:regex(^(?!api(/|$)|hangfire(/|$)).*$)}", shell).AllowAnonymous();

        app.Logger.LogInformation("Serving the web app from {Root}", root);
    }
}
