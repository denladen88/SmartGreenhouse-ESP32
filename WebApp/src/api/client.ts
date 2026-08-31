// Той самий контракт, що й у ../../MobileApp/src/api/client.ts — тонка
// обгортка над fetch з X-Api-Key.

export interface RequestOptions {
  // 404 -> null замість помилки. Вмикати лише для ендпоінтів, де "даних ще
  // нема" — легітимний стан (нема посадки, AI ще не встановив профіль, ще
  // нема телеметрії). Для решти шляхів 404 означає зламаний/переставлений
  // маршрут і має падати як помилка, а не виглядати як "порожньо".
  notFoundAsNull?: boolean;
}

export class ApiClient {
  private readonly backendUrl: string;
  private readonly apiKey: string;

  constructor(backendUrl: string, apiKey: string) {
    this.backendUrl = backendUrl;
    this.apiKey = apiKey;
  }

  private async request<T>(path: string, init?: RequestInit, opts?: RequestOptions): Promise<T | null> {
    const response = await fetch(`${this.backendUrl}${path}`, {
      ...init,
      headers: {
        'X-Api-Key': this.apiKey,
        // Content-Type лише коли реально шлемо тіло (POST/PUT).
        ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
        ...init?.headers,
      },
    });

    if (response.status === 404) {
      if (opts?.notFoundAsNull) {
        return null;
      }
      console.warn(`${init?.method ?? 'GET'} ${path} -> 404 (не очікувалось; трактуємо як помилку)`);
    }
    if (!response.ok) {
      const body = await response.text().catch(() => '');
      throw new Error(`${init?.method ?? 'GET'} ${path} failed: ${response.status} ${body}`);
    }
    if (response.status === 204) {
      return null;
    }
    return (await response.json()) as T;
  }

  get<T>(path: string, opts?: RequestOptions): Promise<T | null> {
    return this.request<T>(path, undefined, opts);
  }

  post<T>(path: string, body: unknown): Promise<T | null> {
    return this.request<T>(path, { method: 'POST', body: JSON.stringify(body) });
  }

  put<T>(path: string, body: unknown): Promise<T | null> {
    return this.request<T>(path, { method: 'PUT', body: JSON.stringify(body) });
  }
}
