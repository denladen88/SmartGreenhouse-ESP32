import { useNavigation } from '@react-navigation/native';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import React, { useState } from 'react';
import { Alert, Button, StyleSheet, Text, TextInput, View } from 'react-native';
import { useApiClient } from '../api/hooks';
import type { Planting, PlantingRequest } from '../types';

// Локальна календарна дата (не UTC) — toISOString() дав би день по UTC, що
// для зон на схід від Гринвіча ввечері вже "завтра".
const today = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

const DATE_RE = /^\d{4}-\d{2}-\d{2}$/;

// Заміняє ручну правку appsettings.json:Plant + перезапуск Backend — заводить
// нову посадку (POST /api/planting), яка одразу засіює свіжий AI-профіль у
// фоні (Backend/Controllers/PlantingController.cs). Показується автоматично,
// якщо GET /api/planting/current повертає 404 (RootNavigator), або вручну з
// Settings при заміні рослини.
export function OnboardingScreen() {
  const api = useApiClient();
  const navigation = useNavigation();
  const queryClient = useQueryClient();

  const [plantName, setPlantName] = useState('');
  const [soilType, setSoilType] = useState('');
  const [plantedDate, setPlantedDate] = useState(today());
  const [notes, setNotes] = useState('');

  const mutation = useMutation({
    mutationFn: () => {
      const body: PlantingRequest = {
        plantName: plantName.trim(),
        soilType: soilType.trim(),
        // Полудень UTC, а не північ: поле читається як локальна календарна
        // дата, і `${date}T00:00:00Z` для зон на захід від UTC зсувало б
        // посадку на попередній день.
        plantedDateUtc: `${plantedDate}T12:00:00Z`,
        notes: notes.trim(),
      };
      return api.post<Planting>('/api/planting', body);
    },
    onSuccess: (planting) => {
      queryClient.setQueryData(['planting', 'current'], planting);
      // 'Main' — це ім'я екрана в зовнішньому Stack.Navigator (RootNavigator),
      // де насправді сидить OnboardingScreen; 'Dashboard' — лише ім'я таба
      // всередині вкладеного Tab.Navigator і звідси не бачиться напряму.
      navigation.navigate('Main' as never);
    },
    onError: (err: Error) => Alert.alert('Помилка', err.message),
  });

  const submit = () => {
    if (!DATE_RE.test(plantedDate.trim())) {
      Alert.alert('Помилка', 'Дата має бути у форматі РРРР-ММ-ДД');
      return;
    }
    // Захист від тихого накопичення посадок: на цей екран можна потрапити й
    // при вже заведеній посадці (кнопка "Нова посадка" в Налаштуваннях).
    const existing = queryClient.getQueryData<Planting>(['planting', 'current']);
    if (existing) {
      Alert.alert('Посадка вже існує', `Зараз активна: ${existing.plantName}. Замінити її новою?`, [
        { text: 'Скасувати', style: 'cancel' },
        { text: 'Замінити', style: 'destructive', onPress: () => mutation.mutate() },
      ]);
      return;
    }
    mutation.mutate();
  };

  return (
    <View style={styles.container}>
      <Text style={styles.title}>Нова посадка</Text>

      <Text style={styles.label}>Назва/сорт рослини</Text>
      <TextInput
        style={styles.input}
        value={plantName}
        onChangeText={setPlantName}
        placeholder="напр. Базилік Genovese"
      />

      <Text style={styles.label}>Тип ґрунту</Text>
      <TextInput
        style={styles.input}
        value={soilType}
        onChangeText={setSoilType}
        placeholder="напр. універсальний, кокосовий субстрат"
      />

      <Text style={styles.label}>Дата посадки (РРРР-ММ-ДД)</Text>
      <TextInput
        style={styles.input}
        value={plantedDate}
        onChangeText={setPlantedDate}
        placeholder="2026-08-27"
      />

      <Text style={styles.label}>Нотатки догляду (опційно)</Text>
      <TextInput
        style={[styles.input, styles.multiline]}
        value={notes}
        onChangeText={setNotes}
        placeholder="особливості догляду, на що зважати"
        multiline
      />

      <Button
        title={mutation.isPending ? 'Зберігаємо…' : 'Почати посадку'}
        onPress={submit}
        disabled={mutation.isPending || !plantName.trim()}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, padding: 16, gap: 8 },
  title: { fontSize: 20, fontWeight: '600', marginBottom: 8 },
  label: { fontSize: 13, color: '#555', marginTop: 12 },
  input: {
    borderWidth: 1,
    borderColor: '#ccc',
    borderRadius: 8,
    padding: 10,
    fontSize: 16,
  },
  multiline: { minHeight: 80, textAlignVertical: 'top' },
});
