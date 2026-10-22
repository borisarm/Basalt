namespace Basalt.iOS;

/// <summary>Native entry point; Avalonia's <see cref="AppDelegate"/> starts the single-view app.</summary>
public static class Program
{
    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
