using System.IO;
using System.Net;
using System.Net.Http;

namespace RepoNLauncher.Services;

/// <summary>A filter list the ad blocker downloads, and from which strength on it's used.</summary>
public sealed record AdFilterList(string Id, string Name, string Url, AdBlockLevel From);

/// <summary>
/// The filter lists the dashboard browser's ad blocker reads: downloaded from their makers, kept beside the browser's
/// data and fetched again once they're 4 days old (as often as EasyList asks), plus a short list of the best-known ad and
/// tracker servers, built in, that blocks until the first download has finished, or when there's none.
/// </summary>
public static class AdFilterLists
{
    public static readonly AdFilterList EasyList =
        new("easylist", "EasyList", "https://easylist.to/easylist/easylist.txt", AdBlockLevel.Light);

    public static readonly AdFilterList[] All =
    [
        EasyList,
        new("easyprivacy", "EasyPrivacy", "https://easylist.to/easylist/easyprivacy.txt", AdBlockLevel.Balanced),
        new("peter-lowe", "Peter Lowe's list",
            "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=adblockplus&showintro=0&mimetype=plaintext", AdBlockLevel.Strict),
        new("annoyances", "Fanboy's Annoyance list", "https://easylist.to/easylist/fanboy-annoyance.txt", AdBlockLevel.Strict),
    ];

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(4);

    /// <summary>Beside the browser's data; uninstalling removes it with the rest.</summary>
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.GetFileName(DashboardConfigService.AppDataDir), "AdBlock");

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        var version = typeof(AdFilterLists).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RepoNLauncher/{version}");
        return http;
    }

    /// <summary>The lists a strength reads.</summary>
    public static IReadOnlyList<AdFilterList> For(AdBlockLevel level) => All.Where(l => l.From <= level).ToList();

    private static string PathOf(AdFilterList list) => Path.Combine(Folder, list.Id + ".txt");

    /// <summary>When the list was last downloaded, or null when it hasn't been.</summary>
    public static DateTime? UpdatedAt(AdFilterList list)
    {
        var path = PathOf(list);
        return File.Exists(path) ? File.GetLastWriteTime(path) : null;
    }

    public static bool IsStale(AdFilterList list) => UpdatedAt(list) is not { } at || DateTime.Now - at > MaxAge;

    /// <summary>Downloads the list, replacing the one kept only once the new one has arrived whole.</summary>
    public static async Task DownloadAsync(AdFilterList list, CancellationToken cancellationToken = default)
    {
        var text = await Http.GetStringAsync(list.Url, cancellationToken);
        // A captive portal's or error page, say, rather than the list: keep the one there is.
        if (text.Length < 1000 || !text.Contains("||", StringComparison.Ordinal))
            throw new InvalidDataException("what came back isn't a filter list");

        Directory.CreateDirectory(Folder);
        var path = PathOf(list);
        var partial = path + ".download";
        await File.WriteAllTextAsync(partial, text, cancellationToken);
        File.Move(partial, path, overwrite: true);
    }

    /// <summary>
    /// The rules a strength blocks with: the built-in list, then each downloaded list it reads. Light reads EasyList for
    /// its servers only, hiding nothing.
    /// </summary>
    public static AdFilterEngine Build(AdBlockLevel level, out IReadOnlyList<AdFilterList> read)
    {
        var hiding = level >= AdBlockLevel.Balanced;
        var lists = new List<(string Text, bool Hiding)> { (BuiltInAds, hiding) };
        if (hiding) lists.Add((BuiltInTrackers, true));

        var found = new List<AdFilterList>();
        foreach (var list in For(level))
        {
            string text;
            try
            {
                text = File.ReadAllText(PathOf(list));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            lists.Add((text, hiding));
            found.Add(list);
        }
        read = found;
        return AdFilterEngine.Build(lists);
    }

    // The best-known ad servers and the spaces pages leave for their ads, enough to block most ads before the lists arrive.
    private const string BuiltInAds = """
        ||doubleclick.net^
        ||googlesyndication.com^
        ||googleadservices.com^
        ||googletagservices.com^
        ||adservice.google.com^
        ||amazon-adsystem.com^
        ||adnxs.com^
        ||adsrvr.org^
        ||criteo.com^
        ||criteo.net^
        ||taboola.com^
        ||outbrain.com^
        ||pubmatic.com^
        ||rubiconproject.com^
        ||openx.net^
        ||casalemedia.com^
        ||indexww.com^
        ||smartadserver.com^
        ||adform.net^
        ||yieldmo.com^
        ||sharethrough.com^
        ||3lift.com^
        ||33across.com^
        ||media.net^
        ||moatads.com^
        ||adsafeprotected.com^
        ||doubleverify.com^
        ||serving-sys.com^
        ||advertising.com^
        ||bidswitch.net^
        ||contextweb.com^
        ||gumgum.com^
        ||teads.tv^
        ||spotxchange.com^
        ||springserve.com^
        ||lijit.com^
        ||sovrn.com^
        ||zemanta.com^
        ||revcontent.com^
        ||mgid.com^
        ||propellerads.com^
        ||popads.net^
        ||popcash.net^
        ||adcash.com^
        ||exoclick.com^
        ||juicyads.com^
        ||trafficjunky.net^
        ||adskeeper.com^
        ||adroll.com^
        ||ads-twitter.com^
        ||ads.linkedin.com^
        ||an.yandex.ru^
        ||adfox.ru^
        ||inmobi.com^
        ||ads.yahoo.com^
        ||yieldlab.net^
        ||adition.com^
        ||smaato.net^
        ||adcolony.com^
        ||unityads.unity3d.com^
        ##ins.adsbygoogle
        ##.adsbygoogle
        ##[id^="div-gpt-ad"]
        ##[id^="google_ads_iframe"]
        ##.OUTBRAIN
        ##[id^="taboola-"]
        ##.trc_related_container
        """;

    private const string BuiltInTrackers = """
        ||google-analytics.com^
        ||googletagmanager.com^
        ||analytics.google.com^
        ||scorecardresearch.com^
        ||quantserve.com^
        ||hotjar.com^
        ||mixpanel.com^
        ||connect.facebook.net^
        ||analytics.twitter.com^
        ||bat.bing.com^
        ||clarity.ms^
        ||rlcdn.com^
        ||krxd.net^
        ||bluekai.com^
        ||demdex.net^
        ||omtrdc.net^
        ||chartbeat.com^
        ||chartbeat.net^
        ||mc.yandex.ru^
        ||analytics.tiktok.com^
        ||tr.snapchat.com^
        ||ct.pinterest.com^
        ||px.ads.linkedin.com^
        ||fullstory.com^
        ||nr-data.net^
        ||branch.io^
        ||amplitude.com^
        ||segment.io^
        ||heapanalytics.com^
        """;
}
