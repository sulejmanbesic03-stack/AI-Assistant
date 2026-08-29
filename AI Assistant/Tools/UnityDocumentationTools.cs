using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace AI_Assistant.Tools
{
    public sealed class UnityDocumentationTools
    {
        private const int MaxSearchResults = 5;
        private const int MaxDocumentChars = 12000;

        private readonly HttpClient client;

        public UnityDocumentationTools()
        {
            client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/130.0 Safari/537.36"
            );

            client.DefaultRequestHeaders.Accept.ParseAdd(
                "text/html,application/xhtml+xml"
            );
        }

        public string SearchUnityDocs(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return "UNITY DOCS ERROR: query is empty.";
            }

            string searchQuery =
                "site:docs.unity3d.com " + query.Trim();

            List<string> providerErrors =
                new List<string>();

            foreach (
                (string Name, string Url, string Pattern) provider
                in GetSearchProviders(searchQuery)
            )
            {
                try
                {
                    string html =
                        client.GetStringAsync(provider.Url)
                            .GetAwaiter()
                            .GetResult();

                    List<string> results =
                        ParseSearchResults(
                            html,
                            provider.Pattern
                        );

                    if (results.Count > 0)
                    {
                        return
                            "UNITY DOCS SEARCH RESULTS\n" +
                            "Provider: " + provider.Name + "\n\n" +
                            string.Join("\n\n", results);
                    }

                    providerErrors.Add(
                        provider.Name + ": no official Unity results parsed"
                    );
                }
                catch (Exception ex)
                {
                    providerErrors.Add(
                        provider.Name + ": " +
                        ex.GetType().Name +
                        " - " +
                        ex.Message
                    );
                }
            }

            return
                "UNITY DOCS SEARCH ERROR: all search providers failed.\n" +
                string.Join("\n", providerErrors) +
                "\nYou may still call read_unity_doc with a known official docs.unity3d.com URL.";
        }

        public string ReadUnityDoc(string url)
        {
            if (!IsAllowedUnityDocsUrl(url))
            {
                return
                    "UNITY DOCS DENIED: only https://docs.unity3d.com/... URLs are allowed.";
            }

            try
            {
                using HttpResponseMessage response =
                    client.GetAsync(url)
                        .GetAwaiter()
                        .GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    return
                        "UNITY DOCS READ ERROR: HTTP " +
                        (int)response.StatusCode +
                        " " + response.ReasonPhrase +
                        " for " + url;
                }

                string html =
                    response.Content.ReadAsStringAsync()
                        .GetAwaiter()
                        .GetResult();

                string title =
                    ExtractTitle(html);

                string text =
                    ExtractReadableText(html);

                if (text.Length > MaxDocumentChars)
                {
                    text =
                        text.Substring(0, MaxDocumentChars) +
                        "\n...[document truncated]";
                }

                return
                    "UNITY DOCUMENTATION\n" +
                    "Title: " + title + "\n" +
                    "URL: " + url + "\n\n" +
                    text;
            }
            catch (Exception ex)
            {
                return
                    "UNITY DOCS READ ERROR: " +
                    ex.GetType().Name +
                    ": " +
                    ex.Message;
            }
        }

        private static IEnumerable<(string Name, string Url, string Pattern)> GetSearchProviders(
            string searchQuery
        )
        {
            string encoded =
                Uri.EscapeDataString(searchQuery);

            // Bing is the primary provider. DuckDuckGo Lite is retained only
            // as a fallback because its HTML endpoint can return HTTP 403.
            yield return (
                "Bing",
                "https://www.bing.com/search?count=10&q=" + encoded,
                "<li[^>]+class=\"b_algo\"[\\s\\S]*?<h2[^>]*>\\s*<a[^>]+href=\"(?<url>https?://[^\"]+)\"[^>]*>(?<title>[\\s\\S]*?)</a>"
            );

            yield return (
                "DuckDuckGo Lite",
                "https://lite.duckduckgo.com/lite/?q=" + encoded,
                "<a[^>]+href=\"(?<url>[^\"]+)\"[^>]*>(?<title>[\\s\\S]*?)</a>"
            );
        }

        private static List<string> ParseSearchResults(
            string html,
            string pattern
        )
        {
            MatchCollection matches =
                Regex.Matches(
                    html,
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.Singleline
                );

            List<string> results =
                new List<string>();

            HashSet<string> seen =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in matches)
            {
                string candidate =
                    WebUtility.HtmlDecode(
                        match.Groups["url"].Value
                    );

                string resolvedUrl =
                    ResolveRedirectUrl(candidate);

                if (!IsAllowedUnityDocsUrl(resolvedUrl))
                {
                    continue;
                }

                if (!seen.Add(resolvedUrl))
                {
                    continue;
                }

                string title =
                    CleanText(
                        match.Groups["title"].Value
                    );

                results.Add(
                    $"{results.Count + 1}. {title}\n{resolvedUrl}"
                );

                if (results.Count >= MaxSearchResults)
                {
                    break;
                }
            }

            return results;
        }

        private static bool IsAllowedUnityDocsUrl(string? value)
        {
            if (
                string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            )
            {
                return false;
            }

            return
                uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
                uri.Host.Equals("docs.unity3d.com", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveRedirectUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            {
                return value;
            }

            string query = uri.Query.TrimStart('?');

            foreach (string pair in query.Split('&'))
            {
                string[] parts = pair.Split('=', 2);

                if (parts.Length != 2)
                {
                    continue;
                }

                if (
                    parts[0].Equals("uddg", StringComparison.OrdinalIgnoreCase) ||
                    parts[0].Equals("u", StringComparison.OrdinalIgnoreCase) ||
                    parts[0].Equals("url", StringComparison.OrdinalIgnoreCase)
                )
                {
                    string decoded =
                        Uri.UnescapeDataString(parts[1]);

                    if (IsAllowedUnityDocsUrl(decoded))
                    {
                        return decoded;
                    }
                }
            }

            return value;
        }

        private static string ExtractTitle(string html)
        {
            Match match =
                Regex.Match(
                    html,
                    "<title[^>]*>(?<value>.*?)</title>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline
                );

            return match.Success
                ? CleanText(match.Groups["value"].Value)
                : "Unity Documentation";
        }

        private static string ExtractReadableText(string html)
        {
            string value =
                Regex.Replace(
                    html,
                    "<script[\\s\\S]*?</script>",
                    " ",
                    RegexOptions.IgnoreCase
                );

            value =
                Regex.Replace(
                    value,
                    "<style[\\s\\S]*?</style>",
                    " ",
                    RegexOptions.IgnoreCase
                );

            value =
                Regex.Replace(
                    value,
                    "<(br|/p|/div|/li|/tr|/h[1-6])[^>]*>",
                    "\n",
                    RegexOptions.IgnoreCase
                );

            value =
                Regex.Replace(
                    value,
                    "<[^>]+>",
                    " "
                );

            value =
                WebUtility.HtmlDecode(value);

            value =
                Regex.Replace(
                    value,
                    "[ \\t]+",
                    " "
                );

            value =
                Regex.Replace(
                    value,
                    "\\n\\s*\\n+",
                    "\n\n"
                );

            return value.Trim();
        }

        private static string CleanText(string value)
        {
            string withoutTags =
                Regex.Replace(value, "<[^>]+>", " ");

            string decoded =
                WebUtility.HtmlDecode(withoutTags);

            return
                Regex.Replace(decoded, "\\s+", " ")
                    .Trim();
        }
    }
}
