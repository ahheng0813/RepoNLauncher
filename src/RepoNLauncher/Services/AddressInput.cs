using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RepoNLauncher.Services;

/// <summary>Where the dashboard browser searches for what's typed in its address that isn't an address.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SearchEngine
{
    Google,
    Bing,
    DuckDuckGo,
}

/// <summary>
/// What's typed in the dashboard browser's address: an address to go to when it looks like one, as browsers take it,
/// otherwise words to search for. localhost:3000, 192.168.1.5, example.com and https://anything are addresses;
/// "react hooks", next.js and what? are searches.
/// </summary>
public static class AddressInput
{
    /// <summary>The page to show for what was typed.</summary>
    public static string Resolve(string text, SearchEngine engine)
    {
        text = text.Trim();
        return AsAddress(text) ?? SearchUrl(engine, text);
    }

    public static string SearchUrl(SearchEngine engine, string query)
    {
        var q = Uri.EscapeDataString(query);
        return engine switch
        {
            SearchEngine.Bing => $"https://www.bing.com/search?q={q}",
            SearchEngine.DuckDuckGo => $"https://duckduckgo.com/?q={q}",
            _ => $"https://www.google.com/search?q={q}",
        };
    }

    private static readonly Regex IPv4 = new(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.CultureInvariant);
    private static readonly Regex ExplicitPort = new(@"^[^/?#@\s]+:\d{1,5}(?:[/?#]|$)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The address typed, with the scheme it leaves out (http for this PC's and its network's sites, as dev servers
    /// serve, https for the rest), or null when it isn't one, so it's searched for.
    /// </summary>
    public static string? AsAddress(string text)
    {
        if (text.Length == 0 || text.Any(char.IsWhiteSpace)) return null;
        if (text.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(text, UriKind.Absolute, out _) ? text : null;
        if (text.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return text;

        // A trailing ? asks a question; someone@example.com is an email address.
        if (text.EndsWith('?') || !Uri.TryCreate("http://" + text, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0)
            return null;

        // The host as typed: the parsed one would read 3.14 as the IP address 3.0.0.14.
        var host = text.Split('/', '?', '#')[0].ToLowerInvariant();
        if (!host.StartsWith('['))
        {
            var colon = host.LastIndexOf(':');
            if (colon >= 0) host = host[..colon];
        }
        host = host.TrimEnd('.');

        var isAddress = host == "localhost" || IPv4.IsMatch(host) || host.StartsWith('[') || ExplicitPort.IsMatch(text)
                        || HasKnownEnding(host);
        if (!isAddress) return null;
        // A name without dots, such as myserver:8080, is a PC on this network.
        var local = IsLocalHost(uri.Host) || !host.Contains('.');
        return (local ? "http://" : "https://") + text;
    }

    // A dotted name ending the way registered names do (.com, .dev, .my…) or local ones (.local, .test…): next.js and
    // file.txt are searched for, as no site ends in .js or .txt.
    private static bool HasKnownEnding(string host)
    {
        var dot = host.LastIndexOf('.');
        return dot > 0 && dot < host.Length - 1 && KnownEndings.Contains(host[(dot + 1)..]);
    }

    // Names only a PC or its network has, for its own servers.
    private static readonly HashSet<string> LocalEndings = new(StringComparer.Ordinal)
    {
        "localhost", "local", "test", "internal", "lan", "home", "corp", "intranet",
    };

    private static readonly HashSet<string> KnownEndings = BuildEndings();

    private static HashSet<string> BuildEndings()
    {
        // Every country's two-letter ending, then the common generic ones.
        const string countries =
            "ac ad ae af ag ai al am ao aq ar as at au aw ax az ba bb bd be bf bg bh bi bj bm bn bo br bs bt bw by bz ca cc cd cf " +
            "cg ch ci ck cl cm cn co cr cu cv cw cx cy cz de dj dk dm do dz ec ee eg er es et eu fi fj fk fm fo fr ga gd ge gf gg " +
            "gh gi gl gm gn gp gq gr gs gt gu gw gy hk hm hn hr ht hu id ie il im in io iq ir is it je jm jo jp ke kg kh ki km kn " +
            "kp kr kw ky kz la lb lc li lk lr ls lt lu lv ly ma mc md me mg mh mk ml mm mn mo mp mq mr ms mt mu mv mw mx my mz na " +
            "nc ne nf ng ni nl no np nr nu nz om pa pe pf pg ph pk pl pm pn pr ps pt pw py qa re ro rs ru rw sa sb sc sd se sg sh " +
            "si sk sl sm sn so sr ss st su sv sx sy sz tc td tf tg th tj tk tl tm tn to tr tt tv tw tz ua ug uk us uy uz va vc ve " +
            "vg vi vn vu wf ws ye yt za zm zw";
        const string generic =
            "com net org edu gov mil int info biz name pro mobi asia jobs tel travel museum aero coop cat post arpa " +
            "app dev page new xyz site online website web tech store shop blog cloud wiki news art design top club live " +
            "space link click fun email network systems solutions agency studio digital media software tools codes run " +
            "host hosting games game today world life team works zone city company center inc llc ltd global group plus " +
            "one red blue black pink green moe lol icu vip win bet bid men work tips guru expert academy school " +
            "education university career careers jobs money finance bank insurance capital fund ventures market " +
            "markets trade exchange sale deals social chat community forum review reviews photo photos pics video " +
            "audio music movie film show tv radio press report today express best cool cafe bar pub restaurant " +
            "pizza food recipes health care clinic dental fitness yoga golf football soccer bike auto car cars " +
            "realty properties house land estate build construction energy solar eco garden farm pet dog fashion " +
            "style beauty hair makeup jewelry shoes watch watches gold fit love dating wedding family kids baby " +
            "help support services consulting legal law tax accountant attorney engineering science ai " +
            "google youtube android chrome gmail amazon aws microsoft azure apple bing windows office xbox " +
            "nyc london paris berlin tokyo";
        var endings = new HashSet<string>(StringComparer.Ordinal);
        endings.UnionWith(countries.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        endings.UnionWith(generic.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        endings.UnionWith(LocalEndings);
        return endings;
    }

    /// <summary>
    /// A site served from this PC or its network, such as localhost:3000 or 192.168.1.5: one of the user's own projects.
    /// It's taken as http, and the ad blocker leaves it as it is.
    /// </summary>
    public static bool IsLocalHost(string host)
    {
        var dot = host.LastIndexOf('.');
        if (host == "localhost" || LocalEndings.Contains(dot >= 0 ? host[(dot + 1)..] : string.Empty)) return true;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return false;
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return true;
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                                 || (b[0] == 169 && b[1] == 254));
    }
}
