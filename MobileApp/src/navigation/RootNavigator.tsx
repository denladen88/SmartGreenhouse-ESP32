import { NavigationContainer } from '@react-navigation/native';
import { createBottomTabNavigator } from '@react-navigation/bottom-tabs';
import { createNativeStackNavigator } from '@react-navigation/native-stack';
import { useQuery } from '@tanstack/react-query';
import React from 'react';
import { ActivityIndicator, StyleSheet, Text, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { useApiClient } from '../api/hooks';
import { useLiveUpdates } from '../api/signalr';
import { useConfig } from '../config/ConfigContext';
import { CameraScreen } from '../screens/CameraScreen';
import { ControlsScreen } from '../screens/ControlsScreen';
import { DashboardScreen } from '../screens/DashboardScreen';
import { HistoryScreen } from '../screens/HistoryScreen';
import { OnboardingScreen } from '../screens/OnboardingScreen';
import { ProfileEditScreen } from '../screens/ProfileEditScreen';
import { SettingsScreen } from '../screens/SettingsScreen';
import type { Planting } from '../types';

const Stack = createNativeStackNavigator();
const Tab = createBottomTabNavigator();

function Loading() {
  return (
    <View style={{ flex: 1, alignItems: 'center', justifyContent: 'center' }}>
      <ActivityIndicator />
    </View>
  );
}

function MainTabs() {
  return (
    <Tab.Navigator>
      <Tab.Screen name="Dashboard" component={DashboardScreen} options={{ title: 'Теплиця' }} />
      <Tab.Screen name="Camera" component={CameraScreen} options={{ title: 'Камера' }} />
      <Tab.Screen name="Controls" component={ControlsScreen} options={{ title: 'Керування' }} />
      <Tab.Screen name="History" component={HistoryScreen} options={{ title: 'Історія' }} />
      <Tab.Screen name="SettingsTab" options={{ title: 'Налаштування' }}>
        {() => <SettingsScreen showOnboardingShortcut />}
      </Tab.Screen>
    </Tab.Navigator>
  );
}

// Живі оновлення (SignalR) і перевірка "чи заведено посадку" мають сенс лише
// після того, як відомі backendUrl/apiKey — тому окремий компонент, змонтований
// виключно коли ConfigContext вже має конфігурацію.
function ConfiguredApp() {
  const live = useLiveUpdates();
  const insets = useSafeAreaInsets();
  const api = useApiClient();

  const plantingQuery = useQuery({
    queryKey: ['planting', 'current'],
    queryFn: () => api.get<Planting>('/api/planting/current', { notFoundAsNull: true }),
  });

  if (plantingQuery.isLoading) {
    return <Loading />;
  }

  // isError (неправильні URL/ключ, мережева помилка) — НЕ те саме, що "нема
  // посадки" (404 -> ApiClient повертає null, не кидає). Раніше обидва
  // випадки трактувались однаково через `plantingQuery.data ? 'Main' :
  // 'Onboarding'` і при помилці з'єднання застосунок мовчки показував
  // Onboarding без жодного способу дістатись до Settings і виправити
  // адресу/ключ. Власний Stack.Navigator тут — той самий патерн, що й у
  // "не налаштовано" гілці нижче.
  if (plantingQuery.isError) {
    return (
      <Stack.Navigator>
        <Stack.Screen name="Setup" options={{ title: 'Налаштування Backend' }}>
          {() => <SettingsScreen errorMessage={(plantingQuery.error as Error).message} />}
        </Stack.Screen>
      </Stack.Navigator>
    );
  }

  return (
    <View style={{ flex: 1 }}>
      {live !== 'connected' && (
        <View style={[styles.offlineBanner, { paddingTop: insets.top + 6 }]}>
          <Text style={styles.offlineText}>
            {live === 'reconnecting' ? 'Відновлення зв’язку…' : 'Живі оновлення офлайн'}
          </Text>
        </View>
      )}
      <View style={{ flex: 1 }}>
        <Stack.Navigator initialRouteName={plantingQuery.data ? 'Main' : 'Onboarding'}>
          <Stack.Screen name="Main" component={MainTabs} options={{ headerShown: false }} />
          <Stack.Screen name="Onboarding" component={OnboardingScreen} options={{ title: 'Нова посадка' }} />
          <Stack.Screen name="ProfileEdit" component={ProfileEditScreen} options={{ title: 'Профіль рослини' }} />
        </Stack.Navigator>
      </View>
    </View>
  );
}

export function RootNavigator() {
  const { loading, isConfigured } = useConfig();

  if (loading) {
    return <Loading />;
  }

  return (
    <NavigationContainer>
      {isConfigured ? (
        <ConfiguredApp />
      ) : (
        <Stack.Navigator>
          <Stack.Screen name="Setup" component={SettingsScreen} options={{ title: 'Налаштування Backend' }} />
        </Stack.Navigator>
      )}
    </NavigationContainer>
  );
}

const styles = StyleSheet.create({
  offlineBanner: { backgroundColor: '#b3261e', paddingHorizontal: 12, paddingBottom: 6 },
  offlineText: { color: '#fff', fontSize: 12, textAlign: 'center' },
});
