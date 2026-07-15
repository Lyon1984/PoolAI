namespace PoolAI.ArchitectureTests;

internal static class RepositoryRoot
{
    public static string Find()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PoolAI.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PoolAI repository root.");
    }
}
