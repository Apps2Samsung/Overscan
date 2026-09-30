using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace Overscan
{
    /// <summary>
    /// Tells the engine which proxy to use, or none. Issue #97.
    /// </summary>
    /// <remarks>
    /// The engine has one proxy for the whole process (<c>WebContext.ProxyUrl</c>,
    /// which is <c>ewk_context_proxy_uri_set</c> underneath), not one per site.
    /// It does have a bypass rule, but that is a list of hosts that skip the proxy,
    /// and "only these sites" cannot be written as one. So "a proxy for this site"
    /// is done by switching the one proxy at the site boundary, the same place the
    /// per-site identity and images switches go on the view, and while a proxied
    /// site is open everything it pulls in (its CDNs, its player, its sign-in) goes
    /// through the proxy too. For a region block that is the point: the video host
    /// is usually the part that checks.
    ///
    /// Two things this cannot know from here, and the report is built to answer:
    /// whether a change reaches requests while the process is running rather than
    /// only at the next start, and whether an empty string takes the engine back to
    /// direct. Chromium's own proxy grammar reads an empty string as "no proxy",
    /// but nothing in the toolkit says the string reaches that grammar unchanged.
    ///
    /// Main thread only, like every other call on the context.
    /// </remarks>
    internal static class NuiProxy
    {
        /// <summary>What the engine was last told and what it read back, for the report.</summary>
        public static string LastResult = "(never told, the engine's own default)";

        /// <summary>True once the engine has been told anything in this process.</summary>
        public static bool EverSet { get; private set; }

        /// <summary>
        /// Points the engine at <paramref name="address"/>, or back to direct for
        /// null. Best-effort: false, with the reason in <see cref="LastResult"/>,
        /// when the engine would not take it.
        /// </summary>
        public static bool Apply(WebView web, ProxyAddress address)
        {
            string wanted = address == null ? string.Empty : address.EngineUri;
            string shown = address == null ? "direct" : address.EngineUri;
            try
            {
                WebContext context = web == null ? null : web.Context;
                if (context == null)
                {
                    LastResult = "engine offered no context, still " + (EverSet ? "on the last proxy" : "direct");
                    return false;
                }

                Breadcrumbs.Drop("proxy: telling the engine " + shown);
                context.ProxyUrl = wanted;
                EverSet = true;

                // Sent every time, empty when there is no login, so an address
                // changed from one with a login to one without does not keep
                // answering the new proxy with the old proxy's password.
                if (address != null)
                {
                    Breadcrumbs.Drop("proxy: setting the login (" + (address.HasLogin ? "one" : "none") + ")");
                    context.SetDefaultProxyAuth(address.User ?? string.Empty, address.Password ?? string.Empty);
                }

                string readBack = context.ProxyUrl;
                LastResult = "told " + shown + ", engine reads back \"" + (readBack ?? "(null)") + "\"" +
                             (address != null && address.HasLogin ? ", login sent" : string.Empty);
                Breadcrumbs.Drop("proxy: " + LastResult);
                return true;
            }
            catch (Exception ex)
            {
                LastResult = "setting " + shown + " failed: " + ex.GetType().Name + ": " + ex.Message;
                Breadcrumbs.Drop("proxy: " + LastResult);
                return false;
            }
        }
    }
}
