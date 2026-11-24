using System.Diagnostics;
using FluxTube.Models;
using FluxTube.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var yt = InnerTube.Instance;
var sw = Stopwatch.StartNew();

void Dump(string name, FeedPage p)
{
    Console.WriteLine($"== {name}: {p.Items.Count} items, cont={(p.Continuation != null)} ({sw.ElapsedMilliseconds} ms)");
    foreach (var v in p.Items.Take(4))
        Console.WriteLine($"  {v.VideoId} [{v.DurationBadge}] {v.Title} | {v.ChannelMeta} | ch={v.ChannelId} av={(v.AvatarUrl != null)}");
    sw.Restart();
}

var home = await yt.HomeAsync();
Dump("home", home);
Console.WriteLine("loggedIn=" + yt.LoggedIn + " cookieLogin=" + CookieStore.IsLoggedIn);
var subs = await yt.BrowseAsync("FEsubscriptions");
Dump("subs", subs);
if (subs.Continuation != null) Dump("subs+", await yt.ContinueAsync("browse", subs.Continuation));
Dump("watchlater", await yt.BrowseAsync("VLWL"));
Dump("liked", await yt.BrowseAsync("VLLL"));
Dump("channel", await yt.ChannelVideosAsync("UCuAXFkgsw1L7xaCfnd5JJOw"));
if (home.Continuation != null) Dump("home+", await yt.ContinueAsync("browse", home.Continuation));
var search = await yt.SearchAsync("lofi hip hop");
Dump("search", search);
if (search.Continuation != null) Dump("search+", await yt.ContinueAsync("search", search.Continuation));
var w = await yt.WatchAsync("dQw4w9WgXcQ");
Console.WriteLine($"== watch: {w.Title} | {w.Channel} {w.ChannelId} | {w.Subscribers} | {w.Views} | {w.Date} | likes={w.Likes} | desc={w.Description.Length}ch av={(w.ChannelAvatar != null)}");
Dump("related", w.Related);
if (w.Related.Continuation != null) Dump("related+", await yt.ContinueAsync("next", w.Related.Continuation));
Console.WriteLine("suggest: " + string.Join(", ", await yt.SuggestAsync("minecr")));
Console.WriteLine("loggedIn=" + yt.LoggedIn);
