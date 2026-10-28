namespace Lantern.Core
{
    /// <summary>Who made Lantern and where to find the project. Shown on the Credits page and in Settings → Credits.</summary>
    public static class About
    {
        public const string Name = "Lantern";
        public const string Tagline = "Roblox account manager";
        public const string Version = "1.0.0";

        public const string Author = "foxems";
        public const string GitHubUser = "foxems";
        public const string Repository = "LRAM";
        public const string DiscordName = "foxems";

        public static string GitHubProfileUrl => "https://github.com/" + GitHubUser;
        public static string RepositoryUrl => GitHubProfileUrl + "/" + Repository;
        public static string IssuesUrl => RepositoryUrl + "/issues";
        public static string ReleasesUrl => RepositoryUrl + "/releases";
        public static string SecurityUrl => RepositoryUrl + "/blob/main/SECURITY.md";
    }
}
