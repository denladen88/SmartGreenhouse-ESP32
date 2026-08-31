import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import React from 'react';
import { StatusBar } from 'expo-status-bar';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { ConfigProvider } from './src/config/ConfigContext';
import { RootNavigator } from './src/navigation/RootNavigator';

// Спільні дефолти, щоб усі екрани поводились однаково: 1 повтор замість
// стандартних 3 (швидше видно помилку конфігурації), і невеликий staleTime,
// бо живі дані й так патчить SignalR.
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: 1,
      staleTime: 30_000,
      refetchOnWindowFocus: false,
    },
  },
});

export default function App() {
  return (
    <SafeAreaProvider>
      <QueryClientProvider client={queryClient}>
        <ConfigProvider>
          <RootNavigator />
          <StatusBar style="auto" />
        </ConfigProvider>
      </QueryClientProvider>
    </SafeAreaProvider>
  );
}
