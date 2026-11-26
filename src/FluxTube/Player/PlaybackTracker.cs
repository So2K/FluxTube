using FluxTube.Models;
using FluxTube.Services;

namespace FluxTube.Player;

/// <summary>Starts playback, remembers position per video, skips SponsorBlock segments.</summary>
public sealed class PlaybackTracker
{
    public static PlaybackTracker Instance { get; } = new();

    private readonly MpvPlayer _p = MpvPlayer.Instance;
    private long _sessionId;
    private double _lastSavedAt;
    private double _lastPos;
    private DateTime _lastSaveTime;
    private List<Extras.Segment> _segments = [];
    private readonly HashSet<int> _skipped = [];
    // every Play gets a number: whatever an older Play left running (link lookups, segment requests) sees a newer
    // number and drops its result, also when the same video was started again
    private int _gen;
    // the player's clock belongs to this session only after its file has loaded and reported a time of its own;
    // before that it still shows the previous video, and saving it would move this video's resume point
    private bool _fileLoaded, _timeValid;
    private double _startPos;

    public VideoItem? Current { get; private set; }
    public string? CurrentSource { get; private set; }
    public bool IsLocal { get; private set; }
    public event Action? CurrentChanged;
    public event Action<string>? PlaybackUnavailable;
    public event Action? VerificationRequired;

    private long _clickAt, _clickAt2;
    private bool _frameLogged = true;
    private double Elapsed() => System.Diagnostics.Stopwatch.GetElapsedTime(_clickAt != 0 ? _clickAt : _clickAt2).TotalMilliseconds;

    private PlaybackTracker()
    {
        _p.TimeChanged += OnTime;
        _p.EndOfFile += _ =>
        {
            Save(force: true);
            // Detect premature stream truncation (e.g. expired URL cutting off mid-video) and recover cleanly
            if (!IsLocal && _timeValid && _p.Duration > 20 && _p.Duration - _p.TimePos > 4.0)
                OnLoadFailed("stream truncated early");
        };
        _p.FileLoaded += () =>
        {
            if (Current == null || _p.CurrentVideoId != Current.VideoId) return;
            if (_clickAt != 0) Paths.Log($"ttff {Current.VideoId}: first frame data {Elapsed():0} ms after click");
            Retrying = false;
            _fileLoaded = true;
            if (_clickAt != 0) _frameLogged = false;
            _clickAt2 = _clickAt != 0 ? _clickAt : _clickAt2; _clickAt = 0;
        };
        _p.LoadFailed += OnLoadFailed;
        _p.BufferingChanged += OnBufferingChanged;
        _p.PlaybackStarted += () =>
        {
            if (Current == null || _p.CurrentVideoId != Current.VideoId) return;
            if (_frameLogged || _clickAt2 == 0) return;
            _frameLogged = true;
            Paths.Log($"frame {Current.VideoId}: picture on screen {System.Diagnostics.Stopwatch.GetElapsedTime(_clickAt2).TotalMilliseconds:0} ms after click");
        };
    }

    /// <summary>The last failure is being retried, so the shell leaves the message to us.</summary>
    public bool Retrying { get; private set; }
    private string? _retried;
    private long _retriedAt;
    private int _retryCount;
    private int _bufGen;
    private string _lastPlayVia = "";

    /// <summary>Watchdog: if mpv gets stuck in paused-for-cache for >10s while playing, seamlessly refresh links and resume at the exact second.</summary>
    private void OnBufferingChanged(bool buffering)
    {
        var bg = Interlocked.Increment(ref _bufGen);
        if (!buffering || IsLocal || Current == null || _p.Paused) return;
        var gen = _gen;
        var id = Current.VideoId;
        var posAtStart = _timeValid ? _p.TimePos : _startPos;
        _ = Task.Run(async () =>
        {
            await Task.Delay(10_000);
            UiThread.Post(() =>
            {
                if (bg != Volatile.Read(ref _bufGen) || gen != _gen || Current?.VideoId != id || IsLocal) return;
                if (!_p.Buffering || _p.Paused) return;
                if (Math.Abs(_p.TimePos - posAtStart) > 0.35) return; // making progress
                Paths.Log($"buffer stall {id} at {_p.TimePos:0.0}s: forcing fresh stream recovery");
                OnLoadFailed("prolonged buffer stall");
            });
        });
    }

    /// <summary>
    /// The player could not make sense of what we fed it (a half-written cache file, a link that died mid-stream, or a prolonged stall).
    /// Forget everything known about this video and start it again from fresh links at the exact playback position.
    /// </summary>
    private void OnLoadFailed(string msg)
    {
        var v = Current;
        if (v == null || IsLocal) return;
        var id = v.VideoId;
        Paths.Log($"load failed {id}: {msg}");
        var now = Environment.TickCount64;
        bool escalateToResolver = _lastPlayVia == "quick" || _retryCount >= 1;
        if (_retried == id)
        {
            // Allow immediate escalation from QuickStream -> full yt-dlp Resolver; throttle later repeats cleanly.
            long minGap = (_retryCount == 1 && _lastPlayVia == "quick") ? 1_200 : 12_000;
            if (now - _retriedAt < minGap || _retryCount >= 3) { Retrying = false; return; }
            _retryCount++;
        }
        else
        {
            _retried = id;
            _retryCount = 1;
        }
        _retriedAt = now;
        Retrying = true;
        var src = CurrentSource ?? "home";
        var resumeAt = _timeValid && _p.TimePos > 0.5 ? _p.TimePos : (_lastPos > 0.5 ? _lastPos : (double?)null);
        var gen = _gen;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { MediaCache.Drop(id); } catch (Exception e) { Paths.Log("drop cache: " + e.Message); }
            QuickStream.Forget(id);
            Resolver.Instance.Forget(id);
            StreamProxy.Instance.Forget(id);
            UiThread.Post(() =>
            {
                if (gen != _gen) return;
                Paths.Log($"retrying {id} with fresh links at {resumeAt ?? 0:0.0}s (attempt {_retryCount}{(escalateToResolver ? ", full yt-dlp" : "")})");
                Play(v, src, resumeAt, bypassQuickStream: escalateToResolver);
            });
        });
    }

    public void Play(VideoItem v, string source, double? startOverride = null, bool startPaused = false, bool bypassQuickStream = false)
    {
        Flush();
        var gen = ++_gen;
        Interlocked.Increment(ref _losslessGen);
        _losslessAttemptedId = null;
        Retrying = false;
        _fileLoaded = _timeValid = false;
        Current = v;
        CurrentSource = source;
        _segments = [];
        _skipped.Clear();
        var start = startOverride ?? (v.IsShort ? 0 : Db.ResumePosition(v.VideoId) ?? 0); // a short always plays from its first frame
        _startPos = _lastPos = _lastSavedAt = start;
        _sessionId = Db.StartWatch(v, source);
        // the new history row carries the resume point from the start: a session that never gets to play keeps it
        if (start > 0) Db.UpdateWatch(_sessionId, null, null, start, 0, 0);
        _lastSaveTime = DateTime.UtcNow;

        if (Downloader.Instance.IsDownloaded(v.VideoId, out var local))
        {
            IsLocal = true;
            IsPreview = false;
            MediaCache.Pinned = null;
            _p.Load(local!.File!, v.VideoId, start, startPaused);
        }
        else
        {
            IsLocal = false;
            _clickAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var id = v.VideoId;
            if (!v.IsShort && !v.IsLive && (AppSettings.Current.LosslessAutoUpgrade || AppSettings.Current.AudioOnlyMode))
                _ = LosslessResolver.ResolveAsync(id, v.Title, v.Channel, ParseDuration(v.Duration));
            var quality = AppSettings.Current.PlayerQuality;
            MediaCache.Pinned = id;
            IsPreview = false;
            var ready = bypassQuickStream ? null : Resolver.Instance.TryGet(id);
            var cached = (ready == null && !bypassQuickStream) ? MediaCache.Find(id, quality) : null;
            if (ready != null)
            {
                // 1. the link was resolved ahead of the click (hover, visible card, related list)
                _lastPlayVia = "resolver";
                LoadFull(ready, v.Title, start, startPaused);
                Paths.Log($"play {id}: prefetched");
            }
            else if (cached != null)
            {
                // 2. played before: start from disk right now; a fresh link is only needed for parts not on disk
                _lastPlayVia = "cache";
                var proxy = StreamProxy.Instance;
                var s = new StreamInfo(id, proxy.RegisterCached(cached.Video), cached.Audio != null ? proxy.RegisterCached(cached.Audio) : null,
                    new Dictionary<string, string>(), v.Title, 0, DateTime.UtcNow) { Vitag = cached.Video.Itag, Aitag = cached.Audio?.Itag ?? 0 };
                LoadFull(s, v.Title, start, startPaused);
                Paths.Log($"play {id}: from disk cache{(cached.Complete ? " (complete, offline)" : "")}");
                if (!cached.Complete)
                    _ = Task.Run(async () =>
                    {
                        // a link for the same formats fills the gaps: one direct request, well under a second
                        StreamInfo? link = null;
                        try { link = await QuickStream.GetExactAsync(id, s.Vitag, s.Aitag); } catch { }
                        if (link != null) { Paths.Log($"play {id}: link for the uncached parts ready after {Elapsed():0} ms"); return; }
                        // those exact formats are not offered any more: take what is (direct request first, yt-dlp last)
                        // and carry on from the same position
                        StreamInfo? fresh = null;
                        try { fresh = await QuickStream.GetAsync(id); } catch { }
                        if (fresh == null) try { fresh = await Resolver.Instance.GetAsync(id); } catch { }
                        Paths.Log($"play {id}: cached formats are gone, {(fresh != null ? "switching to current ones" : "no link available")} after {Elapsed():0} ms");
                        if (fresh == null)
                        {
                            UiThread.Post(() => { if (gen == _gen) _p.ShowText(L.T("Нет связи с YouTube: доступна только сохранённая часть видео", "No connection to YouTube: only cached portion is available"), 5000); });
                            return;
                        }
                        UiThread.Post(() =>
                        {
                            if (gen != _gen) return;
                            LoadFull(fresh, v.Title, _timeValid ? _p.TimePos : _startPos, _p.Paused);
                        });
                    });
            }
            else
            {
                // 3. never opened: one direct player request gives working links (see QuickStream); yt-dlp only if that fails
                _p.Command("stop");
                if (startPaused) _p.SetPause(true);
                _ = Task.Run(async () =>
                {
                    StreamInfo? q = null;
                    if (!bypassQuickStream && AppSettings.Current.QuickStart)
                        try { q = await QuickStream.GetAsync(id); } catch { }
                    if (q != null)
                    {
                        UiThread.Post(() =>
                        {
                            if (gen != _gen) return;
                            _lastPlayVia = "quick";
                            IsPreview = q.Preview;
                            LoadFull(q, v.Title, start, startPaused);
                            Paths.Log($"play {id}: started after {Elapsed():0} ms ({(q.Preview ? "beginning only, proper link follows" : "direct link")})");
                        });
                        if (!q.Preview) return;
                        // only the beginning is served by that link: get the proper one for the same formats, the proxy carries on with it
                        StreamInfo? s = null;
                        try { s = await Resolver.Instance.GetExactAsync(id, q.Vitag, q.Aitag); } catch { }
                        var same = s != null;
                        if (!same) try { s = await Resolver.Instance.GetAsync(id); } catch { }
                        UiThread.Post(() =>
                        {
                            if (gen != _gen) return;
                            IsPreview = false;
                            if (same) Paths.Log($"play {id}: proper link after {Elapsed():0} ms, same formats, nothing to switch");
                            else if (s != null)
                            {
                                LoadFull(s, v.Title, _timeValid ? _p.TimePos : _startPos, _p.Paused);
                                Paths.Log($"play {id}: switched formats after {Elapsed():0} ms");
                            }
                            else _p.ShowText(L.T("Полная ссылка не получена: видео может остановиться. Обновите страницу (F5).", "Full stream link unavailable: playback may stop. Press F5 to refresh."), 6000);
                            CurrentChanged?.Invoke();
                        });
                    }
                    else
                    {
                        StreamInfo? s = null;
                        try { s = await Resolver.Instance.GetAsync(id); } catch { }
                        UiThread.Post(() =>
                        {
                            if (gen != _gen) return;
                            _lastPlayVia = "resolver";
                            if (s != null) LoadFull(s, v.Title, start, startPaused);
                            else
                            {
                                var error = Resolver.Instance.FailureFor(id);
                                var needsLogin = error?.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) == true
                                    || error?.Contains("not a bot", StringComparison.OrdinalIgnoreCase) == true;
                                PlaybackUnavailable?.Invoke(needsLogin
                                    ? L.T("YouTube запросил проверку. Пройдите её в открывшемся окне — FluxTube сохранит вход автоматически.",
                                          "YouTube requested verification. Complete it in the sign-in window — FluxTube will save your session automatically.")
                                    : L.T("Не удалось получить видеопоток YouTube. Проверьте подключение и попробуйте ещё раз.",
                                          "Could not load the YouTube video stream. Check your connection and try again."));
                                if (needsLogin) VerificationRequired?.Invoke();
                            }
                            Paths.Log($"play {id}: {(s != null ? "yt-dlp link" : "no playable link")} after {Elapsed():0} ms (no quick start)");
                        });
                    }
                });
            }
            Resolver.Instance.MarkWatched(v.VideoId);
        }
        if (start > 0 && startOverride == null) _p.ShowText(L.T($"Продолжение с {Fmt.Time(start)}", $"Resuming at {Fmt.Time(start)}"), 2000);
        CurrentChanged?.Invoke();

        if (AppSettings.Current.SponsorBlock || (source == "shorts" && ShortsAdBlocker.Enabled))
        {
            var id = v.VideoId;
            var isShort = source == "shorts";
            _ = Task.Run(async () =>
            {
                var segs = isShort
                    ? await Extras.ShortsSponsorSegmentsAsync(id)
                    : await Extras.SponsorSegmentsAsync(id);
                UiThread.Post(() =>
                {
                    if (gen != _gen) return;
                    _segments = segs;
                    CurrentChanged?.Invoke();
                });
            });
        }
    }

    /// <summary>True while the quick 360p stand-in is on screen and full quality is still on its way.</summary>
    public bool IsPreview { get; private set; }
    public IReadOnlyList<Extras.Segment> Segments => _segments;
    private string? _countedPlay;
    private string? _losslessAttemptedId;
    private int _losslessGen;

    private void LoadFull(StreamInfo s, string? title, double start, bool keepPause)
    {
        _p.LoadStream(s, title, start, keepPause);
        if (_countedPlay != s.VideoId)
        {
            _countedPlay = s.VideoId;
            var q = AppSettings.Current.PlayerQuality;
            ThreadPool.QueueUserWorkItem(_ => MediaCache.NotePlay(s.VideoId, q, s.Vitag, s.Aitag));
        }
        bool wantLossless = (AppSettings.Current.LosslessAutoUpgrade || AppSettings.Current.AudioOnlyMode)
            && !IsLocal && Current != null && !Current.IsShort && !Current.IsLive && _losslessAttemptedId != s.VideoId;
        if (wantLossless)
        {
            _losslessAttemptedId = s.VideoId;
            double dur = s.Duration > 0 ? s.Duration : ParseDuration(Current!.Duration);
            StartLosslessUpgrade(Current!, dur, manual: AppSettings.Current.AudioOnlyMode);
        }
    }

    /// <summary>Called when user enables Headphones / Audio-Only mode: immediately switches to attached FLAC or searches for it now.</summary>
    public void EnsureBestAudioForMusicMode()
    {
        if (Current == null || IsLocal || Current.IsShort || Current.IsLive) return;
        if (_p.IsLosslessAudio) return;
        if (_p.HasLosslessTrack)
        {
            _p.ToggleLosslessAB();
            CurrentChanged?.Invoke();
            return;
        }
        if (_p.LosslessSearching) return;
        _losslessAttemptedId = Current.VideoId;
        double dur = _p.Duration > 0 ? _p.Duration : ParseDuration(Current.Duration);
        StartLosslessUpgrade(Current, dur, manual: true);
    }

    private static double ParseDuration(string? d)
    {
        if (string.IsNullOrWhiteSpace(d)) return 0;
        var parts = d.Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!int.TryParse(p.Trim(), out var v) || v < 0) return 0;
            total = total * 60 + v;
        }
        return total;
    }

    /// <summary>
    /// Manual 1-click action from UI badge:
    ///  - if FLAC is already attached -> instant A/B toggle between FLAC and YouTube Opus;
    ///  - otherwise -> searches public/custom lossless sources right now and switches immediately when found.
    /// </summary>
    public string RequestOrToggleLossless()
    {
        if (Current == null || IsLocal) return "unavailable";
        if (_p.HasLosslessTrack)
        {
            bool nowFlac = _p.ToggleLosslessAB();
            CurrentChanged?.Invoke();
            return nowFlac ? "flac" : "youtube";
        }
        if (_p.LosslessSearching) return "searching";
        _losslessAttemptedId = Current.VideoId;
        double dur = _p.Duration > 0 ? _p.Duration : ParseDuration(Current.Duration);
        StartLosslessUpgrade(Current, dur, manual: true);
        return "searching";
    }

    private void StartLosslessUpgrade(VideoItem v, double durationSec, bool manual)
    {
        var playGen = _gen;
        var lGen = Interlocked.Increment(ref _losslessGen);
        var id = v.VideoId;
        var title = v.Title;
        var channel = v.Channel;
        _p.LosslessSearching = true;
        CurrentChanged?.Invoke();

        _ = Task.Run(async () =>
        {
            try
            {
                // 1. Instant hit from local FLAC disk cache (0 ms, works offline when complete; auto-resumes URL when partial!)
                var cachedFlac = MediaCache.FindLossless(id);
                if (cachedFlac != null)
                {
                    var localUrl = StreamProxy.Instance.RegisterCached(cachedFlac);
                    int estKbps = (cachedFlac.Length > 0 && durationSec > 1) ? (int)Math.Round(cachedFlac.Length * 8.0 / durationSec / 1000.0) : 0;
                    UiThread.Post(() =>
                    {
                        if (playGen != _gen || lGen != Volatile.Read(ref _losslessGen) || _p.CurrentVideoId != id) return;
                        _p.AttachLosslessTrack(localUrl, cachedFlac.Complete ? "Cache" : "FLAC", autoSelect: true, expectedBitrateKbps: estKbps);
                        Paths.Log($"lossless {id}: instant attach from disk cache ({estKbps} kbps{(cachedFlac.Complete ? ", complete" : ", partial -> resuming")})");
                        CurrentChanged?.Invoke();
                    });
                    if (cachedFlac.Complete) return;

                    // Partial FLAC cache: immediately supply upstream URL to the SAME StreamProxy entry so playback never truncates!
                    var resumeMatch = await LosslessResolver.ResolveAsync(id, title, channel, durationSec, manualRequest: true).ConfigureAwait(false);
                    if (resumeMatch != null)
                    {
                        var resumedUrl = StreamProxy.Instance.RegisterLossless(resumeMatch.Url, id, cachedFlac.Length, resumeMatch.Mime);
                        StreamProxy.Instance.StartBackgroundDownload(resumedUrl);
                    }
                    return;
                }

                // 2. Auto-resolve from live Monochrome / HiFi-API / custom self-hosted pool
                var match = await LosslessResolver.ResolveAsync(id, title, channel, durationSec, manualRequest: manual).ConfigureAwait(false);
                if (match == null)
                {
                    UiThread.Post(() =>
                    {
                        if (playGen != _gen || lGen != Volatile.Read(ref _losslessGen)) return;
                        _p.LosslessSearching = false;
                        if (manual) _p.ShowText(L.T("FLAC для этого трека не найден", "No Lossless FLAC found for this track"), 2400);
                        CurrentChanged?.Invoke();
                    });
                    return;
                }

                var proxyUrl = StreamProxy.Instance.RegisterLossless(match.Url, id, match.ContentLength, match.Mime, initialHead: match.InitialHead);
                // Silently pre-buffer ~2.2 MB around the current playback timestamp while YouTube keeps playing -> ZERO pause on switch!
                double refDur = match.DurationSec > 1 ? match.DurationSec : durationSec;
                double curPos = _timeValid ? _p.TimePos : _startPos;
                await StreamProxy.Instance.PrefillWindowAsync(proxyUrl, curPos, refDur).ConfigureAwait(false);
                StreamProxy.Instance.StartBackgroundDownload(proxyUrl);

                int matchKbps = (match.ContentLength > 0 && refDur > 1) ? (int)Math.Round(match.ContentLength * 8.0 / refDur / 1000.0) : 0;
                UiThread.Post(() =>
                {
                    if (playGen != _gen || lGen != Volatile.Read(ref _losslessGen) || _p.CurrentVideoId != id) return;
                    bool autoSelect = manual || match.ExactTiming || AppSettings.Current.AudioOnlyMode;
                    _p.AttachLosslessTrack(proxyUrl, match.SourceLabel, autoSelect, timingDiff: !match.ExactTiming, expectedBitrateKbps: matchKbps);
                    if (autoSelect)
                        _p.ShowText($"ULTRA FLAC · {match.SourceLabel}", 2000);
                    CurrentChanged?.Invoke();
                });
            }
            catch (Exception ex)
            {
                Paths.Log($"lossless upgrade {id}: {ex.Message}");
                UiThread.Post(() =>
                {
                    if (playGen == _gen && lGen == Volatile.Read(ref _losslessGen))
                    {
                        _p.LosslessSearching = false;
                        CurrentChanged?.Invoke();
                    }
                });
            }
        });
    }

    public void PlayFile(DownloadItem d)
    {
        var v = new VideoItem { VideoId = d.VideoId ?? d.File ?? d.Url, Title = d.Title, Channel = d.Channel };
        Flush();
        ++_gen;
        Interlocked.Increment(ref _losslessGen);
        _losslessAttemptedId = null;
        Retrying = false;
        _fileLoaded = _timeValid = false;
        Current = v;
        CurrentSource = "downloads";
        IsLocal = true;
        IsPreview = false;
        MediaCache.Pinned = null;
        _segments = [];
        _skipped.Clear();
        var start = d.VideoId != null ? Db.ResumePosition(d.VideoId) ?? 0 : 0;
        _startPos = _lastPos = _lastSavedAt = start;
        _sessionId = d.VideoId != null ? Db.StartWatch(v, "downloads") : 0;
        if (_sessionId != 0 && start > 0) Db.UpdateWatch(_sessionId, null, null, start, 0, 0);
        _lastSaveTime = DateTime.UtcNow;
        _p.Load(d.File!, d.VideoId, start);
        CurrentChanged?.Invoke();
    }

    public void UpdateMeta(string? title, string? channel)
    {
        if (_sessionId == 0) return;
        Db.UpdateWatch(_sessionId, title, channel, _timeValid ? _p.TimePos : _startPos, _timeValid ? _p.Duration : 0, 0);
    }

    private void OnTime(double t)
    {
        if (!_fileLoaded) return; // still the previous video's clock
        _timeValid = true;
        if (_segments.Count > 0)
        {
            for (int i = 0; i < _segments.Count; i++)
            {
                var s = _segments[i];
                if (s.ActionType != "skip") continue;
                if (t >= s.Start && t < s.End - 0.5 && !_skipped.Contains(i))
                {
                    _skipped.Add(i);
                    Save(force: true);  // what was watched up to the segment counts, the jump over it does not
                    var dur = _p.Duration;
                    var dest = CurrentSource == "shorts" && dur > 2 && s.End >= dur - 1.2 ? 0.0 : s.End;
                    _lastPos = dest;
                    _p.SeekAbsolute(dest);
                    _p.ShowText(L.T("Пропущено: ", "Skipped: ") + Extras.CategoryName(s.Category), 2200);
                    break;
                }
            }
        }
        if (Math.Abs(t - _lastSavedAt) >= 5) Save(force: false);
    }

    private void Save(bool force)
    {
        if (_sessionId == 0 || !_timeValid) return;
        var pos = _p.TimePos;
        var now = DateTime.UtcNow;
        var delta = Math.Max(0, Math.Min(pos - _lastPos, (now - _lastSaveTime).TotalSeconds * 4 + 1));
        if (!force && Math.Abs(pos - _lastSavedAt) < 5) return;
        Db.UpdateWatch(_sessionId, null, null, pos, _p.Duration, delta);
        _lastSavedAt = pos;
        _lastPos = pos;
        _lastSaveTime = now;
    }

    public void Flush()
    {
        Save(force: true);
        _sessionId = 0;
    }

    public void Stop()
    {
        Flush();
        ++_gen;
        Interlocked.Increment(ref _losslessGen);
        _losslessAttemptedId = null;
        Retrying = false;
        _fileLoaded = _timeValid = false;
        _p.Stop();
        MediaCache.Pinned = null;
        _countedPlay = null;
        Current = null;
        CurrentChanged?.Invoke();
    }
}
