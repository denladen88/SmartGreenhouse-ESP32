import { useNavigation } from '@react-navigation/native';
import { useQueryClient } from '@tanstack/react-query';
import React, { useState } from 'react';
import { Alert, Button, StyleSheet, Text, TextInput, View } from 'react-native';
import { useConfig } from '../config/ConfigContext';

interface SettingsScreenProps {
  // true лише коли екран змонтований усередині основного Tab-навігатора
  // (де в тому ж стеку зареєстрований маршрут "Onboarding") — під час
  // першого запуску (ще нема конфігурації) такого маршруту немає.
  showOnboardingShortcut?: boolean;
  // Показується зверху, коли сюди потрапили через помилку запиту (RootNavigator),
  // а не через звичайну навігацію — щоб було видно, що саме зламалось.
  errorMessage?: string;
}

export function SettingsScreen({ showOnboardingShortcut = false, errorMessage }: SettingsScreenProps) {
  const { config, saveConfig } = useConfig();
  const navigation = useNavigation();
  const queryClient = useQueryClient();
  // Порожнє, а не приклад-IP: інакше тап "Зберегти" без редагування зберіг би
  // чужу адресу. Приклад лишається у placeholder.
  const [backendUrl, setBackendUrl] = useState(config?.backendUrl ?? '');
  const [apiKey, setApiKey] = useState(config?.apiKey ?? '');

  const onSave = async () => {
    if (!backendUrl.trim() || !apiKey.trim()) {
      Alert.alert('Помилка', 'Заповни адресу Backend і API-ключ');
      return;
    }
    // Без кінцевого "/" — усі запити в ApiClient самі додають шлях, що
    // починається з "/api"/"/hubs".
    await saveConfig({ backendUrl: backendUrl.trim().replace(/\/+$/, ''), apiKey: apiKey.trim() });
    // Інакше запит, що впав через стару неправильну адресу/ключ (наприклад
    // 'planting','current'), лишається закешованим з помилкою й не
    // повторюється сам після виправлення — той самий баг, що й у WebApp.
    queryClient.invalidateQueries();
    Alert.alert('Збережено', 'Налаштування Backend оновлено');
  };

  return (
    <View style={styles.container}>
      {errorMessage && <Text style={styles.errorBanner}>Не вдалось з'єднатись з Backend: {errorMessage}</Text>}
      <Text style={styles.label}>Адреса Backend (в локальній Wi-Fi мережі)</Text>
      <TextInput
        style={styles.input}
        value={backendUrl}
        onChangeText={setBackendUrl}
        placeholder="http://192.168.1.50:5080"
        autoCapitalize="none"
        autoCorrect={false}
        keyboardType="url"
      />

      <Text style={styles.label}>X-Api-Key (значення Api:Key з appsettings.json Backend)</Text>
      <TextInput
        style={styles.input}
        value={apiKey}
        onChangeText={setApiKey}
        placeholder="api key"
        autoCapitalize="none"
        autoCorrect={false}
        secureTextEntry
      />

      <Button title="Зберегти" onPress={onSave} />

      {showOnboardingShortcut && (
        <>
          <View style={{ height: 24 }} />
          <Button title="Нова посадка" onPress={() => navigation.navigate('Onboarding' as never)} />
          <View style={{ height: 12 }} />
          <Button title="Профіль рослини" onPress={() => navigation.navigate('ProfileEdit' as never)} />
        </>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, padding: 16, gap: 8 },
  errorBanner: { color: '#900', backgroundColor: '#fee', padding: 10, borderRadius: 8, marginBottom: 8 },
  label: { fontSize: 13, color: '#555', marginTop: 12 },
  input: {
    borderWidth: 1,
    borderColor: '#ccc',
    borderRadius: 8,
    padding: 10,
    fontSize: 16,
  },
});
