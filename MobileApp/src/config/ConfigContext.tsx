import * as SecureStore from 'expo-secure-store';
import React, { createContext, useContext, useEffect, useState } from 'react';
import { Platform } from 'react-native';

const BACKEND_URL_KEY = 'backendUrl';
const API_KEY_KEY = 'apiKey';

// expo-secure-store не має реальної веб-реалізації (Keychain/Keystore на
// вебі не існує) — на цій платформі викликати getItemAsync/setItemAsync
// впало б з рантайм-помилкою. Для веб-цілі (npx expo start --web) достатньо
// звичайного localStorage.
const storage = Platform.OS === 'web'
  ? {
      getItemAsync: async (key: string) => window.localStorage.getItem(key),
      setItemAsync: async (key: string, value: string) => window.localStorage.setItem(key, value),
    }
  : SecureStore;

interface BackendConfig {
  backendUrl: string;
  apiKey: string;
}

interface ConfigContextValue {
  config: BackendConfig | null;
  loading: boolean;
  isConfigured: boolean;
  saveConfig: (config: BackendConfig) => Promise<void>;
}

const ConfigContext = createContext<ConfigContextValue | undefined>(undefined);

// IP Backend-сервера в локальній мережі може змінюватись (див. пораду про
// статичну DHCP-адресу в плані застосунку) — тому не хардкодимо, а один раз
// просимо ввести на екрані Settings і зберігаємо в SecureStore.
export function ConfigProvider({ children }: { children: React.ReactNode }) {
  const [config, setConfig] = useState<BackendConfig | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    (async () => {
      const [backendUrl, apiKey] = await Promise.all([
        storage.getItemAsync(BACKEND_URL_KEY),
        storage.getItemAsync(API_KEY_KEY),
      ]);
      if (backendUrl && apiKey) {
        setConfig({ backendUrl, apiKey });
      }
      setLoading(false);
    })();
  }, []);

  const saveConfig = async (next: BackendConfig) => {
    await Promise.all([
      storage.setItemAsync(BACKEND_URL_KEY, next.backendUrl),
      storage.setItemAsync(API_KEY_KEY, next.apiKey),
    ]);
    setConfig(next);
  };

  return (
    <ConfigContext.Provider value={{ config, loading, isConfigured: config !== null, saveConfig }}>
      {children}
    </ConfigContext.Provider>
  );
}

export function useConfig(): ConfigContextValue {
  const ctx = useContext(ConfigContext);
  if (!ctx) {
    throw new Error('useConfig must be used within a ConfigProvider');
  }
  return ctx;
}
