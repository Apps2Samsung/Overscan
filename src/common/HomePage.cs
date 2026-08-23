using System.Collections.Generic;
using System.Text;

namespace Overscan
{
    /// <summary>
    /// The start screen, generated as a page and loaded into the web view.
    ///
    /// Building it as HTML rather than native widgets means one implementation for
    /// both UI stacks, the D-pad pointer works on it exactly like any other page,
    /// and tiles are plain links — so opening one needs no URL interception. It is
    /// also the only place in the app where we get to use CSS.
    ///
    /// Everything is inline: an old engine, no network, no assets.
    /// </summary>
    internal static class HomePage
    {
        /// <summary>Marks the generated page so history does not record it.</summary>
        public const string BaseUrl = "https://overscan.start/";

        public static string Build(IList<Bookmark> favourites, IList<Bookmark> history, string searchUrl)
        {
            var html = new StringBuilder();
            html.Append(@"<!doctype html><html><head><meta charset='utf-8'>
<title>Overscan</title><style>
  *{box-sizing:border-box}
  body{margin:0;padding:56px 64px;background:#0b0d12;color:#eaeef6;
       font-family:'Samsung One','Helvetica Neue',Arial,sans-serif;-webkit-font-smoothing:antialiased}
  h1{margin:0 0 4px;font-size:44px;font-weight:700;letter-spacing:-0.5px}
  h1 span{color:#4894ff}
  p.sub{margin:0 0 44px;color:#98a0b4;font-size:22px}
  h2{margin:40px 0 18px;font-size:20px;font-weight:600;color:#4894ff;
     text-transform:uppercase;letter-spacing:1.6px}
  .grid{display:flex;flex-wrap:wrap;margin:-10px}
  a.tile{display:block;width:calc(25% - 20px);margin:10px;padding:22px 24px;
         background:#171a22;border:2px solid #2b303c;border-radius:14px;
         text-decoration:none;color:#eaeef6;transition:all 120ms ease-out}
  a.tile:hover{background:#1f2431;border-color:#4894ff;transform:translateY(-2px)}
  a.tile .host{display:block;font-size:26px;font-weight:600;overflow:hidden;
                white-space:nowrap;text-overflow:ellipsis}
  a.tile .name{display:block;margin-top:6px;font-size:18px;color:#98a0b4;
                overflow:hidden;white-space:nowrap;text-overflow:ellipsis}
  .empty{color:#6b7385;font-size:20px;padding:8px 2px 0}
  .hint{margin-top:56px;padding-top:24px;border-top:2px solid #20242e;
        color:#6b7385;font-size:19px;line-height:1.7}
  .hint b{color:#98a0b4;font-weight:600}
</style></head><body>
<h1>Over<span>scan</span></h1>
<p class='sub'>The web, on the big screen.</p>
");

            html.Append("<h2>Favourites</h2>");
            if (favourites.Count == 0)
            {
                html.Append("<div class='empty'>Press <b>8</b> on any page to keep it here.</div>");
            }
            else
            {
                AppendGrid(html, favourites, 12);
            }

            if (history.Count > 0)
            {
                html.Append("<h2>Recent</h2>");
                AppendGrid(html, history, 8);
            }

            html.Append(@"<div class='hint'>
<b>0</b> type an address &nbsp;·&nbsp; <b>8</b> keep this page &nbsp;·&nbsp;
<b>9</b> back to this screen &nbsp;·&nbsp; <b>7</b> all keys<br/>
Move the pointer with the D-pad, press OK to click.<br/>
On the keyboard: <b>shift</b> for capitals, <b>sym</b> for punctuation
(<b>@</b> is on the bottom row), and <b>start</b> to make what you typed the page
this browser opens on — press <b>start</b> with nothing typed to get this
screen back.<br/>
Slow set? Press <b>Info</b> to load pages without images, or <b>1</b> for the
lighter mobile version of a site. Some sites have a dedicated TV interface —
<b>youtube.com/tv</b> is far faster than the desktop one.
</div></body></html>");
            return html.ToString();
        }

        private static void AppendGrid(StringBuilder html, IList<Bookmark> items, int limit)
        {
            html.Append("<div class='grid'>");
            for (int i = 0; i < items.Count && i < limit; i++)
            {
                Bookmark item = items[i];
                html.Append("<a class='tile' href='").Append(Escape(item.Url)).Append("'>")
                    .Append("<span class='host'>").Append(Escape(HostOf(item.Url))).Append("</span>")
                    .Append("<span class='name'>").Append(Escape(item.Title)).Append("</span>")
                    .Append("</a>");
            }

            html.Append("</div>");
        }

        private static string HostOf(string url)
        {
            string rest = url ?? string.Empty;
            int scheme = rest.IndexOf("://", System.StringComparison.Ordinal);
            if (scheme > 0)
            {
                rest = rest.Substring(scheme + 3);
            }

            int slash = rest.IndexOf('/');
            if (slash > 0)
            {
                rest = rest.Substring(0, slash);
            }

            return rest.StartsWith("www.", System.StringComparison.Ordinal) ? rest.Substring(4) : rest;
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("'", "&#39;").Replace("\"", "&quot;");
        }
    }
}
