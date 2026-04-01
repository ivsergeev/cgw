// CorpGateway — Agent Control Overlay
// Injects a visual border + banner when the browser is under agent control

(function () {
  if (window.__cgwOverlayInjected) return;
  window.__cgwOverlayInjected = true;

  // Only show banner in top frame, but show border in all frames
  const isTopFrame = window === window.top;
  const BANNER_HEIGHT = 36;

  // Create host element with Shadow DOM for style isolation
  const host = document.createElement('div');
  host.id = '__cgw-overlay-host';
  host.style.cssText = 'all:initial !important; position:fixed !important; top:0 !important; left:0 !important; width:0 !important; height:0 !important; z-index:2147483647 !important; pointer-events:none !important;';
  const shadow = host.attachShadow({ mode: 'closed' });

  const style = document.createElement('style');
  style.textContent = `
    :host { all: initial !important; }

    .cgw-banner {
      position: fixed;
      top: 0;
      left: 0;
      right: 0;
      height: ${BANNER_HEIGHT}px;
      background: linear-gradient(90deg, #4f46e5, #7c3aed, #6366f1, #4f46e5);
      background-size: 300% 100%;
      animation: cgw-shimmer 6s linear infinite;
      color: #fff;
      font: 600 13px/${BANNER_HEIGHT}px -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
      text-align: center;
      letter-spacing: 0.5px;
      z-index: 2147483647;
      pointer-events: none;
      display: none;
      box-shadow: 0 2px 12px rgba(79, 70, 229, 0.5);
    }

    .cgw-banner.visible { display: block; }

    .cgw-banner svg {
      vertical-align: middle;
      margin-right: 8px;
      margin-top: -2px;
    }

    @keyframes cgw-shimmer {
      0% { background-position: 0% 50%; }
      100% { background-position: 300% 50%; }
    }

    .cgw-border {
      position: fixed;
      top: 0;
      left: 0;
      width: 100vw;
      height: 100vh;
      pointer-events: none;
      z-index: 2147483646;
      display: none;
      outline: 4px solid #6366f1;
      outline-offset: -4px;
    }

    .cgw-border.visible {
      display: block;
      animation: cgw-glow 3s ease-in-out infinite;
    }

    @keyframes cgw-glow {
      0%, 100% { outline-color: rgba(99, 102, 241, 0.8); }
      50% { outline-color: rgba(99, 102, 241, 1); }
    }
  `;

  shadow.appendChild(style);

  // Banner — only in top frame
  let banner = null;
  if (isTopFrame) {
    banner = document.createElement('div');
    banner.className = 'cgw-banner';
    banner.innerHTML = `
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
        <path d="M12 2a4 4 0 0 1 4 4v2H8V6a4 4 0 0 1 4-4z"/>
        <rect x="3" y="8" width="18" height="12" rx="2"/>
        <circle cx="12" cy="15" r="2"/>
      </svg>
      CorpGateway — браузер под управлением агента
    `;
    shadow.appendChild(banner);
  }

  // Border — all frames
  const border = document.createElement('div');
  border.className = 'cgw-border';
  shadow.appendChild(border);

  function show() {
    if (banner) banner.classList.add('visible');
    border.classList.add('visible');
  }

  function hide() {
    if (banner) banner.classList.remove('visible');
    border.classList.remove('visible');
  }

  // Inject into page
  function inject() {
    if (!document.documentElement) {
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
      if (chrome.runtime.lastError) return; // extension context not ready
      if (res && res.connected) show();
    });
  } catch {} // ignore if extension context invalidated
})();
