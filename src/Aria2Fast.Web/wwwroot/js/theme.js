(() => {
  let theme = 'auto';
  try { theme = localStorage.getItem('aria2fast.theme') || 'auto'; } catch {}
  const media = matchMedia('(prefers-color-scheme: dark)');
  const apply = () => {
    document.documentElement.dataset.theme = theme === 'auto' ? (media.matches ? 'dark' : 'light') : theme;
    document.documentElement.style.colorScheme = document.documentElement.dataset.theme;
    document.dispatchEvent(new CustomEvent('aria2fast-theme', { detail: document.documentElement.dataset.theme }));
  };
  window.setTheme = value => { theme = ['auto', 'light', 'dark'].includes(value) ? value : 'auto'; try { localStorage.setItem('aria2fast.theme', theme); } catch {} apply(); };
  window.getTheme = () => theme;
  media.addEventListener('change', apply);
  apply();
})();
