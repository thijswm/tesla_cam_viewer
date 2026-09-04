using System.Reflection;

namespace TeslaCamViewer;

public static class AppVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return "dev";
        }

        var version = informational.Split('+', 2)[0].Trim();
        return string.IsNullOrWhiteSpace(version) ? "dev" : version;
    }
}
