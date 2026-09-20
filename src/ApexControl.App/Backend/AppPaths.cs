using System.IO;
namespace ApexControl.App.Backend;

public static class AppPaths
{
    // Next to the solution when run from a checkout (shared with the console tools' backups folder);
    // otherwise under the user's local application data.
    public static string BackupsRoot { get; } = Locate();

    private static string Locate()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ApexControl.sln")))
                return Path.Combine(dir.FullName, "backups");

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApexControl", "backups");
    }
}
