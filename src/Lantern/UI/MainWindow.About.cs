using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Lantern.Core;

namespace Lantern.UI
{
    /// <summary>The Credits page in the left rail and Settings → Credits: the project links and the Discord "copy" buttons.</summary>
    public partial class MainWindow
    {
        void WireAbout()
        {
            CreditsTitle.Text = About.Name + " " + About.Version;
            CreditsVersion.Text = About.Tagline + " · MIT license";
            CreditsProfileLabel.Text = "github.com/" + About.GitHubUser;

            CreditsGitHub.Click += (s, e) => OpenLink(About.RepositoryUrl);
            CreditsProfile.Click += (s, e) => OpenLink(About.GitHubProfileUrl);
            CreditsIssues.Click += (s, e) => OpenLink(About.IssuesUrl);
            CreditsReleases.Click += (s, e) => OpenLink(About.ReleasesUrl);
            DiscordCopy.Click += (s, e) => CopyWithFeedback(About.DiscordName, DiscordCopyGlyph, DiscordCopyLabel, "Copy " + About.DiscordName);

            WireCreditsPage();
        }

        /// <summary>The Credits page: the hero, the four link cards and the facts underneath.</summary>
        void WireCreditsPage()
        {
            CrName.Text = About.Name;
            CrTagline.Text = About.Tagline;
            CrVersion.Text = "v" + About.Version + "  ·  MIT license  ·  Free and open source";
            CrAuthor.Text = "Made by " + About.Author;
            CrGitHubSub.Text = "Browse the code on GitHub";
            CrDiscordLabel.Text = About.DiscordName + " · click to copy";

            CrHeroGitHub.Click += (s, e) => OpenLink(About.RepositoryUrl);
            CrGitHub.Click += (s, e) => OpenLink(About.RepositoryUrl);
            CrIssues.Click += (s, e) => OpenLink(About.IssuesUrl);
            CrReleases.Click += (s, e) => OpenLink(About.ReleasesUrl);
            CrAuthorLink.Click += (s, e) => OpenLink(About.GitHubProfileUrl);
            CrDiscord.Click += (s, e) => CopyWithFeedback(About.DiscordName, CrDiscordGlyph, CrDiscordLabel, About.DiscordName + " · click to copy");
            CrHeroDiscord.Click += (s, e) => CopyWithFeedback(About.DiscordName, CrHeroDiscordGlyph, CrHeroDiscordLabel, "Copy " + About.DiscordName);
        }

        /// <summary>Link cards sit two across when there is room, one above the other when there is not.</summary>
        void ApplyCreditsLayout(double contentWidth, double gutter)
        {
            if (CrRoot == null) return;
            CrRoot.Margin = new Thickness(gutter, 24, gutter, 40);
            CrLinks.Columns = contentWidth < 640 ? 1 : 2;
            CrHero.Padding = contentWidth < 520 ? new Thickness(16, 18, 16, 18) : new Thickness(28, 26, 28, 26);
        }

        /// <summary>Opens one of the project's own links in the default browser.</summary>
        static void OpenLink(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Write("Open link: " + ex.Message); }
        }

        void CopyWithFeedback(string text, System.Windows.Controls.TextBlock glyph, System.Windows.Controls.TextBlock label, string idleLabel)
        {
            try { ClipboardSet(text); }
            catch (Exception ex) { Logger.Write("Copy: " + ex.Message); ShowToast("Couldn't reach the clipboard.", true); return; }
            glyph.Text = "";
            label.Text = "Copied";
            var back = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            back.Tick += (s, e) => { back.Stop(); glyph.Text = ""; label.Text = idleLabel; };
            back.Start();
        }
    }
}
