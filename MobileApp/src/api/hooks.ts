import { useMemo } from 'react';
import { useConfig } from '../config/ConfigContext';
import { ApiClient } from './client';

export function useApiClient(): ApiClient {
  const { config } = useConfig();
  if (!config) {
    throw new Error('useApiClient called before backend config was set');
  }
  // Мемоізуємо за URL/ключем, а не створюємо новий клієнт на кожен рендер —
  // інакше react-query бачив би "новий" queryFn щоразу.
  return useMemo(() => new ApiClient(config.backendUrl, config.apiKey), [config.backendUrl, config.apiKey]);
}
