using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
                "AI-Assistant/1.0 UnityDocumentationReader"
            );
        }

        public string SearchUnityDocs(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return "UNITY DOCS ERROR: query is empty.";
            }

            try
            {
                string searchQuery =
                    "site:docs.unity3d.com " + query.Trim();

                string url =
                    "https://html.duckduckgo.com/html/?q=" +
                    Uri.EscapeDataString(searchQuery);

                string html =
                    client.GetStringAsync(url)
                        .GetAwaiter()
                        .GetResult();

                MatchCollection matches =
                    Regex.Matches(
                        html,
                        "<a[^>]+class=\"[^\"]*result__a[^\"]*\"[^>]+href=\"(?<url>[^\"]+)\"[^>]*>(?<title>.*?)</a>",
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
                        ResolveDuckDuckGoUrl(candidate);

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

                if (results.Count == 0)
                {
                    return
                        "UNITY DOCS SEARCH: No official docs.unity3d.com results found for: " +
                        query;
                }

                return
                    "UNITY DOCS SEARCH RESULTS\n" +
                    string.Join("\n\n", results);
            }
            catch (Exception ex)
            {
                return
                    "UNITY DOCS SEARCH ERROR: " +
                    ex.GetType().Name +
                    ": " +
                    ex.Message;
            }
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
                string html =
                    client.GetStringAsync(url)
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

        private static string ResolveDuckDuckGoUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            {
                return value;
            }

            if (
                !uri.Host.Contains(
                    "duckduckgo.com",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return value;
            }

            string query = uri.Query.TrimStart('?');

            foreach (string pair in query.Split('&'))
            {
                string[] parts = pair.Split('=', 2);

                if (
                    parts.Length == 2 &&
                    parts[0].Equals("uddg", StringComparison.OrdinalIgnoreCase)
                )
                {
                    return Uri.UnescapeDataString(parts[1]);
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
