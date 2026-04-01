// CorpGateway — Agent Control Overlay
// Injects a visual border + banner when the browser is under agent control
// Runs only in top frame (all_frames: false in manifest)

(function () {
  if (window.__cgwOverlayInjected) return;
  window.__cgwOverlayInjected = true;

  const BANNER_HEIGHT = 32;
  const BORDER_WIDTH = 4;

  // Injected style on <html> for the border glow — no overlay div needed
  const globalStyle = document.createElement('style');
  globalStyle.id = '__cgw-global-style';
  globalStyle.textContent = `
    html.__cgw-active {
      outline: ${BORDER_WIDTH}px solid #6366f1 !important;
      outline-offset: -${BORDER_WIDTH}px !important;
      animation: __cgw-glow 3s ease-in-out infinite !important;
      border-top: ${BANNER_HEIGHT}px solid #4f46e5 !important;
      margin-top: 0 !important;
    }
    @keyframes __cgw-glow {
      0%, 100% { outline-color: rgba(99, 102, 241, 0.7); }
      50% { outline-color: rgba(99, 102, 241, 1); }
    }
  `;

  // Banner via Shadow DOM
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
  `;

  const banner = document.createElement('div');
  banner.className = 'cgw-banner';
  banner.innerHTML = `
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
      <path d="M12 2a4 4 0 0 1 4 4v2H8V6a4 4 0 0 1 4-4z"/>
      <rect x="3" y="8" width="18" height="12" rx="2"/>
      <circle cx="12" cy="15" r="2"/>
    </svg>
    CorpGateway — браузер под управлением агента
  `;

  shadow.appendChild(style);
  shadow.appendChild(banner);

  function show() {
    document.documentElement.classList.add('__cgw-active');
    banner.classList.add('visible');
  }

  function hide() {
    document.documentElement.classList.remove('__cgw-active');
    banner.classList.remove('visible');
  }

  // Inject
  function inject() {
    if (!document.documentElement) {
      document.addEventListener('DOMContentLoaded', inject, { once: true });
      return;
    }
    document.documentElement.appendChild(globalStyle);
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
