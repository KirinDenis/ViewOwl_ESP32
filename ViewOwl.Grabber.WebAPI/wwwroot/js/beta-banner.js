/**
 * beta-banner.js
 * Injects a dismissible "Development Preview" warning banner at the top of any page.
 * Dismissed state is persisted in localStorage so the user only sees it once per browser.
 * Include this script at the bottom of <body> on all public pages.
 */
(function () {
  'use strict';

  const STORAGE_KEY = 'vo-beta-banner-dismissed';

  if (localStorage.getItem(STORAGE_KEY) === '1') return;

  const style = document.createElement('style');
  style.textContent = `
    #vo-beta-banner {
      position: fixed;
      top: 0; left: 0; right: 0;
      z-index: 99999;
      background: #2a1f00;
      border-bottom: 1px solid rgba(255, 200, 0, 0.4);
      color: #ffd54f;
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Arial, sans-serif;
      font-size: 12px;
      line-height: 1.4;
      padding: 7px 48px 7px 16px;
      display: flex;
      align-items: center;
      gap: 12px;
      box-shadow: 0 2px 8px rgba(0,0,0,0.4);
    }
    #vo-beta-banner a {
      color: #ffe082;
      text-decoration: underline;
    }
    #vo-beta-banner a:hover { color: #fff; }
    #vo-beta-banner-close {
      position: absolute;
      right: 12px; top: 50%;
      transform: translateY(-50%);
      background: none;
      border: none;
      color: rgba(255,213,79,0.6);
      font-size: 18px;
      cursor: pointer;
      line-height: 1;
      padding: 2px 6px;
      border-radius: 3px;
      transition: color 0.15s;
    }
    #vo-beta-banner-close:hover { color: #ffd54f; }
    /* Push page content down by the banner height */
    body.vo-has-banner { padding-top: 34px !important; }
  `;
  document.head.appendChild(style);

  const banner = document.createElement('div');
  banner.id = 'vo-beta-banner';
  banner.innerHTML = `
    <span>⚠️</span>
    <span>
      <strong>Development Preview</strong> — This is a beta project.
      Not for production use. Do not store sensitive data.
      &nbsp;·&nbsp;
      <a href="/legal/privacy.html">Privacy</a>
      &nbsp;·&nbsp;
      <a href="/legal/terms.html">Terms</a>
    </span>
    <button id="vo-beta-banner-close" title="Dismiss" aria-label="Dismiss banner">×</button>
  `;
  document.body.prepend(banner);
  document.body.classList.add('vo-has-banner');

  document.getElementById('vo-beta-banner-close').addEventListener('click', function () {
    localStorage.setItem(STORAGE_KEY, '1');
    banner.remove();
    document.body.classList.remove('vo-has-banner');
  });
}());
