// CorpGateway — Agent Control Overlay
// Injects a visual border + banner when the browser is under agent control

(function () {
  if (window.__cgwOverlayInjected) return;
  window.__cgwOverlayInjected = true;

  const BANNER_HEIGHT = 32;

  // Create host element with Shadow DOM for style isolation
  const host = document.createElement('div');
  host.id = '__cgw-overlay-host';
  host.style.cssText = 'all:initial !important; position:fixed !important; top:0 !important; left:0 !important; width:0 !important; height:0 !important; z-index:2147483647 !important; pointer-events:none !important;';
  const shadow = host.attachShadow({ mode: 'closed' });

  // Styles
  const style = document.createElement('style');
  style.textContent = `
    :host { all: initial !important; }

    .cgw-banner {
      position: fixed;
      top: 0;
      left: 0;
      right: 0;
      height: ${BANNER_HEIGHT}px;
      background: linear-gradient(135deg, #4f46e5, #7c3aed, #6366f1);
      color: #fff;
      font: 600 13px/32px -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
      text-align: center;
      letter-spacing: 0.3px;
      z-index: 2147483647;
      pointer-events: none;
      display: none;
      box-shadow: 0 2px 8px rgba(79, 70, 229, 0.4);
    }

    .cgw-banner.visible {
      display: block;
    }

    .cgw-banner svg {
      vertical-align: middle;
      margin-right: 6px;
      margin-top: -2px;
    }

    .cgw-border {
      position: fixed;
      top: 0;
      left: 0;
      right: 0;
      bottom: 0;
      border: 3px solid #6366f1;
      border-radius: 0;
      pointer-events: none;
      z-index: 2147483646;
      display: none;
      box-shadow: inset 0 0 0 1px rgba(99, 102, 241, 0.3);
    }

    .cgw-border.visible {
      display: block;
    }
  `;

  // Banner
  const banner = document.createElement('div');
  banner.className = 'cgw-banner';
  banner.innerHTML = `
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
      <circle cx="12" cy="12" r="10"/>
      <path d="M8 12h.01M12 12h.01M16 12h.01"/>
    </svg>
    CorpGateway — браузер под управлением агента
  `;

  // Border
  const border = document.createElement('div');
  border.className = 'cgw-border';

  shadow.appendChild(style);
  shadow.appendChild(banner);
  shadow.appendChild(border);

  function show() {
    banner.classList.add('visible');
    border.classList.add('visible');
    document.documentElement.style.setProperty('--cgw-banner-offset', BANNER_HEIGHT + 'px');
  }

  function hide() {
    banner.classList.remove('visible');
    border.classList.remove('visible');
    document.documentElement.style.removeProperty('--cgw-banner-offset');
  }

  // Inject into page
  function inject() {
    if (!document.body) {
      document.addEventListener('DOMContentLoaded', inject, { once: true });
      return;
    }
    document.documentElement.appendChild(host);
  }

  inject();

  // Listen for state changes from background
  chrome.runtime.onMessage.addListener((msg) => {
    if (msg.type === 'cgw-overlay') {
      msg.connected ? show() : hide();
    }
  });

  // Query initial state
  try {
    chrome.runtime.sendMessage({ type: 'getStatus' }, (res) => {
      if (chrome.runtime.lastError) return;
      if (res && res.connected) show();
    });
  } catch {}
})();
