const BACKEND_URL_KEY = 'backendUrl';
const API_KEY_KEY = 'apiKey';

export interface BackendConfig {
  backendUrl: string;
  apiKey: string;
}

// Веб-аналог MobileApp/src/config/ConfigContext.tsx — тут завжди
// localStorage (немає потреби в Platform-розгалуженні, як у мобільному).
// Якщо WebApp роздається самим Backend (той самий origin/порт — прод-сценарій
// з wwwroot), backendUrl можна не питати взагалі: він завжди
// window.location.origin. Питаємо лише якщо збережене значення відсутнє.
export function loadConfig(): BackendConfig | null {
  const backendUrl = window.localStorage.getItem(BACKEND_URL_KEY);
  const apiKey = window.localStorage.getItem(API_KEY_KEY);
  return backendUrl && apiKey ? { backendUrl, apiKey } : null;
}

export function saveConfig(config: BackendConfig): void {
  window.localStorage.setItem(BACKEND_URL_KEY, config.backendUrl);
  window.localStorage.setItem(API_KEY_KEY, config.apiKey);
}

export function defaultBackendUrl(): string {
  // import.meta.env.DEV — true під час `npm run dev` (Vite dev-сервер на
  // своєму порту, типово 5173), false у зібраному проді. Раніше тут
  // безумовно був window.location.origin — правильно лише коли WebApp
  // зібрано й роздається самим Backend (той самий origin), і хибно під час
  // розробки: підставляло адресу самого Vite, а не Backend, тож усі запити
  // летіли в SPA-fallback Vite (index.html) замість реального API.
  if (import.meta.env.DEV) {
    return 'http://localhost:5080';
  }
  return window.location.origin;
}
