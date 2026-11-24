using FluxTube.Models;
using Microsoft.Data.Sqlite;

namespace FluxTube.Services;

/// <summary>Local, detailed memory of everything: watch sessions, every card that appeared on screen, downloads.</summary>
public static class Db
{
    private static readonly SqliteConnection Conn;
    private static readonly object Lock = new();

    static Db()
    {
        Conn = new SqliteConnection($"Data Source={Paths.Database}");
        Conn.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
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
    }

    /// <summary>Opens the DB and touches the hot indexes off the UI thread at startup.</summary>
    public static void Warm() => Scalar("SELECT count(*) FROM watch");

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

    public static long StartWatch(VideoItem v, string source)
    {
        lock (Lock)
        {
            Exec("INSERT INTO watch(video_id,title,channel,channel_id,started_at,last_at,source) VALUES($v,$t,$c,$ci,$n,$n,$s)",
                ("$v", v.VideoId), ("$t", v.Title), ("$c", v.Channel), ("$ci", v.ChannelId), ("$n", Now), ("$s", source));
            return (long)Scalar("SELECT last_insert_rowid()")!;
        }
    }

    public static void UpdateWatch(long id, string? title, string? channel, double position, double duration, double watchedDelta)
        => Exec("""
            UPDATE watch SET position_s=$p, duration_s=CASE WHEN $d>0 THEN $d ELSE duration_s END,
              watched_s=watched_s+$w, last_at=$n,
              title=COALESCE(NULLIF($t,''),title), channel=COALESCE(NULLIF($c,''),channel)
            WHERE id=$id
            """, ("$p", position), ("$d", duration), ("$w", watchedDelta), ("$n", Now), ("$t", title), ("$c", channel), ("$id", id));

    /// <summary>Last saved position for resume; null when finished or never watched.</summary>
    public static double? ResumePosition(string videoId)
    {
        var rows = Query("SELECT position_s, duration_s FROM watch WHERE video_id=$v ORDER BY last_at DESC LIMIT 1",
            r => (r.GetDouble(0), r.GetDouble(1)), ("$v", videoId));
        if (rows.Count == 0) return null;
        var (p, d) = rows[0];
        if (p < 10 || (d > 0 && p > d - 15)) return null;
        return p;
    }

    public static Dictionary<string, double> ProgressFor(IEnumerable<string> ids)
    {
        var list = ids.Distinct().ToList();
        var result = new Dictionary<string, double>();
        if (list.Count == 0) return result;
        var ps = string.Join(',', list.Select((_, i) => "$p" + i));
        var args = list.Select((v, i) => ("$p" + i, (object?)v)).ToArray();
        foreach (var (id, p, d) in Query($"SELECT video_id, position_s, duration_s FROM watch WHERE video_id IN ({ps}) ORDER BY last_at",
                     r => (r.GetString(0), r.GetDouble(1), r.GetDouble(2)), args))
            if (d > 0) result[id] = Math.Clamp(p / d, 0, 1);
        return result;
    }

    public sealed record WatchRow(long Id, string VideoId, string Title, string Channel, string? ChannelId,
        double Duration, double Position, double Watched, DateTime Started, DateTime Last, string Source)
    {
        public VideoItem ToVideo() => new()
        {
            VideoId = VideoId, Title = Title, Channel = Channel, ChannelId = ChannelId,
            Duration = Fmt.Time(Duration), Meta = $"{Fmt.When(Last)} · {Fmt.Time(Position)} из {Fmt.Time(Duration)}",
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

    public static void DeleteWatch(long id) => Exec("DELETE FROM watch WHERE id=$id", ("$id", id));
    public static void ClearHistory() => Exec("DELETE FROM watch");

    public static List<string> RecentWatchedIds(int n)
        => Query("SELECT video_id FROM watch GROUP BY video_id ORDER BY MAX(last_at) DESC LIMIT $n", r => r.GetString(0), ("$n", n));

    // ---------------- seen (impressions) ----------------

    private static readonly Dictionary<string, long> SeenRecently = new();

    /// <summary>Records that a card was actually shown on screen. Same video+source within 30 min counts once.</summary>
    public static void Seen(VideoItem v, string source)
    {
        var key = v.VideoId + "|" + source;
        var now = Now;
        lock (SeenRecently)
        {
            if (SeenRecently.TryGetValue(key, out var t) && now - t < 1800) return;
            if (SeenRecently.Count > 4000) SeenRecently.Clear(); // a long session: start over rather than grow
            SeenRecently[key] = now;
        }
        Exec("INSERT INTO seen(video_id,title,channel,channel_id,duration,meta,avatar,source,seen_at) VALUES($v,$t,$c,$ci,$d,$m,$a,$s,$n)",
            ("$v", v.VideoId), ("$t", v.Title), ("$c", v.Channel), ("$ci", v.ChannelId), ("$d", v.Duration), ("$m", v.Meta),
            ("$a", v.AvatarUrl), ("$s", source), ("$n", now));
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
        => Exec("""
            INSERT INTO cache_stream(video_id,itag,length,mime,ranges,bytes) VALUES($v,$i,$l,$m,$r,$b)
            ON CONFLICT(video_id,itag) DO UPDATE SET length=$l, mime=$m, ranges=$r, bytes=$b
            """, ("$v", videoId), ("$i", itag), ("$l", length), ("$m", mime), ("$r", ranges), ("$b", bytes));

    public static (string Quality, int Vitag, int Aitag, int Plays)? CacheVideo(string videoId)
    {
        var r = Query("SELECT quality, vitag, aitag, plays FROM cache_video WHERE video_id=$v",
            x => (Str(x, 0), x.GetInt32(1), x.GetInt32(2), x.GetInt32(3)), ("$v", videoId));
        return r.Count > 0 ? r[0] : null;
    }

    /// <summary>Creates or refreshes the entry. A different quality or format replaces the old one (its files are stale).</summary>
    public static void CacheTouch(string videoId, string quality, int vitag, int aitag, bool addPlay)
        => Exec("""
            INSERT INTO cache_video(video_id,quality,vitag,aitag,plays,last_play,created) VALUES($v,$q,$vi,$ai,$p,$n,$n)
            ON CONFLICT(video_id) DO UPDATE SET quality=$q, vitag=$vi, aitag=$ai, plays=plays+$p, last_play=$n
            """, ("$v", videoId), ("$q", quality), ("$vi", vitag), ("$ai", aitag), ("$p", addPlay ? 1 : 0), ("$n", Now));

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
    public static string Time(double s)
    {
        if (s <= 0 || double.IsNaN(s)) return "";
        var t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string When(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "только что";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} мин назад";
        if (t.Date == DateTime.Today) return $"сегодня {t:HH:mm}";
        if (t.Date == DateTime.Today.AddDays(-1)) return $"вчера {t:HH:mm}";
        return t.ToString("d MMM HH:mm");
    }

    public static string Day(DateTime t)
    {
        if (t.Date == DateTime.Today) return "Сегодня";
        if (t.Date == DateTime.Today.AddDays(-1)) return "Вчера";
        return t.ToString("dddd, d MMMM");
    }

    public static string Size(long b) => b switch
    {
        <= 0 => "",
        < 1 << 20 => $"{b / 1024.0:0} КБ",
        < 1 << 30 => $"{b / 1048576.0:0.0} МБ",
        _ => $"{b / 1073741824.0:0.00} ГБ",
    };

    public static string Source(string s) => s switch
    {
        "home" => "Главная",
        "subs" => "Подписки",
        "wl" => "Смотреть позже",
        "liked" => "Понравившиеся",
        "foryou" => "Для вас",
        _ when s.StartsWith("search:") => "Поиск: " + s[7..],
        _ when s.StartsWith("related:") => "Рядом с: " + (s.Contains('|') ? s[(s.IndexOf('|') + 1)..] : "видео"),
        _ when s.StartsWith("channel:") => "Канал",
        _ => s,
    };
}
