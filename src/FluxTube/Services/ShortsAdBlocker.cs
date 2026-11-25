using System.Text.RegularExpressions;
using FluxTube.Models;

namespace FluxTube.Services;

/// <summary>
/// Multi-layer YouTube Shorts Ad &amp; Sponsored video detector ("off" / "standard" / "max"):
/// 1. Hard YouTube ad slot &amp; paid promotion flags from InnerTube metadata
/// 2. SponsorBlock full-video ("actionType":"full") and high-coverage sponsor/selfpromo/exclusive_access segments
/// 3. Mandatory Russian ad-law disclosures (erid tokens, Реклама. ООО/ИП, ИНН+реклама)
/// 4. RU/EN explicit ad hashtags (#ad, #реклама, #sponsored, #направахрекламы, #интеграция)
/// 5. Max-mode aggressive detection for promo codes, betting/casino lures, WB/Ozon article-spam ads, and affiliate funnels.
/// </summary>
public static partial class ShortsAdBlocker
{
    private static int _blockedSessionCount;
    public static int BlockedSessionCount => Volatile.Read(ref _blockedSessionCount);
    public static void RecordBlocked() => Interlocked.Increment(ref _blockedSessionCount);

    public static string Mode => AppSettings.Current.ShortsAdBlock;
    public static bool Enabled => Mode != "off";
    public static bool IsMax => Mode == "max";

    // Mandatory Russian ad marking (erid: 2Vtz..., Реклама. ООО/ИП/АО, ИНН + реклама)
    [GeneratedRegex(@"\berid\s*[:=\-#]?\s*[a-z0-9]{6,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EridRx();

    [GeneratedRegex(@"(?:^|[\s.,!?;:|/(\[])реклам[аы]\.?\s*(?:ооо|ип|ао|пао|зао|инн|огрн|«|"")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RuLegalAdRx();

    [GeneratedRegex(@"\b(?:на\s+правах\s+рекламы|рекламн\w+\s+(?:ролик|интеграци\w*|пост|материал|выпуск)|includes?\s+paid\s+promotion|paid\s+(?:partnership|promotion|sponsorship)|sponsored\s+by|sponsored\s+video|ad\s+break)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitPaidRx();

    // Explicit ad hashtags
    [GeneratedRegex(@"(?:^|[\s.,!?;:|/\\(\[])#(ad|ads|sponsored|paidpromotion|paidpartnership|advertisement|реклама|направахрекламы|партнерка|рекламныйролик|интеграция)(?=$|[\s.,!?;:|/\\)\]])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdHashtagRx();

    // Max mode: promo codes, discount codes, betting/casino, marketplace article spam, affiliate funnel phrases
    [GeneratedRegex(@"\b(промокод\w*|по\s+промокоду|код\s+на\s+скидку|скидк\w+\s+по\s+ссылке|купить\s+со\s+скидкой|заказывай\s+по\s+ссылке|ссылка\s+на\s+товар|use\s+code\s+[a-z0-9_-]{3,}|promo\s*code|discount\s+code|coupon\s+code|%\s*off\s+with\s+code)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PromoCodeRx();

    [GeneratedRegex(@"\b(1xbet|1win|melbet|fonbet|winline|betboom|mostbet|parimatch|betcity|leonbets|pin[\s-]?up|stake\.com|bc\.game|букмекер\w*|ставки\s+на\s+спорт|бонус\s+на\s+депозит|фрибет\w*|фриспин\w*|бесплатн\w+\s+вращени\w*|онлайн[\s-]?казино|online\s+casino|gambling\s+bonus)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BettingCasinoRx();

    [GeneratedRegex(@"\b(артикул\w*|арт\.?\s*\d{6,})\b.*\b(wb|вб|wildberries|вайлдберриз|ozon|озон|яндекс\s*маркет|алиэкспресс|aliexpress)\b|\b(wb|вб|wildberries|вайлдберриз|ozon|озон)\b.*\b(артикул\w*|арт\.?\s*\d{6,})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex MarketplaceSpamRx();

    [GeneratedRegex(@"\b(ссылка\s+в\s+(?:шапке|профиле|закрепе|комментариях|описании)|link\s+in\s+(?:bio|description|pinned))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkInBioRx();

    [GeneratedRegex(@"\b(скидк|бесплатн\w+\s+(?:доступ|пробн|забер|получи)|выгодн|купи|заказ|бот\s+в\s+тг|тг[\s-]?бот|vpn|впн|крипт|заработ|инвестиц|курс\s+по|казино|бонус|discount|free\s+trial|shop\s+now|order\s+now|buy\s+now|deal|offer|sponsor)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FunnelIntentRx();

    [GeneratedRegex(@"\bинн\s*:?\s*\d{10,12}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InnRx();

    [GeneratedRegex(@"\b(огрн\w*|реклам\w*|ооо|ип|маркировк)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InnContextRx();

    /// <summary>
    /// Evaluates a Short using all available signals (InnerTube ad/paid flags, SponsorBlock full/partial segments,
    /// title/description/channel heuristics, and user-blocked channels). Returns the block reason or null if clean.
    /// </summary>
    public static string? Check(VideoItem? item, WatchInfo? watch, IReadOnlyList<Extras.Segment>? segments, double durationSeconds = 0)
    {
        // Always respect user-blocked channels in Shorts too
        var chId = item?.ChannelId ?? watch?.ChannelId;
        if (!string.IsNullOrEmpty(chId) && Array.IndexOf(AppSettings.Current.BlockedChannels, chId) >= 0)
            return L.T("скрытый канал", "blocked channel");

        if (!Enabled) return null;

        // 1. Hard YouTube Ad / Paid Promotion flags from InnerTube
        if (item?.IsAd == true)
            return L.T("реклама YouTube", "YouTube Ad");
        if (item?.HasPaidPromotion == true || watch?.HasPaidPromotion == true)
            return L.T("платная реклама", "paid promotion");

        // 2. SponsorBlock full-video or high-coverage ad segments
        if (segments is { Count: > 0 })
        {
            double adSeconds = 0;
            double maxEnd = durationSeconds;
            foreach (var seg in segments)
            {
                if (seg.End > maxEnd) maxEnd = seg.End;
                bool isAdCat = seg.Category is "sponsor" or "selfpromo" or "exclusive_access";
                if (seg.ActionType == "full" && isAdCat)
                    return "SponsorBlock · " + Extras.CategoryName(seg.Category);
                if (isAdCat && seg.End > seg.Start)
                    adSeconds += (seg.End - seg.Start);
            }
            if (adSeconds > 0)
            {
                double effDur = durationSeconds > 1 ? durationSeconds : Math.Max(maxEnd, 60);
                double ratio = adSeconds / effDur;
                double threshold = durationSeconds > 1 ? (IsMax ? 0.50 : 0.70) : 0.68;
                if (ratio >= threshold || (durationSeconds > 1 && IsMax && adSeconds >= 24 && ratio >= 0.42))
                    return "SponsorBlock";
            }
        }

        // 3. Text analysis across Title + Description + Channel
        var title = item?.Title is { Length: > 0 } t0 ? t0 : (watch?.Title ?? "");
        var desc = watch?.Description is { Length: > 0 } d0 ? d0 : (item?.Description ?? "");
        var combined = desc.Length > 0 ? title + "\n" + desc : title;
        if (combined.Length == 0) return null;

        // Mandatory RU ad law disclosures & explicit paid tags (Standard + Max)
        if (EridRx().IsMatch(combined))
            return L.T("маркировка erid", "ad token (erid)");
        if (RuLegalAdRx().IsMatch(combined) || (InnRx().IsMatch(combined) && InnContextRx().IsMatch(combined)))
            return L.T("реклама (юрлицо)", "legal ad disclosure");
        if (ExplicitPaidRx().IsMatch(combined))
            return L.T("платная интеграция", "paid promotion");
        if (AdHashtagRx().IsMatch(combined))
            return L.T("тег #реклама", "#ad / #sponsored");

        if (!IsMax) return null;

        // 4. Max-mode aggressive promo / affiliate / betting / marketplace spam filter
        if (BettingCasinoRx().IsMatch(combined))
            return L.T("ставки / казино", "betting / casino ad");
        if (PromoCodeRx().IsMatch(combined))
            return L.T("промокод / реклама", "promo code ad");
        if (MarketplaceSpamRx().IsMatch(combined))
            return L.T("реклама маркетплейса", "marketplace ad");
        if (LinkInBioRx().IsMatch(combined) && FunnelIntentRx().IsMatch(combined))
            return L.T("рекламная воронка", "affiliate promo");

        return null;
    }
}
