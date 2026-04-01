// CorpGateway — Auth header interceptor (MAIN world, document_start)
//
// Injected into origin tabs via registerContentScripts BEFORE page scripts run.
// Patches fetch() and XMLHttpRequest to capture Authorization headers,
// mirroring CDP Network.requestWillBeSent behavior.

(function() {
  if (window.__cgw_intercepted) return;
  window.__cgw_intercepted = true;

  console.log('[CGW] Auth interceptor active on', location.origin);

  // ── Patch fetch ────────────────────────────────────────────

  const origFetch = window.fetch;
  window.fetch = function(input, init) {
    try { captureAuthFromHeaders(init?.headers); } catch {}

    // Also capture from Request object
    if (input instanceof Request) {
      try {
        const auth = input.headers.get('Authorization') || input.headers.get('authorization');
        if (auth) {
          window.__cgw_auth = auth;
          console.log('[CGW] Auth captured from Request object');
        }
      } catch {}
    }

    return origFetch.apply(this, arguments);
  };

  // ── Patch XMLHttpRequest.setRequestHeader ──────────────────

  const origSetRequestHeader = XMLHttpRequest.prototype.setRequestHeader;
  XMLHttpRequest.prototype.setRequestHeader = function(name, value) {
    try {
      if (name && name.toLowerCase() === 'authorization' && value) {
        window.__cgw_auth = value;
        console.log('[CGW] Auth captured from XHR');
      }
    } catch {}
    return origSetRequestHeader.call(this, name, value);
  };

  // ── Helpers ────────────────────────────────────────────────

  function captureAuthFromHeaders(headers) {
    if (!headers) return;
    let auth = null;

    if (headers instanceof Headers) {
      auth = headers.get('Authorization') || headers.get('authorization');
    } else if (Array.isArray(headers)) {
      for (const pair of headers) {
        if (Array.isArray(pair) && pair[0]?.toLowerCase() === 'authorization') {
          auth = pair[1]; break;
        }
      }
    } else if (typeof headers === 'object') {
      for (const key of Object.keys(headers)) {
        if (key.toLowerCase() === 'authorization') {
          auth = headers[key]; break;
        }
      }
    }

    if (auth) {
      window.__cgw_auth = auth;
      console.log('[CGW] Auth captured from fetch headers');
    }
  }
})();
