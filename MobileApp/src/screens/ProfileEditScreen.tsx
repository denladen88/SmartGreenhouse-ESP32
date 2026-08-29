import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import React, { useEffect, useState } from 'react';
import { Alert, Button, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native';
import { useApiClient } from '../api/hooks';
import type { PlantProfile } from '../types';

// Задіює PUT /api/plant-profile (Backend/Controllers/PlantProfileController.cs),
// який застосовує лише діапазонні поля; id/plantName ігноруються, сервер сам
// ставить lastUpdatedUtc/lastUpdateReason. Наступний аналіз AI може ці межі
// переписати.
type RangeKey =
  | 'tempMinC'
  | 'tempMaxC'
  | 'humidityMinPct'
  | 'humidityMaxPct'
  | 'soilMoistureMinPct'
  | 'soilMoistureMaxPct'
  | 'soilTempMinC'
  | 'soilTempMaxC'
  | 'dailyLightHoursTarget';

const FIELDS: { key: RangeKey; label: string }[] = [
  { key: 'tempMinC', label: 'Температура, мін (°C)' },
  { key: 'tempMaxC', label: 'Температура, макс (°C)' },
  { key: 'humidityMinPct', label: 'Вологість повітря, мін (%)' },
  { key: 'humidityMaxPct', label: 'Вологість повітря, макс (%)' },
  { key: 'soilMoistureMinPct', label: 'Вологість ґрунту, мін (%)' },
  { key: 'soilMoistureMaxPct', label: 'Вологість ґрунту, макс (%)' },
  { key: 'soilTempMinC', label: 'Температура ґрунту, мін (°C)' },
  { key: 'soilTempMaxC', label: 'Температура ґрунту, макс (°C)' },
  { key: 'dailyLightHoursTarget', label: 'Світло, годин/добу' },
];

const PAIRS: [RangeKey, RangeKey][] = [
  ['tempMinC', 'tempMaxC'],
  ['humidityMinPct', 'humidityMaxPct'],
  ['soilMoistureMinPct', 'soilMoistureMaxPct'],
  ['soilTempMinC', 'soilTempMaxC'],
];

const emptyValues = () => Object.fromEntries(FIELDS.map((f) => [f.key, ''])) as Record<RangeKey, string>;

export function ProfileEditScreen() {
  const api = useApiClient();
  const queryClient = useQueryClient();

  const profileQuery = useQuery({
    queryKey: ['plantProfile'],
    queryFn: () => api.get<PlantProfile>('/api/plant-profile', { notFoundAsNull: true }),
  });
  const profile = profileQuery.data ?? null;

  const [values, setValues] = useState<Record<RangeKey, string>>(emptyValues);

  useEffect(() => {
    if (profile) {
      setValues(Object.fromEntries(FIELDS.map((f) => [f.key, String(profile[f.key])])) as Record<RangeKey, string>);
    }
  }, [profile?.id, profile?.lastUpdatedUtc]);

  const mutation = useMutation({
    mutationFn: (body: Record<RangeKey, number>) => api.put<PlantProfile>('/api/plant-profile', body),
    onSuccess: (updated) => {
      if (updated) queryClient.setQueryData(['plantProfile'], updated);
      Alert.alert('Збережено', 'Профіль оновлено');
    },
    onError: (e: Error) => Alert.alert('Помилка', e.message),
  });

  const submit = () => {
    const nums = {} as Record<RangeKey, number>;
    for (const f of FIELDS) {
      const raw = values[f.key].trim();
      const n = Number(raw);
      if (raw === '' || !Number.isFinite(n)) {
        Alert.alert('Помилка', `Некоректне число: ${f.label}`);
        return;
      }
      nums[f.key] = n;
    }
    for (const [min, max] of PAIRS) {
      if (nums[min] > nums[max]) {
        Alert.alert('Помилка', `Мінімум більший за максимум: ${FIELDS.find((f) => f.key === min)?.label}`);
        return;
      }
    }
    mutation.mutate(nums);
  };

  if (profileQuery.isLoading) {
    return (
      <View style={styles.container}>
        <Text style={styles.hint}>Завантаження…</Text>
      </View>
    );
  }

  if (!profile) {
    return (
      <View style={styles.container}>
        <Text style={styles.hint}>
          AI ще не створив профіль для цієї рослини — редагування буде доступне після першого аналізу.
        </Text>
      </View>
    );
  }

  return (
    <ScrollView style={styles.container}>
      <Text style={styles.title}>{profile.plantName}</Text>
      <Text style={styles.hint}>Ці межі використовує локальний контролер. Наступний аналіз AI може їх переписати.</Text>
      {FIELDS.map((f) => (
        <View key={f.key}>
          <Text style={styles.label}>{f.label}</Text>
          <TextInput
            style={styles.input}
            value={values[f.key]}
            onChangeText={(t) => setValues((v) => ({ ...v, [f.key]: t }))}
            keyboardType="numeric"
          />
        </View>
      ))}
      <View style={{ height: 16 }} />
      <Button title={mutation.isPending ? 'Зберігаємо…' : 'Зберегти'} onPress={submit} disabled={mutation.isPending} />
      <View style={{ height: 32 }} />
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, padding: 16 },
  title: { fontSize: 20, fontWeight: '600', marginBottom: 4 },
  label: { fontSize: 13, color: '#555', marginTop: 12 },
  input: { borderWidth: 1, borderColor: '#ccc', borderRadius: 8, padding: 10, fontSize: 16 },
  hint: { fontSize: 12, color: '#888', marginTop: 8 },
});
