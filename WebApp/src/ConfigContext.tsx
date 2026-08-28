import React, { createContext, useContext, useState } from 'react';
import { loadConfig, saveConfig as persistConfig, type BackendConfig } from './config';

interface ConfigContextValue {
  config: BackendConfig | null;
  isConfigured: boolean;
  saveConfig: (config: BackendConfig) => void;
}

const ConfigContext = createContext<ConfigContextValue | undefined>(undefined);

// На відміну від MobileApp/src/config/ConfigContext.tsx тут не потрібен
// стан "loading" — localStorage читається синхронно (немає async SecureStore).
export function ConfigProvider({ children }: { children: React.ReactNode }) {
  const [config, setConfig] = useState<BackendConfig | null>(() => loadConfig());

  const saveConfig = (next: BackendConfig) => {
    persistConfig(next);
    setConfig(next);
  };

  return (
    <ConfigContext.Provider value={{ config, isConfigured: config !== null, saveConfig }}>
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
