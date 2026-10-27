namespace Adelaide.Web.Tests;

/// <summary>
/// Where the out-of-process harnesses find the two things they need to start a real Adelaide.Web child: the dotnet
/// MUXER that will host it, and a LAUNCHABLE Adelaide.Web.dll.
///
/// WHY THIS FILE EXISTS. Both harnesses (<see cref="HttpSurfaceFixture"/> and <see cref="FaultHost"/>) carried their
/// own private copy of this logic, written on the Windows dev box and — because both were staged, untracked
/// artifacts — never executed anywhere else. The first CI run after they were installed failed 125 tests at fixture
/// creation, every one of them on `cannot locate dotnet.exe`, and the DLL lookup behind it would have failed next.
/// Two copies of a platform assumption is two places to fix it and one place to forget; there is one copy now.
///
/// THE TWO ENVIRONMENTS THIS MUST SATISFY, both real:
///  • The Windows dev box — a USER-LOCAL SDK install (C:\Program Files\dotnet carries only the shared runtime), and
///    build output optionally redirected via ADELAIDE_ARTIFACTS_PATH (Directory.Build.props), so the app lands in a SIBLING
///    `Adelaide.Web/` directory of the test assembly's own output.
///  • Linux CI — the muxer is `dotnet` (no `.exe`, resolved via PATH/DOTNET_ROOT), and Directory.Build.props
///    leaves the artifacts redirect off unless ADELAIDE_ARTIFACTS_PATH is set, so the stock per-project
///    `src/Adelaide.Web/bin/&lt;config&gt;/&lt;tfm&gt;/` layout applies instead.
///
/// Both overrides (ADELAIDE_TEST_DOTNET, ADELAIDE_TEST_WEB_DLL) still win, for the environment nobody predicted.
/// </summary>
internal static class TestHostPaths
{
    /// <summary>The dotnet muxer that will host the child.</summary>
    internal static string Dotnet()
    {
        if (Environment.GetEnvironmentVariable("ADELAIDE_TEST_DOTNET") is { Length: > 0 } o && File.Exists(o)) return o;
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        // The muxer hosting THIS test run, when there is one — the same SDK by construction.
        if (Environment.ProcessPath is { } pp && Path.GetFileName(pp).Equals(exe, StringComparison.OrdinalIgnoreCase)) return pp;

        // DOTNET_ROOT: set by actions/setup-dotnet on CI, and by the dev box's run scripts.
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, exe)))
            return Path.Combine(root, exe);

        // PATH, the way `which dotnet` resolves it. A malformed entry must not abort the search.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* invalid chars in a PATH entry — skip it */ }
        }

        foreach (var wellKnown in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet", exe),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", exe),
                     $"/usr/share/dotnet/{exe}", $"/usr/lib/dotnet/{exe}", $"/usr/local/share/dotnet/{exe}",
                 })
            if (File.Exists(wellKnown)) return wellKnown;

        throw new InvalidOperationException($"cannot locate {exe} (set ADELAIDE_TEST_DOTNET)");
    }

    /// <summary>
    /// A LAUNCHABLE Adelaide.Web.dll — one with its <c>.runtimeconfig.json</c> beside it, because `dotnet &lt;dll&gt;`
    /// cannot start without one (which also rules out the bare reference copy sitting in the test's own output).
    /// Searches both layouts described on the class, then prefers this test assembly's own build configuration and
    /// the most recent build — so a stale sibling configuration can never be launched against a fresh test, the
    /// stale-binary trap CLAUDE.md rule 5 exists for.
    /// </summary>
    internal static string WebDll()
    {
        if (Environment.GetEnvironmentVariable("ADELAIDE_TEST_WEB_DLL") is { Length: > 0 } o && File.Exists(o)) return o;

        var candidates = new List<string>();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            foreach (var root in new[] { Path.Combine(dir.FullName, "Adelaide.Web"), Path.Combine(dir.FullName, "src", "Adelaide.Web") })
                if (Directory.Exists(root))
                    candidates.AddRange(Directory.GetFiles(root, "Adelaide.Web.dll", SearchOption.AllDirectories)
                        .Where(f => File.Exists(Path.ChangeExtension(f, ".runtimeconfig.json"))));
            if (candidates.Count > 0) break;
        }
        if (candidates.Count == 0)
            throw new InvalidOperationException($"cannot locate a launchable Adelaide.Web.dll from {AppContext.BaseDirectory} (set ADELAIDE_TEST_WEB_DLL)");

        var sep = Path.DirectorySeparatorChar;
        var config = AppContext.BaseDirectory.Contains($"{sep}release{sep}", StringComparison.OrdinalIgnoreCase) ? "release" : "debug";
        var sameConfig = candidates.Where(f => f.Contains($"{sep}{config}{sep}", StringComparison.OrdinalIgnoreCase)).ToList();
        return (sameConfig.Count > 0 ? sameConfig : candidates).OrderByDescending(File.GetLastWriteTimeUtc).First();
    }
}
