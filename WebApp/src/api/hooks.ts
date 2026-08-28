import { useMemo } from 'react';
import { useConfig } from '../ConfigContext';
import { ApiClient } from './client';

export function useApiClient(): ApiClient {
  const { config } = useConfig();
  if (!config) {
    throw new Error('useApiClient called before backend config was set');
  }
  return useMemo(() => new ApiClient(config.backendUrl, config.apiKey), [config.backendUrl, config.apiKey]);
}
