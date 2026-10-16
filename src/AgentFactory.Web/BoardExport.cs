namespace AgentFactory;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

/// <summary>
/// The Board app's static export, served by this process at the root beside its JSON API.
/// </summary>
/// <remarks>
/// <para>
/// The Board is a Next static export with <c>trailingSlash: true</c>, so every route is a
/// directory with an <c>index.html</c> (<c>projects/index.html</c>,
/// <c>work-items/index.html</c>) and the build also writes a <c>.html</c> alias beside
/// each one (<c>projects.html</c>), for the two spellings of the same address. Static
/// files and the directory index are served by the framework's own handlers; what this
/// class adds is the two cases those handlers deliberately do not cover, and both are
/// about the boundary between the Board and the API rather than about the export.
/// </para>
/// <para>
/// <strong>It adds no route.</strong> The fallback below is middleware rather than a
/// mapped endpoint, because an endpoint would appear in the process's route table — and
/// the route table is the JSON API's, pinned as the whole of what is addressable. A
/// fallback that arrived as a route would also be a route that could swallow <c>/api</c>,
/// which is the one thing it must never do.
/// </para>
/// <para>
/// The file provider is named rather than left to the content root: the content root is a
/// factory *data* directory and never carries a <c>wwwroot</c> of its own, so the app is a
/// property of the application rather than of wherever a deployment put it.
/// </para>
/// </remarks>
public static class BoardExport
{
    /// <summary>
    /// Serves the export under <paramref name="directory"/>, if it is there. A checkout
    /// that has not built the app has no export and no Board; that is said once, out loud,
    /// rather than answered with a 404 for every route.
    /// </summary>
    public static void Serve(WebApplication app, string directory)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!Directory.Exists(directory))
        {
            // Said rather than left as an app that answers every route with a 404: a
            // checkout that has not run `pnpm build` publishes no export, and the copy
            // that ships is put here by the `CopyTheBoardsExport` target.
            app.Logger.LogWarning(
                "No wwwroot beside the entry assembly at {Directory}, so the Board app is not served: "
                    + "the export ships there and this build did not put it there",
                directory);
            return;
        }

        var webRoot = new PhysicalFileProvider(directory);

        // Default files, so a directory address serves its own index rather than a 404.
        // `/projects/` resolves to `projects/index.html`, and the root's front door is
        // `wwwroot/index.html`. Declared with the same provider as the static files,
        // because that provider is the published directory rather than the content root.
        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = webRoot,
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = webRoot,
            ServeUnknownFileTypes = false,
        });

        // Everything the two handlers above did not answer. Routing has already run, so a
        // matched endpoint is left to run and an unmatched one arrives here with no
        // endpoint at all.
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint() is not null)
            {
                await next();
                return;
            }

            // An unknown `/api` path is an API caller's 404, not the Board. Handing a JSON
            // client an HTML page would be the process answering a question nobody asked,
            // and it is the one place the fallback must not reach.
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync("""{"error":"no such endpoint"}""");
                return;
            }

            // A route spelled without its trailing slash — `/projects` for
            // `/projects/` — is the `.html` alias the export writes beside the directory
            // index. A cold load of either spelling is the same page.
            var alias = webRoot.GetFileInfo($"{context.Request.Path.Value?.TrimStart('/')}.html");
            if (alias.Exists)
            {
                await SendAsync(context, alias, StatusCodes.Status200OK);
                return;
            }

            // Nothing here. The Board's own not-found page, with the status that says so
            // — a file the export already ships, so a reader gets the app's answer rather
            // than an empty body.
            var notFound = webRoot.GetFileInfo("404.html");
            if (notFound.Exists)
            {
                await SendAsync(context, notFound, StatusCodes.Status404NotFound);
            }
        });
    }

    /// <summary>
    /// One exported page, as HTML. A static export is one content type's worth of files
    /// at these addresses, so the type is named here rather than looked up.
    /// </summary>
    private static async Task SendAsync(HttpContext context, IFileInfo file, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(file);
    }
}
