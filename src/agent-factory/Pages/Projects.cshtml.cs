namespace AgentFactory.Pages;

using AgentFactory.Credentials;
using AgentFactory.Projects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>
/// The projects surface: what the factory serves, and the one place a repository is
/// added, removed, or given a credential. The file stays the single source of truth —
/// this page writes <c>factories/&lt;project&gt;.yaml</c> and the secrets directory, and
/// reads nothing back that the loader did not already say.
/// </summary>
/// <remarks>
/// The loader reads <c>factories/</c> once, at start (the loader research, ticket #28).
/// A file written here is served after a restart, and the page says so plainly rather
/// than pretending a write took effect. Credential values are write-only: the page shows
/// whether a name has a value, never the value itself (#32).
/// </remarks>
public class ProjectsModel(
    ProjectLoadReport projects,
    FactoryOptions options,
    CompositeCredentialReader credentials) : PageModel
{
    public ProjectLoadReport Projects => projects;

    public FactoryOptions Options => options;

    /// <summary>The project a just-succeeded add named, said back in the query string.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Added { get; set; }

    /// <summary>Why an add did not write anything, said back in the query string.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Problem { get; set; }

    public void OnGet()
    {
    }

    /// <summary>Whether a credential name has a value, from either source. Presence only —
    /// the page never shows what the value is.</summary>
    public bool CredentialPresent(string name) => credentials.Has(name);

    public IActionResult OnPostAdd(
        string name,
        string repoUrl,
        string workerImage,
        string llmModel,
        string githubKeyName,
        string llmKeyName)
    {
        var refused = ProjectFiles.Write(
            options.FactoriesDirectory,
            (name ?? string.Empty).Trim(),
            (repoUrl ?? string.Empty).Trim(),
            (workerImage ?? string.Empty).Trim(),
            (llmModel ?? string.Empty).Trim(),
            (githubKeyName ?? string.Empty).Trim(),
            (llmKeyName ?? string.Empty).Trim());

        if (refused is { } problem)
        {
            return RedirectToPage(new { problem });
        }

        return RedirectToPage(new { added = (name ?? string.Empty).Trim() });
    }

    public IActionResult OnPostRemove(string name)
    {
        if (ProjectFiles.IsServableName(name ?? string.Empty))
        {
            ProjectFiles.Delete(options.FactoriesDirectory, name!);
        }

        return RedirectToPage();
    }

    /// <summary>
    /// The credentials form's one write. A value arrives, is written into the secrets
    /// directory, and is never rendered again — the page shows presence, not content.
    /// </summary>
    public IActionResult OnPostCredential(string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
        {
            credentials.Write(name.Trim(), value);
        }

        return RedirectToPage();
    }
}
