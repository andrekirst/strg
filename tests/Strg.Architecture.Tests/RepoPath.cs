namespace Strg.Architecture.Tests;

/// <summary>
/// Locates the repo root by walking up from <see cref="AppContext.BaseDirectory"/> until the
/// <c>strg.slnx</c> sentinel is found. Source-text ArchTests use this to anchor relative
/// paths to source files — robust to CI vs. local checkout path differences.
/// </summary>
internal static class RepoPath
{
    public static readonly string Root = FindRepoRoot();

    public static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(Root, relativePath));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "strg.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate repo root — no strg.slnx marker found walking up from " +
                AppContext.BaseDirectory);
    }
}
