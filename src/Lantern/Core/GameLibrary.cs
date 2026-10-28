using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;

namespace Lantern.Core
{
    public sealed class GameItem : INotifyPropertyChanged
    {
        public GameItem(GameData data) { Data = data; }
        public GameData Data { get; }
        public event PropertyChangedEventHandler PropertyChanged;

        public string Name => string.IsNullOrWhiteSpace(Data.Name) ? "Place " + Data.PlaceId : Data.Name;
        public string Subtitle => "Place " + Data.PlaceId + (Data.Private ? " · private server" : "");
        public string Initial => Name.Substring(0, 1).ToUpperInvariant();

        ImageSource _icon;
        public ImageSource Icon
        {
            get => _icon;
            set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasIcon))); }
        }
        public bool HasIcon => _icon != null;
    }

    public sealed class GameLibrary
    {
        public readonly ObservableCollection<GameItem> Games = new ObservableCollection<GameItem>();
        readonly ConcurrentDictionary<long, GameInfo> _infoCache = new ConcurrentDictionary<long, GameInfo>();

        public void Load()
        {
            foreach (var g in Store.LoadPlain<GamesFile>(Paths.Games).Games)
            {
                var item = new GameItem(g);
                Games.Add(item);
                LoadIcon(item);
            }
        }

        public void Save()
        {
            try { Store.SavePlain(Paths.Games, new GamesFile { Games = Games.Select(g => g.Data).ToList() }); }
            catch (Exception ex) { Logger.Write("Games save: " + ex.Message); }
        }

        static string IconPath(long universeId) => Path.Combine(Paths.Cache, "game-" + universeId + ".png");

        void LoadIcon(GameItem item)
        {
            if (item.Data.UniverseId <= 0) return;
            var path = IconPath(item.Data.UniverseId);
            if (!File.Exists(path)) return;
            Task.Run(() => InstanceManager.LoadBitmap(path, 96)).ContinueWith(t =>
            {
                if (t.Result != null) item.Icon = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Cached name lookup used by the launch bar while the user types.</summary>
        public async Task<GameInfo> InfoAsync(long placeId)
        {
            if (_infoCache.TryGetValue(placeId, out var hit)) return hit;
            var info = await RobloxApi.GetGameInfoAsync(placeId);
            if (info != null) _infoCache[placeId] = info;
            return info;
        }

        public async Task<(GameItem item, string error)> AddAsync(string raw)
        {
            var parsed = TargetParser.Parse(raw, "");
            if (parsed.IsEmpty) return (null, "Paste a place id or a game link first.");
            if (!string.IsNullOrEmpty(parsed.Error)) return (null, parsed.Error);
            if (parsed.Target.PlaceId <= 0)
                return (null, "Share links can't be saved on their own — paste the game's page link or place id.");

            var existing = Games.FirstOrDefault(g => g.Data.PlaceId == parsed.Target.PlaceId && g.Data.Private == parsed.Target.IsPrivate
                                                     && (!g.Data.Private || g.Data.Raw == raw.Trim()));
            if (existing != null) return (existing, null);

            var info = await InfoAsync(parsed.Target.PlaceId);
            if (info == null) return (null, "Roblox has no game with that place id (or it couldn't be reached).");

            var data = new GameData
            {
                PlaceId = parsed.Target.PlaceId,
                UniverseId = info.UniverseId,
                Name = info.Name,
                Raw = raw.Trim(),
                Private = parsed.Target.IsPrivate
            };
            var item = new GameItem(data);
            Games.Add(item);
            Save();

            if (!string.IsNullOrEmpty(info.IconUrl))
            {
                var bytes = await RobloxApi.DownloadImageAsync(info.IconUrl);
                if (bytes != null)
                {
                    try { Paths.Ensure(); File.WriteAllBytes(IconPath(info.UniverseId), bytes); LoadIcon(item); } catch { }
                }
            }
            return (item, null);
        }

        public void Remove(GameItem item)
        {
            Games.Remove(item);
            Save();
        }
    }
}
