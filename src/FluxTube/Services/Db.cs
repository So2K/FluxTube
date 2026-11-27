using System.Collections.Concurrent;
using FluxTube.Models;
using Microsoft.Data.Sqlite;

namespace FluxTube.Services;

/// <summary>Local, detailed memory of everything: watch sessions, every card that appeared on screen, downloads.</summary>
public static class Db
{
    private static readonly SqliteConnection Conn;
    private static readonly object Lock = new();
    private static readonly ConcurrentQueue<Action> _writeQueue = new();
    private static readonly ManualResetEventSlim _writeWake = new(false);
    private static readonly Thread _writerThread;
    private static volatile bool _shuttingDown;
    private static long _nextWatchSeed;
    // Write-through RAM cache for O(1) zero-lock ResumePosition & ProgressFor on the UI thread!
    private static readonly ConcurrentDictionary<string, (double Pos, double Dur)> _progressCache = new(StringComparer.Ordinal);

    static Db()
    {
        Conn = new SqliteConnection($"Data Source={Paths.Database}");
        Conn.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS watch (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                video_id TEXT NOT NULL, title TEXT, channel TEXT, channel_id TEXT,
                duration_s REAL DEFAULT 0, position_s REAL DEFAULT 0, watched_s REAL DEFAULT 0,
                started_at INTEGER NOT NULL, last_at INTEGER NOT NULL, source TEXT);
            CREATE INDEX IF NOT EXISTS ix_watch_vid ON watch(video_id);
            CREATE INDEX IF NOT EXISTS ix_watch_last ON watch(last_at DESC);
            CREATE TABLE IF NOT EXISTS seen (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                video_id TEXT NOT NULL, title TEXT, channel TEXT, channel_id TEXT, duration TEXT, meta TEXT, avatar TEXT,
                source TEXT, seen_at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_seen_at ON seen(seen_at DESC);
            CREATE INDEX IF NOT EXISTS ix_seen_vid ON seen(video_id, source);
            CREATE TABLE IF NOT EXISTS downloads (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                url TEXT NOT NULL, video_id TEXT, title TEXT, channel TEXT, file TEXT, thumb TEXT,
                quality TEXT, status TEXT, error TEXT, size_bytes INTEGER DEFAULT 0, duration_s REAL DEFAULT 0,
                added_at INTEGER NOT NULL, finished_at INTEGER);
            CREATE TABLE IF NOT EXISTS cache_video (
                video_id TEXT PRIMARY KEY, quality TEXT, vitag INTEGER, aitag INTEGER,
                plays INTEGER DEFAULT 0, last_play INTEGER NOT NULL, created INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS cache_stream (
                video_id TEXT NOT NULL, itag INTEGER NOT NULL, length INTEGER, mime TEXT, ranges TEXT, bytes INTEGER DEFAULT 0,
                PRIMARY KEY (video_id, itag));
            """);
        _nextWatchSeed = Math.Max(Convert.ToInt64(Scalar("SELECT COALESCE(MAX(id), 0) FROM watch") ?? 0L), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 100);
        _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "flux-db-writer", Priority = ThreadPriority.BelowNormal };
        _writerThread.Start();
    }

    private static void EnqueueWrite(Action work)
    {
        if (_shuttingDown)
        {
            try { lock (Lock) work(); } catch { }
            return;
        }
        _writeQueue.Enqueue(work);
        _writeWake.Set();
    }

    private static void FlushWriteBatch()
    {
        if (_writeQueue.IsEmpty) return;
        lock (Lock)
        {
            if (_writeQueue.IsEmpty) return;
            using var tx = Conn.BeginTransaction();
            int n = 0;
            while (n < 256 && _writeQueue.TryDequeue(out var act))
            {
                try { act(); } catch (Exception e) { Paths.Log("db async: " + e.Message); }
                n++;
            }
            tx.Commit();
        }
    }

    private static void WriterLoop()
    {
        while (!_shuttingDown)
        {
            _writeWake.Wait(220);
            _writeWake.Reset();
            if (!_writeQueue.IsEmpty)
            {
                if (!_shuttingDown) Thread.Sleep(45); // coalesce scroll bursts (50 Seen cards -> 1 SQLite transaction!)
                while (!_writeQueue.IsEmpty) FlushWriteBatch();
            }
        }
    }

    public static void Flush()
    {
        try { while (!_writeQueue.IsEmpty) FlushWriteBatch(); } catch { }
    }

    public static (int Plays, double WatchedSec) VideoWatchStats(string videoId)
    {
        try
        {
            var rows = Query("SELECT COUNT(*), COALESCE(SUM(watched_s), 0) FROM watch WHERE video_id=$v",
                r => (r.GetInt32(0), r.GetDouble(1)), ("$v", videoId));
            return rows.Count > 0 ? rows[0] : (0, 0);
        }
        catch { return (0, 0); }
    }

    public static (int UniqueVideos, int Sessions, double WatchedSec, string? FavTitle, double FavSec) ChannelWatchStats(string channelId, string? channelName = null)
    {
        try
        {
            var rows = !string.IsNullOrEmpty(channelId)
                ? Query("SELECT COUNT(DISTINCT video_id), COUNT(*), COALESCE(SUM(watched_s), 0) FROM watch WHERE channel_id=$c OR ($n <> '' AND channel=$n)",
                    r => (r.GetInt32(0), r.GetInt32(1), r.GetDouble(2)), ("$c", channelId), ("$n", channelName ?? ""))
                : Query("SELECT COUNT(DISTINCT video_id), COUNT(*), COALESCE(SUM(watched_s), 0) FROM watch WHERE channel=$n",
                    r => (r.GetInt32(0), r.GetInt32(1), r.GetDouble(2)), ("$n", channelName ?? ""));
            if (rows.Count == 0) return (0, 0, 0, null, 0);
            var fav = !string.IsNullOrEmpty(channelId)
                ? Query("SELECT title, SUM(watched_s) as ws FROM watch WHERE (channel_id=$c OR ($n <> '' AND channel=$n)) AND title IS NOT NULL AND title <> '' GROUP BY video_id ORDER BY ws DESC LIMIT 1",
                    r => (r.GetString(0), r.GetDouble(1)), ("$c", channelId), ("$n", channelName ?? ""))
                : Query("SELECT title, SUM(watched_s) as ws FROM watch WHERE channel=$n AND title IS NOT NULL AND title <> '' GROUP BY video_id ORDER BY ws DESC LIMIT 1",
                    r => (r.GetString(0), r.GetDouble(1)), ("$n", channelName ?? ""));
            return (rows[0].Item1, rows[0].Item2, rows[0].Item3, fav.Count > 0 ? fav[0].Item1 : null, fav.Count > 0 ? fav[0].Item2 : 0);
        }
        catch { return (0, 0, 0, null, 0); }
    }

    /// <summary>Opens the DB, preloads recent resume positions into RAM, and prunes ancient seen rows off the UI thread.</summary>
    public static void Warm()
    {
        try
        {
            foreach (var (vid, p, d) in Query("SELECT video_id, position_s, duration_s FROM watch ORDER BY last_at ASC LIMIT 2000",
                         r => (r.GetString(0), r.GetDouble(1), r.GetDouble(2))))
            {
                _progressCache[vid] = (p, d);
            }
            EnqueueWrite(() => Exec("DELETE FROM seen WHERE id NOT IN (SELECT id FROM seen ORDER BY seen_at DESC LIMIT 12000)"));
        }
        catch { }
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static void Exec(string sql, params (string, object?)[] args)
    {
        lock (Lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private static List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        lock (Lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    private static object? Scalar(string sql, params (string, object?)[] args)
    {
        lock (Lock)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            return cmd.ExecuteScalar();
        }
    }

    private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    public static DateTime FromUnix(long t) => DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime;

    // ---------------- watch history ----------------

    private static readonly ConcurrentDictionary<long, string> _watchRowVid = new();

    public static long StartWatch(VideoItem v, string source)
    {
        long id = Interlocked.Increment(ref _nextWatchSeed);
        var vid = v.VideoId;
        var title = v.Title;
        var ch = v.Channel;
        var cid = v.ChannelId;
        var now = Now;
        if (_watchRowVid.Count > 512) _watchRowVid.Clear();
        _watchRowVid[id] = vid;
        EnqueueWrite(() => Exec("INSERT INTO watch(id,video_id,title,channel,channel_id,started_at,last_at,source) VALUES($id,$v,$t,$c,$ci,$n,$n,$s)",
            ("$id", id), ("$v", vid), ("$t", title), ("$c", ch), ("$ci", cid), ("$n", now), ("$s", source)));
        return id;
    }

    public static void UpdateWatch(long id, string? title, string? channel, double position, double duration, double watchedDelta)
    {
        if (_watchRowVid.TryGetValue(id, out var vid))
        {
            double prevDur = _progressCache.TryGetValue(vid, out var prev) ? prev.Dur : 0;
            _progressCache[vid] = (position, duration > 0 ? duration : prevDur);
        }
        var now = Now;
        EnqueueWrite(() => Exec("""
            UPDATE watch SET position_s=$p, duration_s=CASE WHEN $d>0 THEN $d ELSE duration_s END,
              watched_s=watched_s+$w, last_at=$n,
              title=COALESCE(NULLIF($t,''),title), channel=COALESCE(NULLIF($c,''),channel)
            WHERE id=$id
            """, ("$p", position), ("$d", duration), ("$w", watchedDelta), ("$n", now), ("$t", title), ("$c", channel), ("$id", id)));
    }

    /// <summary>Updates the latest watch row for videoId without inserting blank ghost rows in History.</summary>
    public static void UpdateLatestPosition(string videoId, double position, double duration)
    {
        if (string.IsNullOrEmpty(videoId) || position <= 1) return;
        double prevDur = _progressCache.TryGetValue(videoId, out var prev) ? prev.Dur : 0;
        _progressCache[videoId] = (position, duration > 0 ? duration : prevDur);
        var now = Now;
        EnqueueWrite(() =>
        {
            var id = Scalar("SELECT id FROM watch WHERE video_id=$v ORDER BY last_at DESC LIMIT 1", ("$v", videoId));
            if (id is long rowId)
                Exec("UPDATE watch SET position_s=$p, duration_s=CASE WHEN $d>0 THEN $d ELSE duration_s END, last_at=$n WHERE id=$id",
                    ("$p", position), ("$d", duration), ("$n", now), ("$id", rowId));
        });
    }

    public static void Shutdown()
    {
        _shuttingDown = true;
        _writeWake.Set();
        try { _writerThread.Join(1200); } catch { }
        lock (Lock)
        {
            try
            {
                while (!_writeQueue.IsEmpty) FlushWriteBatch();
                Exec("PRAGMA wal_checkpoint(TRUNCATE);");
                Conn.Close();
            }
            catch { }
        }
    }

    /// <summary>Last saved position for resume; O(1) lock-free RAM hit when prewarmed, SQLite fallback otherwise.</summary>
    public static double? ResumePosition(string videoId)
    {
        if (_progressCache.TryGetValue(videoId, out var cached))
        {
            if (cached.Pos < 10 || (cached.Dur > 0 && cached.Pos > cached.Dur - 15)) return null;
            return cached.Pos;
        }
        var rows = Query("SELECT position_s, duration_s FROM watch WHERE video_id=$v ORDER BY last_at DESC LIMIT 1",
            r => (r.GetDouble(0), r.GetDouble(1)), ("$v", videoId));
        if (rows.Count == 0) return null;
        var (p, d) = rows[0];
        _progressCache[videoId] = (p, d);
        if (p < 10 || (d > 0 && p > d - 15)) return null;
        return p;
    }

    public static Dictionary<string, double> ProgressFor(IEnumerable<string> ids)
    {
        var list = ids.Distinct().ToList();
        var result = new Dictionary<string, double>();
        if (list.Count == 0) return result;
        var missing = new List<string>();
        foreach (var id in list)
        {
            if (_progressCache.TryGetValue(id, out var c))
            {
                if (c.Dur > 0) result[id] = Math.Clamp(c.Pos / c.Dur, 0, 1);
            }
            else missing.Add(id);
        }
        if (missing.Count == 0) return result;
        var ps = string.Join(',', missing.Select((_, i) => "$p" + i));
        var args = missing.Select((v, i) => ("$p" + i, (object?)v)).ToArray();
        foreach (var (id, p, d) in Query($"SELECT video_id, position_s, duration_s FROM watch WHERE video_id IN ({ps}) ORDER BY last_at",
                     r => (r.GetString(0), r.GetDouble(1), r.GetDouble(2)), args))
        {
            _progressCache[id] = (p, d);
            if (d > 0) result[id] = Math.Clamp(p / d, 0, 1);
        }
        return result;
    }

    public sealed record WatchRow(long Id, string VideoId, string Title, string Channel, string? ChannelId,
        double Duration, double Position, double Watched, DateTime Started, DateTime Last, string Source)
    {
        public VideoItem ToVideo() => new()
        {
            VideoId = VideoId, Title = Title, Channel = Channel, ChannelId = ChannelId,
            Duration = Fmt.Time(Duration), Meta = $"{Fmt.When(Last)} · {Fmt.Time(Position)} {L.T("из", "of")} {Fmt.Time(Duration)}",
            Progress = Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0,
        };
    }

    public static List<WatchRow> History(string? filter, int limit = 500)
        => Query("""
            SELECT id, video_id, title, channel, channel_id, duration_s, position_s, watched_s, started_at, last_at, source
            FROM watch WHERE ($f IS NULL OR title LIKE $f OR channel LIKE $f)
            ORDER BY last_at DESC LIMIT $l
            """, r => new WatchRow(r.GetInt64(0), r.GetString(1), Str(r, 2), Str(r, 3), r.IsDBNull(4) ? null : r.GetString(4),
                r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), FromUnix(r.GetInt64(8)), FromUnix(r.GetInt64(9)), Str(r, 10)),
            ("$f", string.IsNullOrWhiteSpace(filter) ? null : $"%{filter}%"), ("$l", limit));

    public static void DeleteWatch(long id) => EnqueueWrite(() => Exec("DELETE FROM watch WHERE id=$id", ("$id", id)));
    public static void ClearHistory() { _progressCache.Clear(); EnqueueWrite(() => Exec("DELETE FROM watch")); }

    public static List<string> RecentWatchedIds(int n)
        => Query("SELECT video_id FROM watch ORDER BY last_at DESC LIMIT $n", r => r.GetString(0), ("$n", Math.Max(n * 5, 24)))
            .Distinct(StringComparer.Ordinal).Take(n).ToList();

    // ---------------- seen (impressions) ----------------

    private static readonly Dictionary<string, long> SeenRecently = new();

    /// <summary>Records that a card was actually shown on screen. Batched on background SQLite writer thread (zero scroll stalls).</summary>
    public static void Seen(VideoItem v, string source)
    {
        var key = v.VideoId + "|" + source;
        var now = Now;
        lock (SeenRecently)
        {
            if (SeenRecently.TryGetValue(key, out var t) && now - t < 1800) return;
            if (SeenRecently.Count > 4000) SeenRecently.Clear();
            SeenRecently[key] = now;
        }
        var vid = v.VideoId; var title = v.Title; var ch = v.Channel; var cid = v.ChannelId;
        var dur = v.Duration; var meta = v.Meta; var av = v.AvatarUrl;
        EnqueueWrite(() => Exec("INSERT INTO seen(video_id,title,channel,channel_id,duration,meta,avatar,source,seen_at) VALUES($v,$t,$c,$ci,$d,$m,$a,$s,$n)",
            ("$v", vid), ("$t", title), ("$c", ch), ("$ci", cid), ("$d", dur), ("$m", meta),
            ("$a", av), ("$s", source), ("$n", now)));
    }

    public sealed record SeenRow(VideoItem Video, string Source, DateTime At);

    public static List<SeenRow> SeenList(string? filter, int limit = 800)
        => Query("""
            SELECT video_id, title, channel, channel_id, duration, meta, avatar, source, seen_at FROM seen
            WHERE ($f IS NULL OR title LIKE $f OR channel LIKE $f OR source LIKE $f)
            ORDER BY seen_at DESC, id DESC LIMIT $l
            """, r => new SeenRow(new VideoItem
            {
                VideoId = r.GetString(0), Title = Str(r, 1), Channel = Str(r, 2), ChannelId = r.IsDBNull(3) ? null : r.GetString(3),
                Duration = Str(r, 4), Meta = Str(r, 5), AvatarUrl = r.IsDBNull(6) ? null : r.GetString(6),
            }, Str(r, 7), FromUnix(r.GetInt64(8))),
            ("$f", string.IsNullOrWhiteSpace(filter) ? null : $"%{filter}%"), ("$l", limit));

    public static void ClearSeen() => Exec("DELETE FROM seen");

    // ---------------- downloads ----------------

    public static long AddDownload(DownloadItem d)
    {
        lock (Lock)
        {
            Exec("INSERT INTO downloads(url,video_id,title,channel,quality,status,added_at) VALUES($u,$v,$t,$c,$q,$s,$n)",
                ("$u", d.Url), ("$v", d.VideoId), ("$t", d.Title), ("$c", d.Channel), ("$q", d.Quality), ("$s", d.Status), ("$n", Now));
            return (long)Scalar("SELECT last_insert_rowid()")!;
        }
    }

    public static void UpdateDownload(DownloadItem d)
        => Exec("""
            UPDATE downloads SET title=$t, channel=$c, video_id=$v, file=$f, thumb=$th, status=$s, error=$e,
              size_bytes=$sz, duration_s=$du, finished_at=$fa WHERE id=$id
            """, ("$t", d.Title), ("$c", d.Channel), ("$v", d.VideoId), ("$f", d.File), ("$th", d.ThumbFile), ("$s", d.Status),
            ("$e", d.Error), ("$sz", d.SizeBytes), ("$du", d.DurationS), ("$fa", d.Status == "done" ? Now : null), ("$id", d.Id));

    public static List<DownloadItem> Downloads()
        => Query("SELECT id,url,video_id,title,channel,file,thumb,quality,status,error,size_bytes,duration_s,added_at FROM downloads ORDER BY added_at DESC",
            r => new DownloadItem
            {
                Id = r.GetInt64(0), Url = r.GetString(1), VideoId = r.IsDBNull(2) ? null : r.GetString(2), Title = Str(r, 3),
                Channel = Str(r, 4), File = r.IsDBNull(5) ? null : r.GetString(5), ThumbFile = r.IsDBNull(6) ? null : r.GetString(6),
                Quality = Str(r, 7), Status = Str(r, 8), Error = r.IsDBNull(9) ? null : r.GetString(9),
                SizeBytes = r.GetInt64(10), DurationS = r.GetDouble(11), AddedAt = FromUnix(r.GetInt64(12)),
            });

    public static void DeleteDownload(long id) => Exec("DELETE FROM downloads WHERE id=$id", ("$id", id));

    // ---------------- media cache index ----------------

    public static (long Length, string Mime, string Ranges)? CacheStream(string videoId, int itag)
    {
        var r = Query("SELECT length, mime, ranges FROM cache_stream WHERE video_id=$v AND itag=$i",
            x => (x.GetInt64(0), Str(x, 1), Str(x, 2)), ("$v", videoId), ("$i", itag));
        return r.Count > 0 ? r[0] : null;
    }

    public static void CacheSaveStream(string videoId, int itag, long length, string mime, string ranges, long bytes)
        => EnqueueWrite(() => Exec("""
            INSERT INTO cache_stream(video_id,itag,length,mime,ranges,bytes) VALUES($v,$i,$l,$m,$r,$b)
            ON CONFLICT(video_id,itag) DO UPDATE SET length=$l, mime=$m, ranges=$r, bytes=$b
            """, ("$v", videoId), ("$i", itag), ("$l", length), ("$m", mime), ("$r", ranges), ("$b", bytes)));

    public static (string Quality, int Vitag, int Aitag, int Plays)? CacheVideo(string videoId)
    {
        var r = Query("SELECT quality, vitag, aitag, plays FROM cache_video WHERE video_id=$v",
            x => (Str(x, 0), x.GetInt32(1), x.GetInt32(2), x.GetInt32(3)), ("$v", videoId));
        return r.Count > 0 ? r[0] : null;
    }

    /// <summary>Creates or refreshes the entry. A different quality or format replaces the old one (its files are stale).</summary>
    public static void CacheTouch(string videoId, string quality, int vitag, int aitag, bool addPlay)
    {
        var now = Now;
        EnqueueWrite(() => Exec("""
            INSERT INTO cache_video(video_id,quality,vitag,aitag,plays,last_play,created) VALUES($v,$q,$vi,$ai,$p,$n,$n)
            ON CONFLICT(video_id) DO UPDATE SET quality=$q, vitag=$vi, aitag=$ai, plays=plays+$p, last_play=$n
            """, ("$v", videoId), ("$q", quality), ("$vi", vitag), ("$ai", aitag), ("$p", addPlay ? 1 : 0), ("$n", now)));
    }

    public static List<(string Id, int Plays, long Last, long Bytes)> CacheVideos()
        => Query("""
            SELECT v.video_id, v.plays, v.last_play, COALESCE((SELECT SUM(bytes) FROM cache_stream s WHERE s.video_id=v.video_id), 0)
            FROM cache_video v
            """, x => (x.GetString(0), x.GetInt32(1), x.GetInt64(2), x.GetInt64(3)));

    public static long CacheTotalBytes() => Convert.ToInt64(Scalar("SELECT COALESCE(SUM(bytes),0) FROM cache_stream") ?? 0L);

    public static void CacheDelete(string videoId)
    {
        Exec("DELETE FROM cache_stream WHERE video_id=$v", ("$v", videoId));
        Exec("DELETE FROM cache_video WHERE video_id=$v", ("$v", videoId));
    }
}

public static class Fmt
{
    private static readonly string?[] _timeMemo = new string?[7201];

    public static string Time(double s)
    {
        if (s <= 0 || double.IsNaN(s)) return "";
        int sec = (int)s;
        if ((uint)sec <= 7200u)
            return _timeMemo[sec] ??= FormatTimeSec(sec);
        return FormatTimeSec(sec);
    }

    private static string FormatTimeSec(int sec)
    {
        int h = sec / 3600, m = (sec % 3600) / 60, sc = sec % 60;
        return h >= 1 ? $"{h}:{m:00}:{sc:00}" : $"{m}:{sc:00}";
    }

    public static string When(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return L.T("только что", "just now");
        if (d.TotalMinutes < 60) return L.En ? $"{(int)d.TotalMinutes}m ago" : $"{(int)d.TotalMinutes} мин назад";
        if (t.Date == DateTime.Today) return L.En ? $"today {t:HH:mm}" : $"сегодня {t:HH:mm}";
        if (t.Date == DateTime.Today.AddDays(-1)) return L.En ? $"yesterday {t:HH:mm}" : $"вчера {t:HH:mm}";
        return t.ToString("d MMM HH:mm", L.En ? System.Globalization.CultureInfo.InvariantCulture : System.Globalization.CultureInfo.CurrentCulture);
    }

    public static string Day(DateTime t)
    {
        if (t.Date == DateTime.Today) return L.T("Сегодня", "Today");
        if (t.Date == DateTime.Today.AddDays(-1)) return L.T("Вчера", "Yesterday");
        return t.ToString("dddd, d MMMM", L.En ? System.Globalization.CultureInfo.InvariantCulture : System.Globalization.CultureInfo.CurrentCulture);
    }

    public static string Size(long b) => b switch
    {
        <= 0 => "",
        < 1 << 20 => $"{b / 1024.0:0} {L.T("КБ", "KB")}",
        < 1 << 30 => $"{b / 1048576.0:0.0} {L.T("МБ", "MB")}",
        _ => $"{b / 1073741824.0:0.00} {L.T("ГБ", "GB")}",
    };

    public static string Source(string s) => s switch
    {
        "home" => L.T("Главная", "Home"),
        "subs" => L.T("Подписки", "Subscriptions"),
        "wl" => L.T("Смотреть позже", "Watch later"),
        "liked" => L.T("Понравившиеся", "Liked"),
        "foryou" => L.T("Для вас", "For you"),
        _ when s.StartsWith("search:") => L.T("Поиск: ", "Search: ") + s[7..],
        _ when s.StartsWith("related:") => L.T("Рядом с: ", "Related to: ") + (s.Contains('|') ? s[(s.IndexOf('|') + 1)..] : L.T("видео", "video")),
        _ when s.StartsWith("channel:") => L.T("Канал", "Channel"),
        _ => s,
    };
}
