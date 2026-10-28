using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace Lantern.Core
{
    /// <summary>A saved launch ("these accounts, this game") as the profiles list binds to it.</summary>
    public sealed class ProfileItem : INotifyPropertyChanged
    {
        public ProfileItem(ProfileData data) { Data = data; }
        public ProfileData Data { get; }
        public event PropertyChangedEventHandler PropertyChanged;

        public string Name => string.IsNullOrWhiteSpace(Data.Name) ? "Untitled profile" : Data.Name;
        public string Initial => Name.Substring(0, 1).ToUpperInvariant();

        string _subtitle = "";
        public string Subtitle
        {
            get => _subtitle;
            private set { _subtitle = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle))); }
        }

        public void Refresh(System.Collections.Generic.IEnumerable<AccountItem> accounts)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Initial)));
            var all = accounts.ToList();
            int have = Data.AccountIds.Count(id => all.Any(a => a.Data.Id == id));
            int gone = Data.AccountIds.Count - have;

            var parsed = TargetParser.Parse(Data.TargetRaw, Data.JobId, Data.BackupRaw);
            string where = !string.IsNullOrWhiteSpace(Data.GameName) ? Data.GameName
                         : parsed.Ok && parsed.Target.PlaceId > 0 ? "Place " + parsed.Target.PlaceId : "private server";
            string kind = !parsed.Ok ? "link no longer valid" : !string.IsNullOrEmpty(parsed.Target.JobId) ? "specific server"
                        : parsed.Target.IsPrivate ? (parsed.Target.Backup != null ? "private server + backup" : "private server") : "public server";
            var perf = string.IsNullOrEmpty(Data.PerfMode) ? "" : " · " + Data.PerfMode.ToLowerInvariant() + " mode";

            Subtitle = have + (have == 1 ? " account" : " accounts") + (gone > 0 ? " (" + gone + " removed)" : "") +
                       " · " + where + " · " + kind + perf;
        }
    }

    public sealed class ProfileLibrary
    {
        public readonly ObservableCollection<ProfileItem> Profiles = new ObservableCollection<ProfileItem>();
        public bool Demo { get; set; }

        public void Load()
        {
            foreach (var p in Store.LoadPlain<ProfilesFile>(Paths.Profiles).Profiles)
                Profiles.Add(new ProfileItem(p));
        }

        public void Save()
        {
            if (Demo) return;
            try { Store.SavePlain(Paths.Profiles, new ProfilesFile { Profiles = Profiles.Select(p => p.Data).ToList() }); }
            catch (Exception ex) { Logger.Write("Profiles save: " + ex.Message); }
        }

        public ProfileItem Add(string name, System.Collections.Generic.IEnumerable<AccountItem> accounts, string targetRaw, string jobId, string gameName, string perfMode = "", string backupRaw = "")
        {
            var item = new ProfileItem(new ProfileData
            {
                Name = (name ?? "").Trim(),
                AccountIds = accounts.Select(a => a.Data.Id).ToList(),
                TargetRaw = (targetRaw ?? "").Trim(),
                JobId = (jobId ?? "").Trim(),
                BackupRaw = (backupRaw ?? "").Trim(),
                GameName = gameName ?? "",
                PerfMode = perfMode ?? ""
            });
            Profiles.Add(item);
            Save();
            return item;
        }

        public void Remove(ProfileItem p) { Profiles.Remove(p); Save(); }
    }
}
