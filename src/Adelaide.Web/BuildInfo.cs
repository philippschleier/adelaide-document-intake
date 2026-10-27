using System.Reflection;

namespace Adelaide.Web;

/// <summary>Build identity for the startup banner, <c>/version</c> and <c>/health</c> — so an operator, or a future
/// troubleshooting agent, can tell at a glance WHICH build is running and since when (the recurring "is the running
/// code the latest?" question). <see cref="Version"/> carries the git commit when built from a repository: the .NET SDK
/// appends <c>+{sha}</c> to the assembly informational version.</summary>
public static class BuildInfo
{
    public static readonly string Version =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static readonly DateTimeOffset StartedUtc = DateTimeOffset.UtcNow;
}
