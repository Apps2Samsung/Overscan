namespace Overscan
{
    /// <summary>
    /// JavaScript injected into every page. Chromium-EFL exposes no API to feed
    /// synthetic mouse events into a view (only ewk's real input path and
    /// FeedMouseWheel in the NUI binding), so the D-pad cursor is implemented
    /// inside the page: we position an overlay element and dispatch the
    /// mouse/pointer events ourselves. Anchors and buttons activate normally,
    /// because a dispatched click still runs the element's default action.
    /// </summary>
    internal static class PageScript
    {
        public const string Namespace = "__ovs";

        /// <summary>Separator used by the probe payload (never appears in a UA).</summary>
        public const char FieldSeparator = '\u0001';

        /// <summary>
        /// Installs the cursor + helper API. Idempotent, so it is safe to re-run on
        /// every load-finished and after in-page navigation.
        /// </summary>
        public static string Install(string bridgeName)
        {
            return Raw.Replace("__BRIDGE__", bridgeName);
        }

        /// <summary>
        /// Reports what the page actually sees, over the JS message bridge. This is
        /// the measurement that proves the UA override took effect.
        /// </summary>
        public static string Probe(string bridgeName)
        {
            return @"(function(){try{
  var p=[navigator.userAgent,String(window.innerWidth),String(window.innerHeight),
         String(window.devicePixelRatio),document.title||'',location.href];
  window.__BRIDGE__.postMessage('probe'+'\u0001'+p.join('\u0001'));
}catch(e){}})();".Replace("__BRIDGE__", bridgeName);
        }

        private const string Raw = @"
(function(){
  if (window.__ovs) { window.__ovs.install(); return; }

  var st = { x: 0, y: 0, over: null };

  function clamp(v, hi) { return v < 0 ? 0 : (v > hi ? hi : v); }

  function makeCursor() {
    var c = document.createElement('div');
    c.id = '__ovs_cursor';
    c.style.cssText =
      'position:fixed;left:0;top:0;width:22px;height:22px;margin:-11px 0 0 -11px;' +
      'border-radius:50%;background:rgba(255,255,255,0.92);' +
      'border:3px solid rgba(0,0,0,0.85);box-shadow:0 0 6px rgba(0,0,0,0.6);' +
      'z-index:2147483647;pointer-events:none;transition:transform 60ms linear;';
    return c;
  }

  function root() { return document.body || document.documentElement; }

  function place() {
    if (!st.el || !st.el.parentNode) { return; }
    st.el.style.transform = 'translate(' + st.x + 'px,' + st.y + 'px)';
  }

  function fire(type, target) {
    if (!target) { return; }
    var e;
    var init = { bubbles: true, cancelable: true, view: window,
                 clientX: st.x, clientY: st.y, button: 0, buttons: type === 'mousedown' ? 1 : 0 };
    try {
      e = new MouseEvent(type, init);
    } catch (_) {
      e = document.createEvent('MouseEvents');
      e.initMouseEvent(type, true, true, window, type === 'click' ? 1 : 0,
                       0, 0, st.x, st.y, false, false, false, false, 0, null);
    }
    try { target.dispatchEvent(e); } catch (_) {}
  }

  function at() {
    try { return document.elementFromPoint(st.x, st.y); } catch (_) { return null; }
  }

  var CLICKABLE = 'a,button,input,select,textarea,summary,label,' +
                  '[role=button],[role=link],[role=menuitem],[role=tab],[onclick],[tabindex]';

  /* Nearest ancestor (or self) that actually responds to a click. */
  function interactive(node) {
    try {
      if (node.closest) { return node.closest(CLICKABLE); }
    } catch (_) {}

    /* Fallback for engines without closest(), and for SVG nodes on old ones. */
    var n = node;
    while (n && n !== document.body) {
      try {
        if (n.matches && n.matches(CLICKABLE)) { return n; }
      } catch (_) {}
      n = n.parentElement || n.parentNode;
      if (n && n.nodeType !== 1) { return null; }
    }
    return null;
  }

  function scrollableAncestor(node) {
    while (node && node !== document.body && node !== document.documentElement) {
      var s;
      try { s = window.getComputedStyle(node); } catch (_) { s = null; }
      if (s && (s.overflowY === 'auto' || s.overflowY === 'scroll') &&
          node.scrollHeight > node.clientHeight + 4) {
        return node;
      }
      node = node.parentElement;
    }
    return null;
  }

  window.__ovs = {
    install: function () {
      if (!st.el) { st.el = makeCursor(); }
      if (!st.el.parentNode && root()) { root().appendChild(st.el); }
      if (!st.x && !st.y) { st.x = window.innerWidth / 2; st.y = window.innerHeight / 2; }
      st.el.style.display = 'block';
      place();
      return 1;
    },

    hide: function () { if (st.el) { st.el.style.display = 'none'; } },

    /* fx, fy are fractions of the viewport, so the native side never needs to
       know the page's CSS pixel size or zoom level. */
    move: function (fx, fy) {
      st.x = clamp(fx * window.innerWidth, window.innerWidth - 1);
      st.y = clamp(fy * window.innerHeight, window.innerHeight - 1);
      place();
      var t = at();
      if (t !== st.over) {
        if (st.over) { fire('mouseout', st.over); fire('mouseleave', st.over); }
        if (t) { fire('mouseover', t); fire('mouseenter', t); }
        st.over = t;
      }
      fire('mousemove', t);
    },

    click: function () {
      var hit = at();
      if (!hit) { return 'nothing under cursor'; }

      /* elementFromPoint returns the topmost node, which on icon buttons is a
         decorative <svg>/<path>/<span> whose parent carries the behaviour.
         Dispatching on that child does nothing, so climb to the real target. */
      var t = interactive(hit) || hit;

      fire('mousedown', t);
      fire('mouseup', t);
      fire('click', t);

      /* A text field is deliberately NOT focused here — focusing raises the TV's
         IME. It is marked instead: outlined so the user can see where text will
         land, and remembered so the on-screen grid types into it. */
      var isField = t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable;
      if (isField) {
        if (st.field && st.field !== t) {
          try { st.field.style.outline = st.fieldOutline || ''; } catch (_) {}
        }
        st.field = t;
        try {
          st.fieldOutline = t.style.outline || '';
          t.style.outline = '3px solid #48a0ff';
        } catch (_) {}
      }

      var d = (isField ? 'FIELD:' : '') + (t.tagName || '?');
      if (t.id) { d += '#' + t.id; }
      if (t !== hit) { d += ' (from ' + (hit.tagName || '?') + ')'; }
      if (t.href) { d += ' -> ' + t.href; }
      return d;
    },

    /* Scrolls the innermost scrollable element under the cursor, falling back to
       the document. Without this, sites that scroll a div (most SPAs) ignore us. */
    scroll: function (dx, dy) {
      var target = scrollableAncestor(at());
      if (target) { target.scrollTop += dy; target.scrollLeft += dx; return 'el'; }
      window.scrollBy(dx, dy);
      return 'doc';
    },

    page: function (dir) {
      return window.__ovs.scroll(0, dir * Math.round(window.innerHeight * 0.85));
    },

    /* ewk on an older TV lays the page out at a width of its own choosing and
       paints it into the view, which stretches everything. Forcing the layout
       width to the view's real pixel width is the only lever available on 5.0
       (WebView.SetScale and the zoom settings are API 6+). */
    setViewport: function (px) {
      try {
        var m = document.querySelector('meta[name=viewport]');
        if (!m) {
          m = document.createElement('meta');
          m.setAttribute('name', 'viewport');
          (document.head || document.documentElement).appendChild(m);
        }
        if (m.getAttribute('data-ovs-original') === null) {
          m.setAttribute('data-ovs-original', m.getAttribute('content') || '');
        }
        m.setAttribute('content', 'width=' + px + ', initial-scale=1');
        return 'viewport=' + px;
      } catch (e) { return 'viewport failed: ' + e; }
    },

    clearViewport: function () {
      try {
        var m = document.querySelector('meta[name=viewport]');
        if (!m) { return 'no meta'; }
        var original = m.getAttribute('data-ovs-original');
        if (original) { m.setAttribute('content', original); } else { m.removeAttribute('content'); }
        return 'viewport restored';
      } catch (e) { return 'restore failed: ' + e; }
    },

    metrics: function () {
      var d = document.documentElement;
      return [window.innerWidth + 'x' + window.innerHeight,
              'client ' + d.clientWidth + 'x' + d.clientHeight,
              'outer ' + window.outerWidth + 'x' + window.outerHeight,
              'screen ' + screen.width + 'x' + screen.height,
              'dpr ' + window.devicePixelRatio].join(', ');
    },

    /* The field the on-screen keyboard is typing into: whatever the cursor last
       focused, or the page's own search box as a fallback. */
    field: function () {
      if (st.field && st.field.parentNode) { return st.field; }
      var el = document.activeElement;
      if (el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.isContentEditable)) {
        return el;
      }
      return document.querySelector('input[type=search],input[type=text],input:not([type])');
    },

    /* Sets a field's value so frameworks notice. Assigning .value directly is
       invisible to React and friends, which track the native setter. */
    type: function (text) {
      var el = window.__ovs.field();
      if (!el) { return 'no field'; }
      /* Deliberately NOT calling focus(): that is what raises the platform IME.
         Values are set programmatically, so the field does not need focus. */

      if (el.isContentEditable) {
        el.textContent = text;
      } else {
        var set = null;
        try {
          var proto = Object.getPrototypeOf(el);
          var desc = Object.getOwnPropertyDescriptor(proto, 'value');
          if (desc && desc.set) { set = desc.set; }
        } catch (_) {}
        if (set) { set.call(el, text); } else { el.value = text; }
      }

      try {
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
      } catch (_) {}

      try { if (el.blur) { el.blur(); } } catch (_) {}

      return (el.tagName || '?') + (el.id ? '#' + el.id : '');
    },

    /* Enter on the focused field: real key events first (search-as-you-type UIs
       listen for them), then the owning form as a fallback. */
    submit: function () {
      var el = window.__ovs.field();
      if (!el) { return 'no field'; }

      ['keydown', 'keypress', 'keyup'].forEach(function (type) {
        try {
          el.dispatchEvent(new KeyboardEvent(type, {
            bubbles: true, cancelable: true, key: 'Enter', code: 'Enter', keyCode: 13, which: 13
          }));
        } catch (_) {}
      });

      try {
        if (el.form) {
          if (el.form.requestSubmit) { el.form.requestSubmit(); } else { el.form.submit(); }
          return 'form submitted';
        }
      } catch (_) {}

      return 'enter sent';
    }
  };

  /* A page that focuses a text field (DuckDuckGo autofocuses its search box)
     makes ewk raise the TV's on-screen keyboard, which then swallows the remote.
     Blur anything the page focuses unless we asked for it. */
  window.__ovs.allowFocus = false;
  /* Belt and braces for autofocus: neutralise programmatic focus() on text
     fields while the user has not asked to type. */
  try {
    var proto = window.HTMLElement && HTMLElement.prototype;
    if (proto && proto.focus && !proto.__ovsFocusGuard) {
      var realFocus = proto.focus;
      proto.focus = function () {
        var tag = this.tagName;
        if (!window.__ovs.allowFocus && (tag === 'INPUT' || tag === 'TEXTAREA' || this.isContentEditable)) {
          return;
        }
        return realFocus.apply(this, arguments);
      };
      proto.__ovsFocusGuard = true;
    }
  } catch (_) {}
  try {
    document.addEventListener('focusin', function (e) {
      if (window.__ovs.allowFocus) { return; }
      try { if (e.target && e.target.blur) { e.target.blur(); } } catch (_) {}
    }, true);
    if (document.activeElement && document.activeElement.blur) { document.activeElement.blur(); }
  } catch (_) {}

  window.__ovs.install();
  if (document.addEventListener) {
    /* Single-page navigations wipe the overlay out of the DOM. */
    document.addEventListener('visibilitychange', function () { window.__ovs.install(); }, false);
  }
})();
";
    }
}
