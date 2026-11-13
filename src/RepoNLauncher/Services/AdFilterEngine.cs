using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RepoNLauncher.Services;

/// <summary>How hard the dashboard browser's ad blocker blocks: Settings, and the shield beside the browser's address.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdBlockLevel
{
    /// <summary>Ad servers only; nothing on the page is hidden.</summary>
    Light,

    /// <summary>Ads and trackers, and the spaces left for ads hidden.</summary>
    Balanced,

    /// <summary>Also more ad and tracker servers, annoyances such as cookie banners, and pop-ups opened without a click.</summary>
    Strict,
}

/// <summary>What a request is for, as the filter lists name the kinds.</summary>
[Flags]
public enum AdRequestType
{
    None = 0,
    Other = 1,
    Script = 2,
    Image = 4,
    Stylesheet = 8,
    Subdocument = 16,
    XmlHttpRequest = 32,
    Media = 64,
    Font = 128,
    WebSocket = 256,
    Ping = 512,
    Object = 1024,
    Popup = 2048,
    Requests = Other | Script | Image | Stylesheet | Subdocument | XmlHttpRequest | Media | Font | WebSocket | Ping | Object,
}

/// <summary>
/// The ad blocker's rules, read from filter lists in Adblock Plus' syntax (EasyList's): which requests a page may not make,
/// which pop-ups it may not open, and which of its elements to hide. It reads the part of the syntax that blocks and
/// hides; rules it can't follow (scriptlets, procedural hiding, rewrites) are left out rather than half followed.
/// Built once on a background thread, then only read.
/// </summary>
public sealed class AdFilterEngine
{
    public static readonly AdFilterEngine Empty = new();

    // Selectors hidden per rule in the stylesheet: one that the browser can't read drops only those beside it.
    private const int SelectorsPerRule = 10;

    // ||host^ rules without options, the most common kind, looked up by the request's host and the hosts above it.
    private readonly HashSet<string> _blockedHosts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blockedThirdPartyHosts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _allowedHosts = new(StringComparer.Ordinal);

    private readonly RuleIndex _block = new();
    private readonly RuleIndex _allow = new();
    private readonly RuleIndex _blockPopup = new();
    private readonly RuleIndex _allowPopup = new();

    // Sites the lists let alone: entirely (@@…$document), or just not hiding anything, or nothing generic.
    private readonly HashSet<string> _pagesAllowed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pagesNoHiding = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pagesNoGenericHiding = new(StringComparer.Ordinal);

    // Hiding: everywhere, on given sites, and selectors not to hide on given sites.
    private readonly List<string> _generic = new();
    private readonly HashSet<string> _genericSeen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _excludedEverywhere = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _specific = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _exceptions = new(StringComparer.Ordinal);
    private string? _genericCss;

    /// <summary>How many rules it follows.</summary>
    public int RuleCount { get; private set; }

    /// <summary>
    /// Reads the lists in order. A list's hiding rules are read only when <c>Hiding</c> is set, so a list can serve for
    /// blocking alone.
    /// </summary>
    public static AdFilterEngine Build(IEnumerable<(string Text, bool Hiding)> lists)
    {
        var engine = new AdFilterEngine();
        foreach (var (text, hiding) in lists)
        {
            using var reader = new StringReader(text);
            while (reader.ReadLine() is { } line) engine.Add(line.Trim(), hiding);
        }
        engine.Finish();
        return engine;
    }

    // ---- Reading the lists ----

    private void Add(string line, bool hiding)
    {
        if (line.Length == 0 || line[0] == '!' || line[0] == '[') return;
        if (TryAddHiding(line, hiding)) return;
        AddNetworkRule(line);
    }

    // domains##selector hides, domains#@#selector doesn't there; #?#, #$#, #%# and their exceptions are procedural,
    // style or script rules, which are left out. False when the line isn't a hiding rule.
    private bool TryAddHiding(string line, bool hiding)
    {
        var hash = line.IndexOf('#');
        if (hash < 0) return false;
        var domains = line[..hash];
        if (domains.IndexOfAny(['/', '|', '$', '^', '*', '?', '=']) >= 0 && !domains.Contains(".*")) return false;

        var rest = line.AsSpan(hash);
        bool exception;
        int length;
        if (rest.StartsWith("##")) (exception, length) = (false, 2);
        else if (rest.StartsWith("#@#")) (exception, length) = (true, 3);
        else if (rest.StartsWith("#?#") || rest.StartsWith("#$#") || rest.StartsWith("#%#")
                 || rest.StartsWith("#@?#") || rest.StartsWith("#@$#") || rest.StartsWith("#@%#")) return true;
        else return false;

        if (!hiding) return true;
        var selector = line[(hash + length)..].Trim();
        if (!UsableSelector(selector)) return true;

        var on = new List<string>();
        var notOn = new List<string>();
        foreach (var entry in domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry[0] == '~') notOn.Add(entry[1..].ToLowerInvariant());
            else on.Add(entry.ToLowerInvariant());
        }

        if (exception)
        {
            if (on.Count == 0) _excludedEverywhere.Add(selector);
            foreach (var domain in on) ListOf(_exceptions, domain).Add(selector);
        }
        else if (on.Count == 0)
        {
            if (_genericSeen.Add(selector)) _generic.Add(selector);
            foreach (var domain in notOn) ListOf(_exceptions, domain).Add(selector);
        }
        else
        {
            foreach (var domain in on) ListOf(_specific, domain).Add(selector);
        }
        RuleCount++;
        return true;
    }

    // Pseudo-classes only uBlock Origin or Adblock Plus understand; a browser would drop the selector (and those beside it).
    private static readonly string[] ProceduralMarkers =
    [
        ":-abp-", ":has-text(", ":contains(", ":xpath(", ":matches-css", ":upward(", ":remove(", ":style(", ":min-text-length(",
        ":watch-attr(", ":others(", ":matches-path(", ":matches-attr(", ":matches-prop(", ":if(", ":if-not(", ":nth-ancestor(",
        ":matches-media(", ":remove-attr(", ":remove-class(", ":shadow", ":-ext-",
    ];

    private static bool UsableSelector(string selector) =>
        selector.Length > 0 && selector[0] != '+' && selector[0] != '^'
        && selector.IndexOfAny(['{', '}', '\n', '\r']) < 0
        && !ProceduralMarkers.Any(m => selector.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static List<string> ListOf(Dictionary<string, List<string>> map, string key)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
        return list;
    }

    private static HashSet<string> ListOf(Dictionary<string, HashSet<string>> map, string key)
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = new HashSet<string>(StringComparer.Ordinal);
        return set;
    }

    private static readonly Regex PlainHostRule = new(@"^\|\|[a-z0-9._-]+\^?$", RegexOptions.CultureInvariant);

    private void AddNetworkRule(string line)
    {
        var exception = line.StartsWith("@@", StringComparison.Ordinal);
        var rule = exception ? line[2..] : line;

        var options = string.Empty;
        var dollar = rule.LastIndexOf('$');
        if (dollar >= 0)
        {
            options = rule[(dollar + 1)..];
            rule = rule[..dollar];
        }
        // Regular expressions are left out: slow to test against every request, and rare in the lists.
        if (rule.Length == 0 || (rule.Length > 1 && rule[0] == '/' && rule[^1] == '/')) return;

        AdRequestType include = AdRequestType.None, exclude = AdRequestType.None;
        var party = 0; // 1 third-party only, -1 first-party only
        var matchCase = false;
        bool allowPage = false, noHiding = false, noGenericHiding = false;
        string[]? domains = null;

        if (options.Length > 0)
        {
            foreach (var raw in options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var negated = raw[0] == '~';
                var name = negated ? raw[1..] : raw;
                string? value = null;
                var equals = name.IndexOf('=');
                if (equals >= 0)
                {
                    value = name[(equals + 1)..];
                    name = name[..equals];
                }

                switch (name.ToLowerInvariant())
                {
                    case "third-party" or "3p":
                        party = negated ? -1 : 1;
                        break;
                    case "first-party" or "1p":
                        party = negated ? 1 : -1;
                        break;
                    case "domain" or "from" when value != null && !negated:
                        domains = value.ToLowerInvariant().Split('|', StringSplitOptions.RemoveEmptyEntries);
                        break;
                    case "match-case":
                        matchCase = true;
                        break;
                    case "important":
                        break;
                    case "all" when !negated:
                        include |= AdRequestType.Requests | AdRequestType.Popup;
                        break;
                    case "document" or "doc" when exception && !negated:
                        allowPage = true;
                        break;
                    case "elemhide" or "ehide" when exception && !negated:
                        noHiding = true;
                        break;
                    case "generichide" or "ghide" when exception && !negated:
                        noGenericHiding = true;
                        break;
                    default:
                        var type = TypeNamed(name);
                        if (type == AdRequestType.None) return; // an option it can't follow: leave the rule out
                        if (negated) exclude |= type;
                        else include |= type;
                        break;
                }
            }
        }

        // A site the lists let alone, by its host.
        if (allowPage || noHiding || noGenericHiding)
        {
            if (HostOfPattern(rule) is not { } host) return;
            if (allowPage) _pagesAllowed.Add(host);
            if (noHiding) _pagesNoHiding.Add(host);
            if (noGenericHiding) _pagesNoGenericHiding.Add(host);
            RuleCount++;
            return;
        }

        var types = (include == AdRequestType.None ? AdRequestType.Requests : include) & ~exclude;
        if (types == AdRequestType.None) return;

        var pattern = matchCase ? rule : rule.ToLowerInvariant();
        RuleCount++;

        if (types == AdRequestType.Requests && domains == null && !matchCase && PlainHostRule.IsMatch(pattern))
        {
            var host = pattern.TrimStart('|').TrimEnd('^');
            if (party == 0)
            {
                (exception ? _allowedHosts : _blockedHosts).Add(host);
                return;
            }
            if (party == 1 && !exception)
            {
                _blockedThirdPartyHosts.Add(host);
                return;
            }
        }

        var (onDomains, notOnDomains) = SplitDomains(domains);
        if ((types & AdRequestType.Popup) != 0)
            (exception ? _allowPopup : _blockPopup).Add(new NetRule(pattern, matchCase, AdRequestType.Popup, party, onDomains, notOnDomains));
        if ((types & AdRequestType.Requests) != 0)
            (exception ? _allow : _block).Add(new NetRule(pattern, matchCase, types & AdRequestType.Requests, party, onDomains, notOnDomains));
    }

    private static (string[]? On, string[]? NotOn) SplitDomains(string[]? domains)
    {
        if (domains == null) return (null, null);
        var on = domains.Where(d => d[0] != '~').ToArray();
        var notOn = domains.Where(d => d[0] == '~').Select(d => d[1..]).ToArray();
        return (on.Length > 0 ? on : null, notOn.Length > 0 ? notOn : null);
    }

    private static AdRequestType TypeNamed(string name) => name.ToLowerInvariant() switch
    {
        "script" => AdRequestType.Script,
        "image" => AdRequestType.Image,
        "stylesheet" or "css" => AdRequestType.Stylesheet,
        "subdocument" or "frame" => AdRequestType.Subdocument,
        "xmlhttprequest" or "xhr" => AdRequestType.XmlHttpRequest,
        "media" => AdRequestType.Media,
        "font" => AdRequestType.Font,
        "object" or "object-subrequest" => AdRequestType.Object,
        "websocket" => AdRequestType.WebSocket,
        "ping" or "beacon" => AdRequestType.Ping,
        "other" => AdRequestType.Other,
        "popup" => AdRequestType.Popup,
        _ => AdRequestType.None,
    };

    // ||host^, ||host or ||host/: the host it's for.
    private static string? HostOfPattern(string pattern)
    {
        if (!pattern.StartsWith("||", StringComparison.Ordinal)) return null;
        var host = pattern[2..].TrimEnd('^', '/', '*').ToLowerInvariant();
        return host.Length > 0 && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ? host : null;
    }

    private void Finish()
    {
        if (_excludedEverywhere.Count > 0) _generic.RemoveAll(_excludedEverywhere.Contains);
        _genericCss = _generic.Count > 0 ? AppendRules(new StringBuilder(), _generic).ToString() : null;
        _genericSeen.Clear();
    }

    // ---- Using them ----

    /// <summary>
    /// Whether the page at <paramref name="pageHost"/> may not make this request, to <paramref name="url"/> on
    /// <paramref name="requestHost"/>. Both hosts in lower case.
    /// </summary>
    public bool ShouldBlock(string url, string requestHost, string pageHost, AdRequestType type)
    {
        var request = new Request(url, requestHost, pageHost, type);
        // A server listed as an ad server still serves its own site, such as an ad company's home page, its own files.
        var ownServer = requestHost == pageHost;
        var blocked = (!ownServer && HostIn(_blockedHosts, requestHost))
                      || (!ownServer && request.ThirdParty && HostIn(_blockedThirdPartyHosts, requestHost))
                      || _block.Matches(request);
        return blocked && !HostIn(_allowedHosts, requestHost) && !_allow.Matches(request);
    }

    /// <summary>Whether the page at <paramref name="pageHost"/> may not open a pop-up window at <paramref name="url"/>.</summary>
    public bool ShouldBlockPopup(string url, string popupHost, string pageHost)
    {
        var request = new Request(url, popupHost, pageHost, AdRequestType.Popup);
        return _blockPopup.Matches(request) && !_allowPopup.Matches(request);
    }

    /// <summary>The filter lists let this site alone, as one that breaks when anything is blocked on it.</summary>
    public bool AllowsPage(string pageHost) => HostIn(_pagesAllowed, pageHost);

    /// <summary>The CSS that hides the page's ads, or null when nothing on it is to be hidden.</summary>
    public string? HidingCss(string pageHost)
    {
        if (pageHost.Length == 0 || HostIn(_pagesNoHiding, pageHost)) return null;
        var generic = !HostIn(_pagesNoGenericHiding, pageHost);

        HashSet<string>? excepted = null;
        List<string>? specific = null;
        foreach (var key in KeysFor(pageHost))
        {
            if (_exceptions.TryGetValue(key, out var notHere)) (excepted ??= new HashSet<string>(StringComparer.Ordinal)).UnionWith(notHere);
            if (_specific.TryGetValue(key, out var here)) (specific ??= new List<string>()).AddRange(here);
        }

        if (excepted == null && specific == null) return generic ? _genericCss : null;

        var css = new StringBuilder();
        if (generic && _genericCss != null)
        {
            if (excepted == null) css.Append(_genericCss);
            else AppendRules(css, _generic.Where(s => !excepted.Contains(s)));
        }
        if (specific != null) AppendRules(css, specific.Distinct().Where(s => excepted?.Contains(s) != true));
        return css.Length > 0 ? css.ToString() : null;
    }

    private static StringBuilder AppendRules(StringBuilder css, IEnumerable<string> selectors)
    {
        foreach (var chunk in selectors.Chunk(SelectorsPerRule))
        {
            css.AppendJoin(',', chunk);
            css.Append("{display:none!important}\n");
        }
        return css;
    }

    // The host and every host above it, then the site as an entity (example.* for www.example.co.uk), as hiding rules name them.
    private static IEnumerable<string> KeysFor(string host)
    {
        for (var h = host; ; h = h[(h.IndexOf('.') + 1)..])
        {
            yield return h;
            if (!h.Contains('.')) break;
        }
        var site = SiteOf(host);
        var dot = site.IndexOf('.');
        if (dot > 0) yield return site[..dot] + ".*";
    }

    private static bool HostIn(HashSet<string> hosts, string host)
    {
        if (hosts.Count == 0 || host.Length == 0) return false;
        for (var h = host; ; h = h[(h.IndexOf('.') + 1)..])
        {
            if (hosts.Contains(h)) return true;
            if (!h.Contains('.')) return false;
        }
    }

    /// <summary>A domain= entry: the host itself, one under it, or for example.*, example under any ending.</summary>
    private static bool HostMatches(string host, string domain)
    {
        if (domain.EndsWith(".*", StringComparison.Ordinal))
        {
            var site = SiteOf(host);
            var name = domain[..^2];
            return site.StartsWith(name + ".", StringComparison.Ordinal) || ("." + host).Contains("." + name + ".", StringComparison.Ordinal);
        }
        return host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
    }

    // Second-level names under which a country's sites are registered, such as co.uk and com.au.
    private static readonly HashSet<string> CountrySecondLevels = new(StringComparer.Ordinal)
    {
        "co", "com", "net", "org", "gov", "edu", "ac", "ne", "or", "go", "gob", "nic", "mil", "ltd", "plc", "sch", "nom", "info", "biz",
    };

    /// <summary>
    /// The site a host belongs to, as a registrar would have sold it: example.com for www.example.com, example.co.uk for
    /// shop.example.co.uk. Without the whole public suffix list it's a close guess, enough to tell a page's own requests
    /// from other sites'.
    /// </summary>
    public static string SiteOf(string host)
    {
        if (host.Length == 0 || IPAddress.TryParse(host.Trim('[', ']'), out _)) return host;
        var labels = host.Split('.');
        if (labels.Length <= 2) return host;
        var take = labels[^1].Length == 2 && CountrySecondLevels.Contains(labels[^2]) ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }

    /// <summary>A request being checked, with what's worked out from it worked out once.</summary>
    private sealed class Request(string url, string host, string pageHost, AdRequestType type)
    {
        private string? _lower;
        private List<string>? _tokens;

        public string Url { get; } = url;
        public string Lower => _lower ??= Url.ToLowerInvariant();
        public string Host { get; } = host;
        public string PageHost { get; } = pageHost;
        public AdRequestType Type { get; } = type;
        public bool ThirdParty { get; } = pageHost.Length == 0 || SiteOf(host) != SiteOf(pageHost);

        public List<string> Tokens => _tokens ??= RuleIndex.TokensOf(Lower);
    }

    /// <summary>
    /// A filter that isn't just a host: a pattern for the address (* any characters, ^ a separator, | an end, || a host
    /// and the ones under it), with what it's for. Patterns without wildcards are found with a plain search.
    /// </summary>
    private sealed class NetRule
    {
        private readonly string _pattern;
        private readonly bool _matchCase;
        private readonly AdRequestType _types;
        private readonly int _party;
        private readonly string[]? _on;
        private readonly string[]? _notOn;
        private readonly string? _plain;
        private Regex? _regex;

        public NetRule(string pattern, bool matchCase, AdRequestType types, int party, string[]? on, string[]? notOn)
        {
            _pattern = pattern;
            _matchCase = matchCase;
            _types = types;
            _party = party;
            _on = on;
            _notOn = notOn;
            if (pattern.IndexOfAny(['*', '^', '|']) < 0) _plain = pattern;
        }

        public string Pattern => _pattern;

        public bool Matches(Request request)
        {
            if ((_types & request.Type) == 0) return false;
            if (_party == 1 && !request.ThirdParty) return false;
            if (_party == -1 && request.ThirdParty) return false;
            if (_on != null && !_on.Any(d => HostMatches(request.PageHost, d))) return false;
            if (_notOn != null && _notOn.Any(d => HostMatches(request.PageHost, d))) return false;

            var url = _matchCase ? request.Url : request.Lower;
            if (_plain != null) return url.Contains(_plain, StringComparison.Ordinal);
            try
            {
                return (_regex ??= new Regex(ToRegex(_pattern), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))).IsMatch(url);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private static string ToRegex(string pattern)
        {
            var regex = new StringBuilder();
            int start = 0, end = pattern.Length;
            if (pattern.StartsWith("||", StringComparison.Ordinal))
            {
                regex.Append(@"^[a-z][a-z0-9+.\-]*:/+(?:[^/?#]*\.)?");
                start = 2;
            }
            else if (pattern.StartsWith('|'))
            {
                regex.Append('^');
                start = 1;
            }
            var anchoredEnd = end > start && pattern[end - 1] == '|';
            if (anchoredEnd) end--;

            for (var i = start; i < end; i++)
            {
                var c = pattern[i];
                switch (c)
                {
                    case '*':
                        regex.Append(".*");
                        break;
                    case '^':
                        regex.Append(@"(?:[^\w.%\-]|$)");
                        break;
                    default:
                        if (@"\.+?()[]{}$|#".Contains(c)) regex.Append('\\');
                        regex.Append(c);
                        break;
                }
            }
            if (anchoredEnd) regex.Append('$');
            return regex.ToString();
        }
    }

    /// <summary>
    /// Pattern filters, each filed under a word of its pattern that a matching address has as a whole word, as Adblock
    /// Plus does: a request is tested only against the filters filed under its address' words.
    /// </summary>
    private sealed class RuleIndex
    {
        private readonly Dictionary<string, List<NetRule>> _byToken = new(StringComparer.Ordinal);
        private readonly List<NetRule> _untokened = new();

        public void Add(NetRule rule)
        {
            var best = default(string);
            var bestCount = int.MaxValue;
            foreach (var token in CandidateTokens(rule.Pattern))
            {
                var count = _byToken.TryGetValue(token, out var list) ? list.Count : 0;
                if (count < bestCount || (count == bestCount && token.Length > best!.Length))
                {
                    best = token;
                    bestCount = count;
                }
            }
            if (best == null) _untokened.Add(rule);
            else if (_byToken.TryGetValue(best, out var rules)) rules.Add(rule);
            else _byToken[best] = [rule];
        }

        public bool Matches(Request request)
        {
            if (_byToken.Count == 0 && _untokened.Count == 0) return false;
            foreach (var token in request.Tokens)
            {
                if (!_byToken.TryGetValue(token, out var rules)) continue;
                foreach (var rule in rules)
                    if (rule.Matches(request)) return true;
            }
            foreach (var rule in _untokened)
                if (rule.Matches(request)) return true;
            return false;
        }

        private static bool IsWordChar(char c) => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '%';

        // Words of the pattern that can only be whole words of an address it matches: not running into a *, and at the
        // pattern's ends only where it's anchored there.
        private static IEnumerable<string> CandidateTokens(string pattern)
        {
            var lower = pattern.ToLowerInvariant();
            var start = 0;
            var end = lower.Length;
            var anchoredStart = false;
            if (lower.StartsWith("||", StringComparison.Ordinal)) (start, anchoredStart) = (2, true);
            else if (lower.StartsWith('|')) (start, anchoredStart) = (1, true);
            var anchoredEnd = end > start && lower[end - 1] == '|';
            if (anchoredEnd) end--;

            for (var i = start; i < end;)
            {
                if (!IsWordChar(lower[i]))
                {
                    i++;
                    continue;
                }
                var j = i;
                while (j < end && IsWordChar(lower[j])) j++;
                var boundedBefore = i > start ? lower[i - 1] != '*' : anchoredStart;
                var boundedAfter = j < end ? lower[j] != '*' : anchoredEnd;
                if (boundedBefore && boundedAfter && j - i >= 2) yield return lower[i..j];
                i = j;
            }
        }

        public static List<string> TokensOf(string url)
        {
            var tokens = new List<string>();
            for (var i = 0; i < url.Length;)
            {
                if (!IsWordChar(url[i]))
                {
                    i++;
                    continue;
                }
                var j = i;
                while (j < url.Length && IsWordChar(url[j])) j++;
                if (j - i >= 2) tokens.Add(url[i..j]);
                i = j;
            }
            return tokens;
        }
    }
}
