using System.Globalization;
using FluxTube.Models;
using FluxTube.Pages;
using FluxTube.Player;
using FluxTube.Services;

namespace FluxTube;

/// <summary>
/// Pro 3-Tab Apple Liquid Glass Analytics Studio (vidIQ + TubeBuddy + SocialBlade level):
/// - Tab 0 ("Обзор видео"): KPI + Velocity (VPH/Day), Approval Donut Ring, Interactive 100-point YouTube "Most Replayed" Retention Heatmap (click to seek!), SponsorBlock segmented breakdown, Revenue estimate (clearly labeled), Personal watch history.
/// - Tab 1 ("SEO и Тех. аудит"): Animated SEO Score Gauge (0-100), 7-point Audit Checklist (incl. real YouTube adPlacements monetization check), Hidden YouTube SEO Tags with 1-click copy, full Codecs/HDR/FPS/Audio Tracks/Subtitles matrix &amp; live Stats for Nerds.
/// - Tab 2 ("Канал Pro"): Channel Grade Badge, All-time views &amp; lifetime avg, 20-video staggered Bar Chart + Average/Median/Trend curves (hover + click to open), Stability/Consistency gauge, Upload Cadence, Monthly Views &amp; RPM range (explicitly marked as non-100% estimate), Hidden Channel Keywords, and Your Favorite Video on this channel.
/// Every metric explicitly displays whether it is 100% official YouTube/local data, crowdsourced (RYD/SB), or an algorithmic estimate (not 100%).
/// </summary>
public static class AnalyticsPanel
{
    public static bool Open { get; private set; }
    public static R PanelRect { get; private set; }

    private static int _tab; // 0 = Video Overview, 1 = SEO & Tech Audit, 2 = Channel Pro
    private static bool _hasVideo;
    private static long _openedAt;
    private static long _chartAnimAt;
    private static int _reqGen;
    private static readonly int _scrollId = UI.Id("analytics:scroll");

    private static bool _loadingVideo, _loadingChannel;
    private static VideoAnalyticsData? _video;
    private static ChannelAnalyticsData? _channel;
    private static string? _videoId, _channelId;

    public static void Close()
    {
        if (!Open) return;
        Open = false;
        App.Invalidate();
    }

    public static void OpenVideo(string videoId, string title, string channel, string? channelId, string? channelAvatar,
        string? knownLikes, string? knownViews, string? knownDate, string? knownSubs)
    {
        CloseOtherModals();
        Open = true;
        _hasVideo = true;
        _tab = 0;
        _openedAt = _chartAnimAt = UI.Now;
        _videoId = videoId;
        _channelId = channelId;
        _loadingVideo = true;
        _loadingChannel = !string.IsNullOrEmpty(channelId);
        UI.ScrollReset(_scrollId);

        var (plays, watchedSec) = Db.VideoWatchStats(videoId);
        _video = new VideoAnalyticsData
        {
            VideoId = videoId,
            Title = title,
            Channel = channel,
            ChannelId = channelId,
            ChannelAvatar = channelAvatar,
            Views = InnerTube.ParseCompactCount(knownViews),
            Likes = InnerTube.ParseCompactCount(knownLikes),
            PublishDate = knownDate ?? "",
            LocalPlays = plays,
            LocalWatchedSec = watchedSec,
        };
        _channel = !string.IsNullOrEmpty(channelId)
            ? new ChannelAnalyticsData { ChannelId = channelId, Title = channel, Avatar = channelAvatar, SubscribersText = knownSubs ?? "", SubscribersNum = InnerTube.ParseCompactCount(knownSubs) }
            : null;

        int gen = ++_reqGen;
        LoadVideoAsync(videoId, gen);
        if (!string.IsNullOrEmpty(channelId)) LoadChannelAsync(channelId, channel, gen);
        UI.Animate();
        App.Invalidate();
    }

    public static void OpenChannel(string channelId, string title, string? avatar, string? subs, string? videoCount, string? handle)
    {
        CloseOtherModals();
        Open = true;
        _hasVideo = false;
        _tab = 2;
        _openedAt = _chartAnimAt = UI.Now;
        _videoId = null;
        _channelId = channelId;
        _video = null;
        _loadingVideo = false;
        _loadingChannel = true;
        UI.ScrollReset(_scrollId);

        var (uVids, sess, wSec, favTitle, favSec) = Db.ChannelWatchStats(channelId, title);
        _channel = new ChannelAnalyticsData
        {
            ChannelId = channelId,
            Title = title,
            Avatar = avatar,
            Handle = handle ?? "",
            SubscribersText = subs ?? "",
            SubscribersNum = InnerTube.ParseCompactCount(subs),
            VideoCountText = videoCount ?? "",
            VideoCountNum = InnerTube.ParseCompactCount(videoCount),
            LocalUniqueVideos = uVids,
            LocalSessions = sess,
            LocalWatchedSec = wSec,
            LocalFavVideoTitle = favTitle,
            LocalFavVideoSec = favSec,
        };

        int gen = ++_reqGen;
        LoadChannelAsync(channelId, title, gen);
        UI.Animate();
        App.Invalidate();
    }

    private static void CloseOtherModals()
    {
        Menus.Close();
        AccountMenu.Close();
        SharePanel.Close();
        ThemeCodeEditor.Close();
        AudioStudioPanel.Close();
        PreviewPlayer.HideNow();
    }

    private static void LoadVideoAsync(string videoId, int gen)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var metaTask = InnerTube.Instance.VideoAnalyticsAsync(videoId);
                var votesTask = Extras.DislikesAsync(videoId);
                var segsTask = Extras.SponsorSegmentsAsync(videoId);
                await Task.WhenAll(metaTask, votesTask, segsTask).ConfigureAwait(false);
                var m = await metaTask;
                var votes = await votesTask;
                var segs = await segsTask;
                var (plays, watchedSec) = Db.VideoWatchStats(videoId);

                if (votes != null)
                {
                    if (votes.Likes > 0) m.Likes = votes.Likes;
                    if (votes.Dislikes > 0) m.Dislikes = votes.Dislikes;
                    long tot = m.Likes + m.Dislikes;
                    if (tot > 0) m.Rating = 1.0 + 4.0 * m.Likes / tot;
                }
                foreach (var sg in segs)
                {
                    double len = Math.Max(0, sg.End - sg.Start);
                    if (sg.Category == "sponsor") m.SponsorSec += len;
                    else if (sg.Category == "selfpromo") m.SelfPromoSec += len;
                    else m.OtherSkipSec += len;
                }
                m.SegmentCount = segs.Count;
                m.LocalPlays = plays;
                m.LocalWatchedSec = watchedSec;

                App.Post(() =>
                {
                    if (gen != _reqGen) return;
                    if (_video != null)
                    {
                        if (string.IsNullOrEmpty(m.Title)) m.Title = _video.Title;
                        if (string.IsNullOrEmpty(m.Channel)) m.Channel = _video.Channel;
                        if (string.IsNullOrEmpty(m.ChannelId)) m.ChannelId = _video.ChannelId;
                        m.ChannelAvatar ??= _video.ChannelAvatar;
                        if (m.Views <= 0) m.Views = _video.Views;
                        if (m.Likes <= 0) m.Likes = _video.Likes;
                        if (string.IsNullOrEmpty(m.PublishDate)) m.PublishDate = _video.PublishDate;
                    }
                    _video = m;
                    _loadingVideo = false;
                    if (_tab is 0 or 1) _chartAnimAt = UI.Now;
                    if (string.IsNullOrEmpty(_channelId) && !string.IsNullOrEmpty(m.ChannelId))
                    {
                        _channelId = m.ChannelId;
                        _loadingChannel = true;
                        LoadChannelAsync(m.ChannelId, m.Channel, gen);
                    }
                    UI.Animate();
                    App.Invalidate();
                });
            }
            catch (Exception e)
            {
                Paths.Log($"analytics video {videoId}: {e.Message}");
                App.Post(() => { if (gen == _reqGen) { _loadingVideo = false; App.Invalidate(); } });
            }
        });
    }

    private static void LoadChannelAsync(string channelId, string? fallbackName, int gen)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var c = await InnerTube.Instance.ChannelAnalyticsAsync(channelId).ConfigureAwait(false);
                var (uVids, sess, wSec, favTitle, favSec) = Db.ChannelWatchStats(channelId, string.IsNullOrEmpty(c.Title) ? fallbackName : c.Title);
                c.LocalUniqueVideos = uVids;
                c.LocalSessions = sess;
                c.LocalWatchedSec = wSec;
                c.LocalFavVideoTitle = favTitle;
                c.LocalFavVideoSec = favSec;

                App.Post(() =>
                {
                    if (gen != _reqGen) return;
                    if (_channel != null)
                    {
                        if (string.IsNullOrEmpty(c.Title)) c.Title = _channel.Title;
                        if (string.IsNullOrEmpty(c.Handle)) c.Handle = _channel.Handle;
                        c.Avatar ??= _channel.Avatar;
                        if (string.IsNullOrEmpty(c.SubscribersText)) c.SubscribersText = _channel.SubscribersText;
                        if (c.SubscribersNum <= 0) c.SubscribersNum = _channel.SubscribersNum;
                        if (string.IsNullOrEmpty(c.VideoCountText)) c.VideoCountText = _channel.VideoCountText;
                        if (c.VideoCountNum <= 0) c.VideoCountNum = _channel.VideoCountNum;
                    }
                    _channel = c;
                    _loadingChannel = false;
                    if (_tab == 2) _chartAnimAt = UI.Now;
                    UI.Animate();
                    App.Invalidate();
                });
            }
            catch (Exception e)
            {
                Paths.Log($"analytics channel {channelId}: {e.Message}");
                App.Post(() => { if (gen == _reqGen) { _loadingChannel = false; App.Invalidate(); } });
            }
        });
    }

    public static void Draw()
    {
        if (!Open) { PanelRect = default; return; }
        float openT = Math.Clamp((UI.Now - _openedAt) / 240f, 0f, 1f);
        float openEase = 1f - MathF.Pow(1f - openT, 4f);
        if (openT < 1f) UI.Animate();

        float chartT = Math.Clamp((UI.Now - _chartAnimAt) / 760f, 0f, 1f);
        float chartEase = 1f - MathF.Pow(1f - chartT, 4f);
        if (chartT < 1f) UI.Animate();

        var full = new R(0, 0, Gfx.W, Gfx.H);
        Gfx.Fill(full, Gfx.Fade(0xFF000000, 0.58f * openEase));

        float pw = MathF.Min(Gfx.W - 44f, 960f);
        float ph = MathF.Min(Gfx.H - 48f, 740f);
        float px = Gfx.Snap((Gfx.W - pw) * 0.5f);
        float py = Gfx.Snap((Gfx.H - ph) * 0.5f + (1f - openEase) * 20f);
        var panel = new R(px, py, pw, ph);
        PanelRect = panel;

        if (UI.Pressed && !panel.Contains(UI.MX, UI.MY))
        {
            Close();
            return;
        }

        UI.Modal = panel;
        float rad = Gfx.Corner(18);
        Gfx.Fill(panel.Inset(-5), Gfx.Fade(0xFF000000, 0.45f * openEase), rad + 5);
        Gfx.FillFrosted(panel, Th.Menu, rad, 0.95f * openEase, border: true);
        Gfx.Stroke(panel, Gfx.Fade(0xFFFFFFFF, 0.14f * openEase), rad, 1f);

        // Header
        const float hdrH = 58f;
        Gfx.Draw(L.T("Аналитика PRO", "Analytics PRO"), panel.X + 20f, panel.Y + 17f, 18f, Gfx.Fade(Th.Text, openEase), 700);

        // Segmented tab bar
        if (_hasVideo)
        {
            bool hasCh = !string.IsNullOrEmpty(_channelId);
            float segW = hasCh ? 366f : 246f;
            float tx = panel.X + pw * 0.5f - segW * 0.5f;
            var segBg = new R(tx, panel.Y + 13f, segW, 34f);
            Gfx.Fill(segBg, Th.A(0.08f), 17f);
            Gfx.Stroke(segBg, Th.Line, 17f, 1f);
            DrawModeTab(0, L.T("Обзор видео", "Video Overview"), new R(segBg.X + 3, segBg.Y + 3, 118, 28));
            DrawModeTab(1, L.T("SEO и Тех.", "SEO & Tech"), new R(segBg.X + 123, segBg.Y + 3, 118, 28));
            if (hasCh) DrawModeTab(2, L.T("Канал Pro", "Channel Pro"), new R(segBg.X + 243, segBg.Y + 3, 120, 28));
        }

        var closeR = new R(panel.Right - 46f, panel.Y + 13f, 32f, 32f);
        if (UI.IconButton(UI.Id("analytics:close"), closeR, Th.Close)) { Close(); return; }
        Gfx.Fill(new R(panel.X + 16, panel.Y + hdrH, panel.W - 32, 1), Th.Line);

        // Legend bar at top explaining accuracy badges clearly
        float legY = panel.Y + hdrH + 7f;
        float lx = panel.X + 20f;
        lx = DrawBadge(lx, legY, L.T("✓ 100% YouTube / локально", "✓ 100% YouTube / local"), 0xFF22C55Eu) + 8f;
        lx = DrawBadge(lx, legY, L.T("≈ Сообщество (RYD / SB)", "≈ Crowdsourced (RYD / SB)"), 0xFFF59E0Bu) + 8f;
        DrawBadge(lx, legY, L.T("⚠ Оценка (не 100% точно)", "⚠ Estimate (not 100% exact)"), 0xFFFB7185u);

        var body = new R(panel.X + 16, panel.Y + hdrH + 34f, panel.W - 32, panel.H - hdrH - 46f);
        float contentH = _tab == 0 ? 860f : _tab == 1 ? 760f : 890f;
        float sc = UI.BeginScroll(_scrollId, body, contentH);
        Gfx.PushClip(body);
        if (_tab == 0) DrawVideoOverviewTab(body, sc, chartEase, openEase);
        else if (_tab == 1) DrawVideoSeoTechTab(body, sc, chartEase, openEase);
        else DrawChannelProTab(body, sc, chartEase, openEase);
        Gfx.PopClip();
        UI.EndScroll();
    }

    private static void DrawModeTab(int idx, string label, R r)
    {
        bool active = _tab == idx;
        bool hov = UI.Hover(r);
        if (active) Gfx.Fill(r, Th.Brand, 14f);
        else if (hov) Gfx.Fill(r, Th.A(0.08f), 14f);
        var tr = Gfx.Text(label, 12.5f, 600);
        Gfx.Draw(tr, r.X + (r.W - tr.W) * 0.5f, r.Y + (r.H - tr.H) * 0.5f, active ? Th.OnBrand : Th.Text);
        if (UI.Click(UI.Id("analytics:tab:" + idx), r) && !active)
        {
            _tab = idx;
            _chartAnimAt = UI.Now;
            UI.ScrollReset(_scrollId);
            UI.Animate();
            App.Invalidate();
        }
    }

    // ==================== TAB 0: VIDEO OVERVIEW PRO ====================
    private static void DrawVideoOverviewTab(R body, float sc, float chartEase, float openEase)
    {
        var v = _video;
        if (v == null) return;
        float x = body.X + 4, w = body.W - 14, y = body.Y + 4 - sc;

        var tr = Gfx.Draw(v.Title, x, y, 16f, Th.Text, 700, w - 95, 2);
        if (_loadingVideo) Gfx.Draw(L.T("Обновление…", "Loading…"), x + w - 90, y + 2, 12f, Th.Sub);
        y += tr.H + 4;

        double ageHours = v.PublishedUtc.HasValue ? Math.Max(0.25, (DateTime.UtcNow - v.PublishedUtc.Value).TotalHours) : 0;
        string ageTxt = ageHours > 0 ? FormatAge(ageHours) : "";
        var subLine = string.Join("  ·  ", new[]
        {
            v.Channel,
            FormatDate(v.PublishDate),
            ageTxt,
            v.Category,
            v.DurationSec > 0 ? Fmt.Time(v.DurationSec) : ""
        }.Where(s => !string.IsNullOrEmpty(s)));
        Gfx.Draw(subLine, x, y, 12.2f, Th.Sub, 400, w, 1);
        y += 26;

        // 1. 4 KPI Cards with clear accuracy markers
        double viewsPerHour = ageHours > 0 && v.Views > 0 ? v.Views / ageHours : 0;
        double viewsPerDay = viewsPerHour * 24.0;
        double erPct = v.Views > 0 ? (v.Likes + v.Dislikes + v.CommentsCount) * 100.0 / v.Views : 0;
        string erTag = erPct >= 7.0 ? L.T("Вирусная · ER%", "Viral · ER%")
            : erPct >= 4.0 ? L.T("Отличная · ER%", "Great · ER%")
            : erPct >= 2.0 ? L.T("Хорошая · ER%", "Good · ER%")
            : L.T("Обычная · ER%", "Normal · ER%");

        float gap = 10f, kw = (w - gap * 3) / 4f, kh = 84f;
        string velSub = viewsPerDay >= 100 ? $"≈ {FormatNum((long)viewsPerDay)}/{L.T("день", "day")} ({FormatNum((long)viewsPerHour)}/{L.T("ч", "h")})" : (v.Views > 0 ? FormatExact(v.Views) : "—");
        DrawKpiCard(new R(x, y, kw, kh), L.T("Просмотры", "Views"), FormatNum((long)(v.Views * chartEase)), velSub, Th.Brand, openEase, "✓ 100%", 0xFF22C55Eu);
        DrawKpiCard(new R(x + (kw + gap), y, kw, kh), L.T("Лайки", "Likes"), FormatNum((long)(v.Likes * chartEase)),
            v.Views > 0 ? $"{v.Likes * 100.0 / v.Views:0.00}% {L.T("от просм.", "of views")}" : "—", 0xFF22C55Eu, openEase, "✓ 100%", 0xFF22C55Eu);
        DrawKpiCard(new R(x + (kw + gap) * 2, y, kw, kh), L.T("Дизлайки (RYD)", "Dislikes (RYD)"), FormatNum((long)(v.Dislikes * chartEase)),
            L.T("≈ экстраполяция RYD", "≈ RYD extrapolation"), 0xFFEF4444u, openEase, "≈ RYD", 0xFFF59E0Bu);
        DrawKpiCard(new R(x + (kw + gap) * 3, y, kw, kh), L.T("Вовлечённость", "Engagement"),
            v.Views > 0 ? $"{erPct * chartEase:0.00}%" : "—",
            v.CommentsCount > 0 ? $"{FormatNum(v.CommentsCount)} {L.T("комм.", "cmts")} · {erTag}" : erTag, 0xFF38BDF8u, openEase, "≈ расчёт", 0xFFF59E0Bu);
        y += kh + 14;

        // 2. YouTube 100-Point "Most Replayed" Retention Heatmap Area Chart (interactive click-to-seek!)
        float hmH = 192f;
        var hmCard = new R(x, y, w, hmH);
        DrawSubCard(hmCard);
        Gfx.Draw(L.T("Тепловая карта интереса зрителей (Most Replayed — клик перематывает плеер)", "Viewer Retention Heatmap (Most Replayed — click to seek)"),
            hmCard.X + 14, hmCard.Y + 12, 13.2f, Th.Text, 600);
        DrawBadge(hmCard.Right - 108, hmCard.Y + 10, "✓ 100% YouTube", 0xFF22C55Eu);

        if (v.Heatmap.Count < 4)
        {
            Gfx.Draw(_loadingVideo ? L.T("Загружаем график удержания YouTube…", "Loading YouTube heatmap…")
                : L.T("YouTube ещё не сформировал тепловую карту Most Replayed для этого видео (обычно появляется после ~50 тыс. просмотров)",
                      "YouTube has not generated a Most Replayed heatmap for this video yet (typically requires ~50K+ views)"),
                hmCard.X + 14, hmCard.Y + 62, 12.5f, Th.Sub, 400, hmCard.W - 28, 2);
        }
        else
        {
            float px0 = hmCard.X + 16, py0 = hmCard.Y + 42, pw0 = hmCard.W - 32, ph0 = hmH - 76;
            float step = pw0 / v.Heatmap.Count;
            int peakIdx = 0;
            for (int i = 1; i < v.Heatmap.Count; i++) if (v.Heatmap[i].Intensity > v.Heatmap[peakIdx].Intensity) peakIdx = i;

            int hovIdx = -1;
            float hovX = 0, hovY = 0;
            for (int i = 0; i < v.Heatmap.Count; i++)
            {
                var pt = v.Heatmap[i];
                float bx = px0 + i * step;
                float hVal = MathF.Max(2f, pt.Intensity * ph0 * chartEase);
                float by = py0 + ph0 - hVal;
                var hitR = new R(bx, py0, MathF.Max(step, 4f), ph0 + 10);
                bool hov = UI.Hover(hitR);
                if (hov) { hovIdx = i; hovX = bx + step * 0.5f; hovY = by; }

                bool isPeak = i == peakIdx;
                uint col = hov ? 0xFFFFFFFFu : isPeak ? 0xFF34D399u : Th.Brand;
                Gfx.Fill(new R(bx, by, MathF.Max(1.5f, step - 1f), hVal), Gfx.Fade(col, hov ? 0.88f : isPeak ? 0.78f : 0.34f), 2f);
                if (i > 0)
                {
                    var prev = v.Heatmap[i - 1];
                    float pY = py0 + ph0 - MathF.Max(2f, prev.Intensity * ph0 * chartEase);
                    Gfx.Line(px0 + (i - 1) * step + step * 0.5f, pY, bx + step * 0.5f, by, Gfx.Fade(Th.Brand, 0.92f), 2.0f);
                }
                if (UI.Click(UI.Id("analytics:hm:" + i), hitR) && MpvPlayer.Instance.CurrentVideoId == v.VideoId)
                {
                    MpvPlayer.Instance.SeekKeyframe(pt.StartSec);
                    Shell.Toast($"{L.T("Перемотка на", "Jumped to")} {Fmt.Time(pt.StartSec)}", Th.Brand, 1600);
                }
            }

            // Peak callout
            var pk = v.Heatmap[peakIdx];
            float pkX = px0 + peakIdx * step + step * 0.5f;
            float pkY = py0 + ph0 - pk.Intensity * ph0 * chartEase;
            Gfx.Circle(pkX, pkY, 4.5f, 0xFF34D399u);
            string pkLbl = $"{L.T("Пик интереса", "Peak moment")}: {Fmt.Time(pk.StartSec)} ({pk.Intensity * 100:0}%)";
            Gfx.Draw(pkLbl, px0, py0 + ph0 + 8, 11.5f, 0xFF34D399u, 600);
            var endLbl = Gfx.Text(v.DurationSec > 0 ? Fmt.Time(v.DurationSec) : "", 11f, 400);
            Gfx.Draw(endLbl, px0 + pw0 - endLbl.W, py0 + ph0 + 8, Th.Sub);

            if (hovIdx >= 0)
            {
                var hp = v.Heatmap[hovIdx];
                string tip = $"{Fmt.Time(hp.StartSec)}  ·  {L.T("Интерес", "Replay")} {hp.Intensity * 100:0}%";
                var trT = Gfx.Text(tip, 11.5f, 600);
                var tt = new R(Math.Clamp(hovX - (trT.W + 18) * 0.5f, px0, px0 + pw0 - trT.W - 18), MathF.Max(py0 - 4, hovY - 30), trT.W + 18, 24);
                Gfx.Fill(tt, Gfx.Fade(0xFF0E1016, 0.94f), 8f);
                Gfx.Stroke(tt, Gfx.Fade(Th.Brand, 0.7f), 8f, 1f);
                Gfx.Draw(trT, tt.X + 9, tt.Y + 4, 0xFFFFFFFF);
            }
        }
        y += hmH + 14;

        // 3. Approval Donut + SponsorBlock Breakdown
        float leftW = MathF.Round(w * 0.46f), rightW = w - leftW - 12f, rowH = 198f;
        var donutCard = new R(x, y, leftW, rowH);
        DrawSubCard(donutCard);
        Gfx.Draw(L.T("Одобрение аудитории", "Audience Approval"), donutCard.X + 14, donutCard.Y + 12, 13.2f, Th.Text, 600);
        DrawBadge(donutCard.Right - 96, donutCard.Y + 10, "≈ RYD (не 100%)", 0xFFF59E0Bu);

        long totalVotes = v.Likes + v.Dislikes;
        float likeFrac = totalVotes > 0 ? (float)((double)v.Likes / totalVotes) : 1f;
        float cx = donutCard.X + 86f, cy = donutCard.Y + 116f;
        DrawDonutRing(cx, cy, 50f, 12f, likeFrac, chartEase, 0xFF22C55Eu, 0xFFEF4444u);
        var pr = Gfx.Text(totalVotes > 0 ? $"{likeFrac * 100f * chartEase:0.0}%" : "—", 17.5f, 700);
        Gfx.Draw(pr, cx - pr.W * 0.5f, cy - pr.H * 0.5f - 4, Th.Text);
        var sp = Gfx.Text(L.T("лайков", "likes"), 10.5f, 500);
        Gfx.Draw(sp, cx - sp.W * 0.5f, cy + 10, Th.Sub);

        float legX = donutCard.X + 158f, legY2 = donutCard.Y + 56f;
        DrawLegendRow(legX, legY2, 0xFF22C55Eu, L.T("Лайки ✓", "Likes ✓"), FormatNum(v.Likes));
        DrawLegendRow(legX, legY2 + 28, 0xFFEF4444u, L.T("Дизлайки ≈", "Dislikes ≈"), FormatNum(v.Dislikes));
        DrawLegendRow(legX, legY2 + 56, 0xFF38BDF8u, L.T("Комменты ✓", "Comments ✓"), FormatNum(v.CommentsCount));
        string ratioStr = v.Dislikes > 0 ? $"{(double)v.Likes / v.Dislikes:0.0} : 1" : (v.Likes > 0 ? "∞ : 1" : "—");
        DrawLegendRow(legX, legY2 + 84, Th.Brand, L.T("Лайк/Диз", "Ratio"), ratioStr);
        if (v.Rating > 0) DrawLegendRow(legX, legY2 + 112, 0xFFF59E0Bu, L.T("Оценка ≈", "Score ≈"), $"{v.Rating:0.00} / 5");

        var sbCard = new R(x + leftW + 12f, y, rightW, rowH);
        DrawSubCard(sbCard);
        Gfx.Draw(L.T("SponsorBlock (реклама)", "SponsorBlock Breakdown"), sbCard.X + 14, sbCard.Y + 12, 13.2f, Th.Text, 600);
        DrawBadge(sbCard.Right - 96, sbCard.Y + 10, "≈ Сообщество", 0xFFF59E0Bu);

        double totalAd = v.SponsorSec + v.SelfPromoSec + v.OtherSkipSec;
        double dur = v.DurationSec > 0 ? v.DurationSec : MpvPlayer.Instance.Duration;
        double adPct = dur > 0 ? Math.Clamp(totalAd * 100.0 / dur, 0, 100) : 0;
        double cleanPct = Math.Max(0, 100.0 - adPct);
        var barR = new R(sbCard.X + 14, sbCard.Y + 46, sbCard.W - 28, 16f);
        Gfx.Fill(barR, Th.A(0.10f), 8f);
        float bx2 = barR.X;
        float wC = (float)(barR.W * (cleanPct / 100.0) * chartEase);
        float wS = (float)(barR.W * (dur > 0 ? Math.Clamp(v.SponsorSec / dur, 0, 1) : 0) * chartEase);
        float wP = (float)(barR.W * (dur > 0 ? Math.Clamp(v.SelfPromoSec / dur, 0, 1) : 0) * chartEase);
        float wO = (float)(barR.W * (dur > 0 ? Math.Clamp(v.OtherSkipSec / dur, 0, 1) : 0) * chartEase);
        if (wC > 1) { Gfx.Fill(new R(bx2, barR.Y, wC, barR.H), 0xFF34D399u, 8f); bx2 += wC; }
        if (wS > 1) { Gfx.Fill(new R(bx2, barR.Y, MathF.Min(wS, barR.Right - bx2), barR.H), 0xFFE8B448u, 4f); bx2 += wS; }
        if (wP > 1) { Gfx.Fill(new R(bx2, barR.Y, MathF.Min(wP, barR.Right - bx2), barR.H), 0xFFFB923Cu, 4f); bx2 += wP; }
        if (wO > 1) { Gfx.Fill(new R(bx2, barR.Y, MathF.Min(wO, barR.Right - bx2), barR.H), 0xFFA78BFAu, 4f); }

        Gfx.Draw($"{L.T("Чистый контент", "Pure content")}: {cleanPct:0.0}%", sbCard.X + 14, sbCard.Y + 70, 12f, 0xFF34D399u, 600);
        Gfx.Draw(v.SegmentCount > 0
            ? $"{L.T("Интеграции", "Sponsor")}: {Fmt.Time(v.SponsorSec)}  ·  {L.T("Самореклама", "Self-promo")}: {Fmt.Time(v.SelfPromoSec)}  ·  {L.T("Прочее", "Other")}: {Fmt.Time(v.OtherSkipSec)}"
            : L.T("Рекламных сегментов не отмечено", "No SponsorBlock segments reported"),
            sbCard.X + 14, sbCard.Y + 90, 11.5f, totalAd > 0 ? 0xFFE8B448u : Th.Sub, 500, sbCard.W - 28, 2);

        Gfx.Fill(new R(sbCard.X + 14, sbCard.Y + 126, sbCard.W - 28, 1), Th.Line);
        Gfx.Draw(L.T("Твоя история по этому видео", "Your Personal Watch History"), sbCard.X + 14, sbCard.Y + 134, 12f, Th.Sub, 600);
        DrawBadge(sbCard.Right - 96, sbCard.Y + 132, "✓ 100% локально", 0xFF22C55Eu);
        string pStat = v.LocalPlays > 0
            ? $"{L.T("Открытий", "Sessions")}: {v.LocalPlays}×  ·  {L.T("Просмотрено", "Watched")}: {FormatWatchDuration(v.LocalWatchedSec)}"
            : L.T("Первый просмотр в FluxTube", "First watch in FluxTube");
        Gfx.Draw(pStat, sbCard.X + 14, sbCard.Y + 158, 12.5f, Th.Brand, 600, sbCard.W - 28, 1);
        y += rowH + 14;

        // 4. Honest Estimated Revenue Card (explicit non-100% warning!)
        var revCard = new R(x, y, w, 108f);
        DrawSubCard(revCard);
        Gfx.Draw(L.T("Примерная оценка дохода с AdSense за это видео", "Estimated AdSense Revenue for This Video"), revCard.X + 14, revCard.Y + 12, 13.2f, Th.Text, 600);
        DrawBadge(revCard.Right - 148, revCard.Y + 10, L.T("⚠ Оценка (не 100%)", "⚠ Estimate (not 100%)"), 0xFFFB7185u);
        if (v.Views > 0)
        {
            double low = v.Views / 1000.0 * 0.9;
            double high = v.Views / 1000.0 * 3.6;
            Gfx.Draw($"≈ ${FormatMoney(low)} – ${FormatMoney(high)}   ({L.T("вилка RPM $0.9–$3.6 за 1000 просмотров", "RPM range $0.9–$3.6 per 1K views")})",
                revCard.X + 14, revCard.Y + 40, 14f, 0xFF34D399u, 700, revCard.W - 28, 1);
            Gfx.Draw(L.T("Важно: YouTube не раскрывает точный доход. Реальная сумма сильно зависит от ГЕО зрителей, тематики, длины видео и наличия монетизации (не учитывает прямые рекламные интеграции).",
                "Note: YouTube keeps exact revenue private. Actual earnings vary widely by audience geography, niche, ad blockers, and sponsorships."),
                revCard.X + 14, revCard.Y + 66, 11.2f, Th.Sub, 400, revCard.W - 28, 2);
        }
        else
        {
            Gfx.Draw(L.T("Недостаточно данных о просмотрах для расчёта", "Not enough view data to estimate"), revCard.X + 14, revCard.Y + 44, 12.5f, Th.Sub);
        }
    }

    // ==================== TAB 1: SEO & TECHNICAL AUDIT ====================
    private static void DrawVideoSeoTechTab(R body, float sc, float chartEase, float openEase)
    {
        var v = _video;
        if (v == null) return;
        float x = body.X + 4, w = body.W - 14, y = body.Y + 4 - sc;

        // Compute transparent 100-point SEO Score
        bool okTitle = v.Title.Length is >= 25 and <= 85;
        bool okDesc = v.Description.Length >= 150;
        bool okTags = v.Tags.Count >= 5;
        bool okChap = v.ChaptersCount >= 2;
        bool okCaps = v.CaptionLangs.Count > 0;
        bool okHd = v.MaxHeight >= 1080;
        bool okEr = v.Views > 0 && (v.Likes * 100.0 / v.Views) >= 3.0;
        int seoScore = (okTitle ? 15 : 6) + (okDesc ? 15 : 5) + (okTags ? 15 : (v.Tags.Count > 0 ? 8 : 0))
            + (okChap ? 15 : 0) + (okCaps ? 15 : 0) + (okHd ? 15 : 5) + (okEr ? 10 : 4);

        // Top row: SEO Score Gauge + 7-Point Checklist
        float leftW = MathF.Round(w * 0.48f), rightW = w - leftW - 12f, topH = 228f;
        var seoCard = new R(x, y, leftW, topH);
        DrawSubCard(seoCard);
        Gfx.Draw(L.T("SEO-аудит оптимизации", "SEO Optimization Audit"), seoCard.X + 14, seoCard.Y + 12, 13.2f, Th.Text, 600);
        DrawBadge(seoCard.Right - 134, seoCard.Y + 10, L.T("⚠ Оценка алгоритма", "⚠ Algo Score"), 0xFFFB7185u);

        float cx = seoCard.X + 78f, cy = seoCard.Y + 122f;
        uint sCol = seoScore >= 80 ? 0xFF22C55Eu : seoScore >= 55 ? 0xFFF59E0Bu : 0xFFEF4444u;
        DrawDonutRing(cx, cy, 46f, 11f, seoScore / 100f, chartEase, sCol, Th.A(0.10f));
        var sr = Gfx.Text($"{(int)(seoScore * chartEase)}", 22f, 700);
        Gfx.Draw(sr, cx - sr.W * 0.5f, cy - sr.H * 0.5f - 4, Th.Text);
        var outOf = Gfx.Text("/ 100", 11f, 500);
        Gfx.Draw(outOf, cx - outOf.W * 0.5f, cy + 12, Th.Sub);

        float ckX = seoCard.X + 146f, ckY = seoCard.Y + 46f;
        DrawCheckItem(ckX, ckY, okTitle, $"{L.T("Название", "Title")} ({v.Title.Length} {L.T("симв.", "ch")})");
        DrawCheckItem(ckX, ckY + 24, okDesc, $"{L.T("Описание", "Description")} ({v.Description.Length} {L.T("симв.", "ch")})");
        DrawCheckItem(ckX, ckY + 48, okTags, $"{L.T("SEO-теги", "SEO Tags")} ({v.Tags.Count} {L.T("шт.", "pcs")})");
        DrawCheckItem(ckX, ckY + 72, okChap, $"{L.T("Таймкоды/главы", "Chapters")} ({v.ChaptersCount})");
        DrawCheckItem(ckX, ckY + 96, okCaps, $"{L.T("Субтитры", "Captions")} ({v.CaptionLangs.Count} {L.T("яз.", "langs")})");
        DrawCheckItem(ckX, ckY + 120, okHd, $"{L.T("Макс. качество", "Max Quality")} ({(v.MaxHeight > 0 ? v.MaxHeight + "p" : "—")})");
        DrawCheckItem(ckX, ckY + 144, v.AdsDetected == true, v.AdsDetected == true
            ? L.T("Монетизация: adPlacements ✓", "Monetized: adPlacements ✓")
            : L.T("Реклама YouTube не обнаружена (≈)", "No adPlacements detected (≈)"));

        // Right Card: Technical Capabilities Matrix (100% real from YouTube adaptiveFormats + mpv!)
        var techCard = new R(x + leftW + 12f, y, rightW, topH);
        DrawSubCard(techCard);
        Gfx.Draw(L.T("Технический аудит потоков", "Stream & Codec Audit"), techCard.X + 14, techCard.Y + 12, 13.2f, Th.Text, 600);
        DrawBadge(techCard.Right - 108, techCard.Y + 10, "✓ 100% YouTube", 0xFF22C55Eu);

        float tx = techCard.X + 14, ty = techCard.Y + 46;
        string maxRes = v.MaxHeight > 0 ? $"{v.MaxHeight}p{(v.MaxFps > 30 ? v.MaxFps.ToString() : "")}{(v.HasHdr ? " HDR" : "")}" : "—";
        DrawTechRow(tx, ty, techCard.W - 28, L.T("Макс. видеоформат", "Max Video Format"), maxRes);
        var codecs = new List<string>();
        if (v.HasAv1) codecs.Add("AV1");
        if (v.HasVp9) codecs.Add("VP9");
        if (v.HasH264) codecs.Add("H.264");
        DrawTechRow(tx, ty + 26, techCard.W - 28, L.T("Доступные кодеки", "Available Codecs"), codecs.Count > 0 ? string.Join(" · ", codecs) : "—");
        DrawTechRow(tx, ty + 52, techCard.W - 28, L.T("Пик битрейта видео / аудио", "Peak Video / Audio Bitrate"),
            v.MaxVideoBitrateKbps > 0 ? $"{v.MaxVideoBitrateKbps / 1000.0:0.1} Mbps / {v.MaxAudioBitrateKbps} kbps" : "—");
        DrawTechRow(tx, ty + 78, techCard.W - 28, L.T("Дорожки озвучки / Субтитры", "Audio Dubs / Subtitles"),
            $"{v.AudioTrackCount} {L.T("дор.", "tr.")}  ·  {(v.CaptionLangs.Count > 0 ? string.Join(", ", v.CaptionLangs.Take(6)) : L.T("нет", "none"))}");
        DrawTechRow(tx, ty + 104, techCard.W - 28, L.T("Статус", "Flags"),
            string.Join(" · ", new[]
            {
                v.IsFamilySafe ? "Family Safe" : "18+ / Restricted",
                v.IsUnlisted ? L.T("По ссылке", "Unlisted") : L.T("Публичное", "Public"),
                v.HasPaidPromotion ? L.T("Платная реклама", "Paid Promo") : ""
            }.Where(s => !string.IsNullOrEmpty(s))));

        var mp = MpvPlayer.Instance;
        string liveTelemetry = (mp.CurrentVideoId == v.VideoId && mp.VideoWidth > 0)
            ? $"{mp.VideoWidth}×{mp.VideoHeight} · {mp.AudioQualityBadge}"
            : L.T("Воспроизводится другое видео", "Another video is active");
        Gfx.Fill(new R(tx, ty + 132, techCard.W - 28, 1), Th.Line);
        Gfx.Draw(L.T("Сейчас в плеере (mpv):", "Active in player (mpv):"), tx, ty + 140, 11.5f, Th.Sub, 600);
        Gfx.Draw(liveTelemetry, tx, ty + 157, 12.2f, Th.Brand, 600, techCard.W - 28, 1);

        y += topH + 14;

        // Bottom: Full Hidden YouTube SEO Tags Cloud with 1-Click Copy All!
        var tagsCard = new R(x, y, w, 240f);
        DrawSubCard(tagsCard);
        Gfx.Draw($"{L.T("Все скрытые SEO-теги YouTube", "All Hidden YouTube SEO Tags")} ({v.Tags.Count})", tagsCard.X + 14, tagsCard.Y + 12, 13.5f, Th.Text, 600);
        DrawBadge(tagsCard.X + 270, tagsCard.Y + 10, "✓ 100% YouTube", 0xFF22C55Eu);
        if (v.Tags.Count > 0)
        {
            var cpLbl = L.T("Копировать все теги", "Copy all tags");
            var cpW = UI.PillWidth(Th.Copy, cpLbl);
            if (UI.Pill(UI.Id("analytics:copytags"), tagsCard.Right - cpW - 12, tagsCard.Y + 7, 28, Th.Copy, cpLbl, out _))
                Cards.CopyText(string.Join(", ", v.Tags), L.T("Теги скопированы в буфер", "Tags copied to clipboard"));
        }
        if (v.Tags.Count == 0)
        {
            Gfx.Draw(_loadingVideo ? L.T("Загрузка скрытых тегов…", "Loading tags…") : L.T("Автор не указал SEO-теги для этого видео", "Author did not set SEO tags on this video"),
                tagsCard.X + 14, tagsCard.Y + 48, 12.5f, Th.Sub);
        }
        else
        {
            float tgx = tagsCard.X + 14, tgy = tagsCard.Y + 46;
            for (int i = 0; i < v.Tags.Count; i++)
            {
                var tag = v.Tags[i];
                var pillW = MathF.Min(230f, Gfx.Text("#" + tag, 12f, 500).W + 20f);
                if (tgx + pillW > tagsCard.Right - 14) { tgx = tagsCard.X + 14; tgy += 30f; }
                if (tgy + 26f > tagsCard.Bottom - 10) break;
                var trR = new R(tgx, tgy, pillW, 25f);
                bool hov = UI.Hover(trR);
                Gfx.Fill(trR, hov ? Gfx.Fade(Th.Brand, 0.25f) : Th.A(0.08f), 12.5f);
                Gfx.Stroke(trR, hov ? Gfx.Fade(Th.Brand, 0.65f) : Th.Line, 12.5f, 1f);
                Gfx.Draw("#" + tag, trR.X + 10, trR.Y + 5, 11.8f, hov ? Th.Text : Th.Sub, 500, pillW - 16, 1);
                if (UI.Click(UI.Id("analytics:tag:" + i), trR)) { Close(); Shell.OpenSearch(tag); return; }
                tgx += pillW + 6f;
            }
        }
    }

    // ==================== TAB 2: CHANNEL PRO ====================
    private static void DrawChannelProTab(R body, float sc, float chartEase, float openEase)
    {
        var c = _channel;
        if (c == null) return;
        float x = body.X + 4, w = body.W - 14, y = body.Y + 4 - sc;

        // Compute SocialBlade-style Grade (honestly marked as algorithmic estimate!)
        double subRatio = c.SubscribersNum > 0 && c.AvgViews > 0 ? c.AvgViews * 100.0 / c.SubscribersNum : 0;
        var (grade, gradeCol) = ComputeChannelGrade(c, subRatio);

        // Channel Header + Grade Badge
        Cards.Avatar(x, y, 52, c.Avatar, c.Title);
        Gfx.Draw(c.Title + (c.Verified ? " ✓" : ""), x + 64, y + 3, 17f, Th.Text, 700, w - 180, 1);
        var meta = string.Join("  ·  ", new[] { c.Handle, c.SubscribersText, c.VideoCountText, c.Country, c.JoinedDate }.Where(s => !string.IsNullOrEmpty(s)));
        Gfx.Draw(meta, x + 64, y + 27, 12.2f, Th.Sub, 400, w - 180, 1);

        var gradeR = new R(x + w - 110, y + 2, 110, 48);
        Gfx.Fill(gradeR, Gfx.Fade(gradeCol, 0.16f), 12f);
        Gfx.Stroke(gradeR, Gfx.Fade(gradeCol, 0.55f), 12f, 1.2f);
        var grTxt = Gfx.Text(grade, 20f, 700);
        Gfx.Draw(grTxt, gradeR.X + 12, gradeR.Y + 10, gradeCol);
        Gfx.Draw(L.T("Грейд ≈", "Grade ≈"), gradeR.X + 52, gradeR.Y + 10, 11f, Th.Text, 600);
        Gfx.Draw(L.T("оценка", "algo"), gradeR.X + 52, gradeR.Y + 25, 10f, Th.Sub);
        y += 64;

        // 1. 4 Animated KPI Cards
        float gap = 10f, kw = (w - gap * 3) / 4f, kh = 84f;
        long lifeAvg = c.TotalViewsNum > 0 && c.VideoCountNum > 0 ? c.TotalViewsNum / c.VideoCountNum : 0;
        string totalBig = c.TotalViewsNum > 0 ? FormatNum((long)(c.TotalViewsNum * chartEase)) : (string.IsNullOrEmpty(c.TotalViewsText) ? "—" : c.TotalViewsText);
        DrawKpiCard(new R(x, y, kw, kh), L.T("Всего просмотров", "All-Time Views"), totalBig,
            lifeAvg > 0 ? $"{L.T("За всё время", "Lifetime avg")}: {FormatNum(lifeAvg)}/{L.T("вид", "vid")}" : c.JoinedDate, Th.Brand, openEase, "✓ 100%", 0xFF22C55Eu);
        DrawKpiCard(new R(x + (kw + gap), y, kw, kh), L.T("Среднее / Медиана", "Avg / Median (24)"),
            c.AvgViews > 0 ? FormatNum((long)(c.AvgViews * chartEase)) : "—",
            c.MedianViews > 0 ? $"{L.T("Медиана", "Median")}: {FormatNum(c.MedianViews)} · {L.T("Пик", "Peak")}: {FormatNum(c.PeakViews)}" : "—", 0xFF38BDF8u, openEase, "✓ 100%", 0xFF22C55Eu);
        DrawKpiCard(new R(x + (kw + gap) * 2, y, kw, kh), L.T("Охват аудитории", "Audience Reach"),
            subRatio > 0 ? $"{subRatio * chartEase:0.0}%" : "—",
            subRatio >= 35 ? L.T("Очень живая аудитория", "Highly active audience") : subRatio >= 15 ? L.T("Хорошая активность", "Healthy activity") : L.T("Низкий охват подписч.", "Low sub activity"),
            0xFF34D399u, openEase, "≈ расчёт", 0xFFF59E0Bu);
        DrawKpiCard(new R(x + (kw + gap) * 3, y, kw, kh), L.T("Твоя история", "Your Watch Time"),
            c.LocalWatchedSec > 0 ? FormatWatchDuration(c.LocalWatchedSec * chartEase) : "0м",
            $"{L.T("Роликов", "Videos")}: {c.LocalUniqueVideos} · {c.LocalSessions}×", 0xFFA78BFAu, openEase, "✓ 100%", 0xFF22C55Eu);
        y += kh + 14;

        // 2. Interactive Animated Bar Chart + Average + Median + Trend Curve (Last 20 videos)
        float chartH = 264f;
        var chartCard = new R(x, y, w, chartH);
        DrawSubCard(chartCard);
        Gfx.Draw(L.T("График просмотров последних 20 видео (наведи для деталей · клик открывает видео)", "Last 20 Videos Views Chart (hover for details · click to open)"),
            chartCard.X + 14, chartCard.Y + 12, 13.2f, Th.Text, 600);
        DrawBadge(chartCard.Right - 108, chartCard.Y + 10, "✓ 100% YouTube", 0xFF22C55Eu);

        if (c.RecentVideos.Count == 0)
        {
            Gfx.Draw(_loadingChannel ? L.T("Загружаем последние 24 видео канала…", "Loading recent channel videos…") : L.T("Нет данных по последним видео", "No recent video data"),
                chartCard.X + 14, chartCard.Y + 52, 13f, Th.Sub);
        }
        else
        {
            var pts = c.RecentVideos.Take(20).Reverse().ToList();
            long maxV = Math.Max(1, pts.Max(p => p.Views));
            float plotX = chartCard.X + 54f, plotY = chartCard.Y + 46f, plotW = chartCard.W - 72f, plotH = chartH - 86f;

            for (int g = 0; g <= 3; g++)
            {
                float frac = g / 3f;
                float gy = plotY + plotH * (1f - frac);
                Gfx.Line(plotX, gy, plotX + plotW, gy, Gfx.Fade(Th.Line, 0.7f), 1f);
                Gfx.Draw(FormatNum((long)(maxV * frac)), chartCard.X + 10, gy - 7, 10.5f, Th.Sub, 500, 42, 1);
            }

            if (c.AvgViews > 0)
            {
                float avgY = plotY + plotH * (1f - Math.Clamp((float)c.AvgViews / maxV, 0f, 1f));
                Gfx.Line(plotX, avgY, plotX + plotW, avgY, Gfx.Fade(0xFF38BDF8u, 0.65f), 1.4f);
                var avgLbl = Gfx.Text(L.T("Средн.", "Avg") + " " + FormatNum(c.AvgViews), 10.5f, 600);
                Gfx.Fill(new R(plotX + plotW - avgLbl.W - 10, avgY - 18, avgLbl.W + 8, 16), Gfx.Fade(0xFF0C1824, 0.88f), 4);
                Gfx.Draw(avgLbl, plotX + plotW - avgLbl.W - 6, avgY - 17, 0xFF38BDF8u);
            }

            float slotW = plotW / pts.Count;
            float barW = Math.Clamp(slotW * 0.62f, 10f, 34f);
            int hovIdx = -1;
            float hovBarX = 0, hovBarY = 0;
            Span<(float X, float Y)> centers = stackalloc (float, float)[pts.Count];

            for (int i = 0; i < pts.Count; i++)
            {
                var pt = pts[i];
                float localT = Math.Clamp(((UI.Now - _chartAnimAt) - i * 20f) / 480f, 0f, 1f);
                float ease = 1f - MathF.Pow(1f - localT, 3f);
                if (localT < 1f) UI.Animate();

                float frac = Math.Clamp((float)pt.Views / maxV, 0.03f, 1f);
                float bh = MathF.Max(4f, plotH * frac * ease);
                float bx = plotX + i * slotW + (slotW - barW) * 0.5f;
                float by = plotY + plotH - bh;
                var hitR = new R(plotX + i * slotW, plotY, slotW, plotH + 14);
                bool hov = UI.Hover(hitR);
                if (hov) { hovIdx = i; hovBarX = bx + barW * 0.5f; hovBarY = by; }

                bool isPeak = pt.Views == c.PeakViews && c.PeakViews > 0;
                uint col = hov ? 0xFFFFFFFFu : isPeak ? 0xFF34D399u : Th.Brand;
                Gfx.Fill(new R(bx, by, barW, bh), Gfx.Fade(col, hov ? 0.96f : isPeak ? 0.88f : 0.72f), MathF.Min(6f, barW * 0.35f));
                if (hov || isPeak) Gfx.Stroke(new R(bx, by, barW, bh), col, MathF.Min(6f, barW * 0.35f), 1.2f);
                centers[i] = (bx + barW * 0.5f, by);

                if (UI.Click(UI.Id("analytics:bar:" + i), hitR))
                {
                    Close();
                    Shell.OpenVideo(new VideoItem { VideoId = pt.VideoId, Title = pt.Title, Channel = c.Title, ChannelId = c.ChannelId, AvatarUrl = c.Avatar, Duration = pt.Duration }, "analytics");
                    return;
                }
            }
            for (int i = 0; i < pts.Count - 1; i++)
                Gfx.Line(centers[i].X, centers[i].Y, centers[i + 1].X, centers[i + 1].Y, Gfx.Fade(0xFFFFFFFF, 0.36f), 1.6f);

            Gfx.Draw(L.T("← Более старые", "← Older"), plotX, plotY + plotH + 8, 10.5f, Th.Sub);
            var newLbl = Gfx.Text(L.T("Новые →", "Newest →"), 10.5f, 400);
            Gfx.Draw(newLbl, plotX + plotW - newLbl.W, plotY + plotH + 8, Th.Sub);

            if (hovIdx >= 0)
            {
                var hp = pts[hovIdx];
                float tw = 260f, th = 58f;
                float tx = Math.Clamp(hovBarX - tw * 0.5f, chartCard.X + 10, chartCard.Right - tw - 10);
                float ty = MathF.Max(chartCard.Y + 34, hovBarY - th - 10);
                var tt = new R(tx, ty, tw, th);
                Gfx.Fill(tt, Gfx.Fade(0xFF101116, 0.96f), 10f);
                Gfx.Stroke(tt, Gfx.Fade(Th.Brand, 0.65f), 10f, 1.1f);
                Gfx.Draw(hp.Title, tt.X + 10, tt.Y + 8, 12f, 0xFFFFFFFF, 600, tw - 20, 1);
                double vsAvg = c.AvgViews > 0 ? (hp.Views - c.AvgViews) * 100.0 / c.AvgViews : 0;
                string deltaStr = c.AvgViews > 0 ? $" ({(vsAvg >= 0 ? "+" : "")}{vsAvg:0}%)" : "";
                Gfx.Draw($"{FormatNum(hp.Views)}{deltaStr}  ·  {hp.When}  ·  {hp.Duration}", tt.X + 10, tt.Y + 30, 11.2f, vsAvg >= 0 ? 0xFF34D399u : 0xFF93C5FDu, 500, tw - 20, 1);
            }
        }
        y += chartH + 14;

        // 3. Bottom Two Cards: Left = Stability, Cadence & Favorite Video | Right = Estimated Revenue (Honest non-100% warning!)
        float leftW = MathF.Round(w * 0.50f), rightW = w - leftW - 12f, botH = 186f;
        var healthCard = new R(x, y, leftW, botH);
        DrawSubCard(healthCard);
        Gfx.Draw(L.T("Стабильность и ритм канала", "Channel Consistency & Cadence"), healthCard.X + 14, healthCard.Y + 12, 13.2f, Th.Text, 600);
        DrawBadge(healthCard.Right - 104, healthCard.Y + 10, "≈ расчёт", 0xFFF59E0Bu);

        // Consistency bar
        float cons = (float)(c.ConsistencyScore * chartEase);
        Gfx.Draw($"{L.T("Стабильность просмотров", "View consistency")}: {cons:0}%", healthCard.X + 14, healthCard.Y + 42, 12f, Th.Text, 500);
        var cBar = new R(healthCard.X + 14, healthCard.Y + 62, healthCard.W - 28, 10f);
        Gfx.Fill(cBar, Th.A(0.10f), 5f);
        Gfx.Fill(new R(cBar.X, cBar.Y, cBar.W * Math.Clamp(cons / 100f, 0f, 1f), cBar.H), cons >= 60 ? 0xFF34D399u : 0xFFF59E0Bu, 5f);

        double outlier = c.MedianViews > 0 ? (double)c.PeakViews / c.MedianViews : 0;
        DrawTechRow(healthCard.X + 14, healthCard.Y + 84, healthCard.W - 28, L.T("Частота выхода (≈ по датам)", "Upload pace (≈ rel. dates)"),
            c.UploadsPerWeek > 0 ? $"≈ {c.UploadsPerWeek:0.0} {L.T("видео / нед.", "videos / wk")}" : "—");
        DrawTechRow(healthCard.X + 14, healthCard.Y + 108, healthCard.W - 28, L.T("Средняя длительность", "Avg video length"),
            c.AvgDurationSec > 0 ? Fmt.Time(c.AvgDurationSec) : "—");
        DrawTechRow(healthCard.X + 14, healthCard.Y + 132, healthCard.W - 28, L.T("Всплеск хита (Пик / Медиана)", "Viral outlier (Peak / Median)"),
            outlier > 0 ? $"{outlier:0.1}×" : "—");
        if (!string.IsNullOrEmpty(c.LocalFavVideoTitle))
            Gfx.Draw($"{L.T("Твой топ-ролик ✓", "Your fav ✓")}: {c.LocalFavVideoTitle} ({FormatWatchDuration(c.LocalFavVideoSec)})",
                healthCard.X + 14, healthCard.Y + 158, 11.5f, Th.Brand, 600, healthCard.W - 28, 1);

        var revCard = new R(x + leftW + 12f, y, rightW, botH);
        DrawSubCard(revCard);
        Gfx.Draw(L.T("Оценка оборота и дохода", "Est. Monthly Views & Revenue"), revCard.X + 14, revCard.Y + 12, 13.2f, Th.Text, 600);
        DrawBadge(revCard.Right - 148, revCard.Y + 10, L.T("⚠ Оценка (не 100%)", "⚠ Estimate (not 100%)"), 0xFFFB7185u);

        long monthlyV = c.EstMonthlyViews > 0 ? c.EstMonthlyViews : (c.AvgViews * 4);
        if (c.AvgViews > 0)
        {
            DrawTechRow(revCard.X + 14, revCard.Y + 46, revCard.W - 28, L.T("Просмотры за 30 дн. (≈)", "Est. 30-day views (≈)"), FormatNum(monthlyV));
            DrawTechRow(revCard.X + 14, revCard.Y + 72, revCard.W - 28, L.T("С 1 ролика (RPM $0.9–$3.6)", "Per video ($0.9–$3.6 RPM)"),
                $"≈ ${FormatMoney(c.AvgViews / 1000.0 * 0.9)} – ${FormatMoney(c.AvgViews / 1000.0 * 3.6)}");
            DrawTechRow(revCard.X + 14, revCard.Y + 98, revCard.W - 28, L.T("В месяц AdSense (≈)", "Monthly AdSense (≈)"),
                $"≈ ${FormatMoney(monthlyV / 1000.0 * 0.9)} – ${FormatMoney(monthlyV / 1000.0 * 3.6)}");
        }
        Gfx.Draw(L.T("⚠ Приблизительный расчёт (не 100% точно): YouTube не отдаёт реальный RPM и просмотры старого бэк-каталога. Прямые интеграции и донаты здесь не учитываются.",
            "⚠ Approximate estimate (not 100% exact): YouTube does not expose private RPM, back-catalog views, or direct sponsorships."),
            revCard.X + 14, revCard.Y + 128, 11.0f, Th.Sub, 400, revCard.W - 28, 3);
        y += botH + 14;

        // 4. Hidden Channel Keywords (if present)
        if (c.ChannelKeywords.Count > 0)
        {
            var kwCard = new R(x, y, w, 108f);
            DrawSubCard(kwCard);
            Gfx.Draw(L.T("Скрытые ключевые слова канала", "Hidden Channel Keywords"), kwCard.X + 14, kwCard.Y + 12, 13f, Th.Text, 600);
            DrawBadge(kwCard.Right - 108, kwCard.Y + 10, "✓ 100% YouTube", 0xFF22C55Eu);
            float tgx = kwCard.X + 14, tgy = kwCard.Y + 42;
            for (int i = 0; i < c.ChannelKeywords.Count; i++)
            {
                var kwItem = c.ChannelKeywords[i];
                var pillW = MathF.Min(200f, Gfx.Text(kwItem, 11.5f, 500).W + 18f);
                if (tgx + pillW > kwCard.Right - 14) { tgx = kwCard.X + 14; tgy += 28f; }
                if (tgy + 24f > kwCard.Bottom - 6) break;
                var trR = new R(tgx, tgy, pillW, 23f);
                bool hov = UI.Hover(trR);
                Gfx.Fill(trR, hov ? Gfx.Fade(Th.Brand, 0.24f) : Th.A(0.08f), 11.5f);
                Gfx.Stroke(trR, hov ? Gfx.Fade(Th.Brand, 0.6f) : Th.Line, 11.5f, 1f);
                Gfx.Draw(kwItem, trR.X + 9, trR.Y + 4, 11.5f, hov ? Th.Text : Th.Sub, 500, pillW - 14, 1);
                if (UI.Click(UI.Id("analytics:chkw:" + i), trR)) { Close(); Shell.OpenSearch(kwItem); return; }
                tgx += pillW + 6f;
            }
        }
    }

    private static (string Grade, uint Color) ComputeChannelGrade(ChannelAnalyticsData c, double subRatio)
    {
        double score = 0;
        if (c.AvgViews >= 1_000_000) score += 45;
        else if (c.AvgViews >= 250_000) score += 38;
        else if (c.AvgViews >= 50_000) score += 30;
        else if (c.AvgViews >= 10_000) score += 22;
        else if (c.AvgViews > 0) score += 12;

        if (subRatio >= 40) score += 30;
        else if (subRatio >= 20) score += 23;
        else if (subRatio >= 10) score += 16;
        else if (subRatio > 0) score += 8;

        score += c.ConsistencyScore * 0.25;
        if (score >= 88) return ("S+", 0xFF34D399u);
        if (score >= 76) return ("S", 0xFF22C55Eu);
        if (score >= 65) return ("A+", 0xFF38BDF8u);
        if (score >= 52) return ("A", 0xFF60A5FAu);
        if (score >= 38) return ("B+", 0xFFF59E0Bu);
        return ("B", 0xFFFB923Cu);
    }

    private static float DrawBadge(float x, float y, string text, uint col)
    {
        var tr = Gfx.Text(text, 10.5f, 600);
        float w = tr.W + 14f;
        var r = new R(x, y, w, 20f);
        Gfx.Fill(r, Gfx.Fade(col, 0.16f), 10f);
        Gfx.Stroke(r, Gfx.Fade(col, 0.45f), 10f, 0.9f);
        Gfx.Draw(tr, x + 7f, y + 3f, col);
        return x + w;
    }

    private static void DrawCheckItem(float x, float y, bool ok, string label)
    {
        Gfx.Circle(x + 6, y + 8, 5.5f, Gfx.Fade(ok ? 0xFF22C55Eu : 0xFFEF4444u, 0.22f));
        Gfx.Draw(ok ? "✓" : "!", x + 3, y + 1, 11f, ok ? 0xFF22C55Eu : 0xFFF59E0Bu, 700);
        Gfx.Draw(label, x + 18, y + 1, 11.8f, ok ? Th.Text : Th.Sub, ok ? 500 : 400, 260, 1);
    }

    private static void DrawTechRow(float x, float y, float w, string label, string val)
    {
        Gfx.Draw(label, x, y, 11.8f, Th.Sub, 500, w * 0.52f, 1);
        var vr = Gfx.Text(val, 12f, 600, w * 0.48f, 1);
        Gfx.Draw(vr, x + w - vr.W, y, Th.Text);
    }

    private static void DrawSubCard(R r)
    {
        float rad = Gfx.Corner(14);
        Gfx.Fill(r, Th.A(0.055f), rad);
        Gfx.Stroke(r, Th.Line, rad, 1f);
    }

    private static void DrawKpiCard(R r, string label, string bigVal, string sub, uint accent, float openEase, string tag, uint tagCol)
    {
        float rad = Gfx.Corner(13);
        Gfx.Fill(r, Th.A(0.06f), rad);
        Gfx.Stroke(r, Gfx.Fade(accent, 0.30f * openEase), rad, 1f);
        Gfx.Fill(new R(r.X + 10, r.Y + 12, 3.2f, r.H - 24), accent, 1.6f);
        Gfx.Draw(label, r.X + 20, r.Y + 10, 11.2f, Th.Sub, 500, r.W - 76, 1);
        var tg = Gfx.Text(tag, 9.8f, 600);
        Gfx.Fill(new R(r.Right - tg.W - 14, r.Y + 8, tg.W + 8, 16), Gfx.Fade(tagCol, 0.16f), 8f);
        Gfx.Draw(tg, r.Right - tg.W - 10, r.Y + 10, tagCol);
        Gfx.Draw(bigVal, r.X + 20, r.Y + 30, 19f, Th.Text, 700, r.W - 26, 1);
        Gfx.Draw(sub, r.X + 20, r.Y + 58, 10.8f, Gfx.Fade(accent, 0.92f), 500, r.W - 26, 1);
    }

    private static void DrawLegendRow(float x, float y, uint col, string label, string val)
    {
        Gfx.Circle(x + 5, y + 7, 4.5f, col);
        Gfx.Draw(label, x + 16, y, 11.8f, Th.Sub, 500);
        Gfx.Draw(val, x + 114, y, 12.2f, Th.Text, 700);
    }

    private static void DrawDonutRing(float cx, float cy, float radius, float thickness, float primaryFrac, float animProgress, uint primaryCol, uint secondaryCol)
    {
        const int segs = 64;
        float startAngle = -MathF.PI * 0.5f;
        float totalSweep = MathF.PI * 2f * animProgress;
        float primarySweep = totalSweep * Math.Clamp(primaryFrac, 0f, 1f);

        for (int i = 0; i < segs; i++)
        {
            float a0 = startAngle + i * (MathF.PI * 2f / segs);
            float a1 = startAngle + (i + 1) * (MathF.PI * 2f / segs);
            Gfx.Line(cx + MathF.Cos(a0) * radius, cy + MathF.Sin(a0) * radius,
                     cx + MathF.Cos(a1) * radius, cy + MathF.Sin(a1) * radius, Th.A(0.10f), thickness);
        }

        int activeSegs = Math.Max(1, (int)MathF.Ceiling(segs * animProgress));
        for (int i = 0; i < activeSegs; i++)
        {
            float f0 = (float)i / segs * MathF.PI * 2f;
            float f1 = MathF.Min(totalSweep, (float)(i + 1) / segs * MathF.PI * 2f);
            if (f1 <= f0) break;
            float a0 = startAngle + f0;
            float a1 = startAngle + f1;
            uint col = f0 < primarySweep ? primaryCol : secondaryCol;
            Gfx.Line(cx + MathF.Cos(a0) * radius, cy + MathF.Sin(a0) * radius,
                     cx + MathF.Cos(a1) * radius, cy + MathF.Sin(a1) * radius, col, thickness);
        }
    }

    private static string FormatNum(long n)
    {
        if (n <= 0) return "0";
        if (n >= 1_000_000_000) return $"{n / 1_000_000_000.0:0.##}B";
        if (n >= 1_000_000) return $"{n / 1_000_000.0:0.##}M";
        if (n >= 1_000) return $"{n / 1_000.0:0.#}K";
        return n.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatExact(long n) => n.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ');
    private static string FormatMoney(double v) => v >= 1_000_000 ? $"{v / 1_000_000.0:0.##}M" : v >= 1000 ? $"{v / 1000.0:0.#}K" : $"{v:0}";

    private static string FormatDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            return dt.ToString("dd.MM.yyyy");
        return raw;
    }

    private static string FormatAge(double hours)
    {
        if (hours < 24) return $"{hours:0.#} {L.T("ч назад", "h ago")}";
        double days = hours / 24.0;
        if (days < 30) return $"{days:0} {L.T("дн. назад", "d ago")}";
        if (days < 365) return $"{days / 30.0:0.#} {L.T("мес. назад", "mo ago")}";
        return $"{days / 365.0:0.#} {L.T("г. назад", "yr ago")}";
    }

    private static string FormatWatchDuration(double sec)
    {
        if (sec < 60) return $"{(int)sec}{L.T("с", "s")}";
        int mins = (int)(sec / 60);
        if (mins < 60) return $"{mins}{L.T("м", "m")}";
        int hrs = mins / 60;
        int rem = mins % 60;
        return rem > 0 ? $"{hrs}{L.T("ч", "h")} {rem}{L.T("м", "m")}" : $"{hrs}{L.T("ч", "h")}";
    }
}
