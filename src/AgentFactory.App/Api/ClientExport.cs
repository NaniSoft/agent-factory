using Microsoft.Extensions.FileProviders;

namespace AgentFactory.App.Api;

/// <summary>
/// The React app's static export, served by this process at the root beside
/// its JSON API. The app is a Next static export with
/// <c>trailingSlash: true</c>, so every route is a directory with an
/// <c>index.html</c>; the build also writes the <c>.html</c> spelling beside
/// each one for the two ways the same address can be typed.
/// </summary>
public static class ClientExport
{
    public static void Serve(WebApplication app, string directory)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!Directory.Exists(directory))
        {
            app.Logger.LogWarning(
                "No wwwroot beside the entry assembly at {Directory}, so the Agent Factory app is not served. " +
                "Run `pnpm build` in ClientApp and build again.",
                directory);
            return;
        }

        var webRoot = new PhysicalFileProvider(directory);

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webRoot });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = webRoot,
            ServeUnknownFileTypes = false,
        });

        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint() is not null)
            {
                await next();
                return;
            }

            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync("""{"error":"no such endpoint"}""");
                return;
            }

            var alias = webRoot.GetFileInfo($"{context.Request.Path.Value?.TrimStart('/')}.html");
            if (alias.Exists)
            {
                await SendAsync(context, alias, StatusCodes.Status200OK);
                return;
            }

            var notFound = webRoot.GetFileInfo("404.html");
            if (notFound.Exists)
            {
                await SendAsync(context, notFound, StatusCodes.Status404NotFound);
            }
        });
    }

    private static async Task SendAsync(HttpContext context, IFileInfo file, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(file);
    }
}
