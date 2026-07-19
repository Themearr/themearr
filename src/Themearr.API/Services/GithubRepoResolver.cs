namespace Themearr.API.Services;

/// <summary>
/// Single source of truth for which GitHub repo Themearr considers itself to be. Used
/// by <see cref="UpdateService"/> (checking for new releases) and
/// <see cref="Health.HealthDto"/> (linking health messages back to the README) so a
/// fork configured via either mechanism gets correct behaviour — and correct links,
/// which is the entire point of those health messages — in both places instead of
/// silently 404ing against the upstream repo.
/// </summary>
public static class GithubRepoResolver
{
    public static string Resolve(IConfiguration config) =>
        Environment.GetEnvironmentVariable("GITHUB_REPO")
        ?? config["Themearr:GithubRepo"]
        ?? "Themearr/themearr";
}
