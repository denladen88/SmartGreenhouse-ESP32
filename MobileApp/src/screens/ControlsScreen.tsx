import Slider from '@react-native-community/slider';
import { useMutation, useQuery } from '@tanstack/react-query';
import React, { useEffect, useState } from 'react';
import { Alert, Button, StyleSheet, Switch, Text, View } from 'react-native';
import { useApiClient } from '../api/hooks';
import type { AiCommand, AiDecisionRecord } from '../types';

// Ручний override: POST /api/commands публікує ту саму AiCommand, що й
// AiAgronomistService, і логується як AiDecisionRecord з
// Reason="Manual override via mobile app" — наступний тік локального
// контролера (типово 10 хв) природно перепише це своїм рішенням, тож
// перемикачі тут не "тримають" стан назавжди.
export function ControlsScreen() {
  const api = useApiClient();

  // Цей екран читає лише останнє рішення (count=1) під власним ключем
  // ['decisions','latest'] — окремим від HistoryScreen (['decisions','history'],
  // count=DECISION_HISTORY_COUNT), інакше два запити з різним count боролись би
  // за один кеш-запис. useLiveUpdates патчить ['decisions','latest'] на кожен
  // "DecisionReceived", тож після відправки команди нічого руками в кеш
  // дописувати не треба.
  const latestDecisionQuery = useQuery({
    queryKey: ['decisions', 'latest'],
    queryFn: () => api.get<AiDecisionRecord[]>('/api/decisions/history?count=1').then((r) => r?.[0] ?? null),
  });
  const latest = latestDecisionQuery.data;

  const [pumpOn, setPumpOn] = useState(false);
  const [fanOn, setFanOn] = useState(false);
  const [lightBrightness, setLightBrightness] = useState(0);
  const [soilHeaterPower, setSoilHeaterPower] = useState(0);

  // Синхронізуємо локальний стан елементів керування з останнім відомим
  // рішенням (від AI чи попереднього override) лише один раз, коли воно
  // прийшло — далі користувач керує самостійно, доки не надішле нову команду.
  useEffect(() => {
    if (latest) {
      setPumpOn(latest.pumpOn);
      setFanOn(latest.fanOn);
      setLightBrightness(latest.lightBrightness);
      setSoilHeaterPower(latest.soilHeaterPower);
    }
  }, [latest?.id]);

  const mutation = useMutation({
    mutationFn: (command: AiCommand) => api.post<AiDecisionRecord>('/api/commands', command),
    onError: (err: Error) => Alert.alert('Помилка', err.message),
  });

  const send = () => {
    mutation.mutate({
      pump_on: pumpOn,
      fan_on: fanOn,
      light_brightness: Math.round(lightBrightness),
      soil_heater_power: Math.round(soilHeaterPower),
    });
  };

  return (
    <View style={styles.container}>
      <View style={styles.row}>
        <Text style={styles.label}>Насос</Text>
        <Switch value={pumpOn} onValueChange={setPumpOn} />
      </View>

      <View style={styles.row}>
        <Text style={styles.label}>Вентилятор</Text>
        <Switch value={fanOn} onValueChange={setFanOn} />
      </View>

      <View style={styles.sliderBlock}>
        <Text style={styles.label}>Яскравість світла: {Math.round(lightBrightness)}</Text>
        <Slider
          minimumValue={0}
          maximumValue={255}
          step={1}
          value={lightBrightness}
          onValueChange={setLightBrightness}
        />
      </View>

      <View style={styles.sliderBlock}>
        <Text style={styles.label}>Потужність нагрівача ґрунту: {Math.round(soilHeaterPower)}</Text>
        <Slider
          minimumValue={0}
          maximumValue={255}
          step={1}
          value={soilHeaterPower}
          onValueChange={setSoilHeaterPower}
        />
      </View>

      <Button title={mutation.isPending ? 'Надсилаємо…' : 'Надіслати команду'} onPress={send} disabled={mutation.isPending} />

      {latest && (
        <Text style={styles.hint}>
          Override діє до наступного тіку локального контролера (~10 хв) — далі AI знову вирішує сам.
        </Text>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, padding: 16, gap: 16 },
  row: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center' },
  label: { fontSize: 16 },
  sliderBlock: { gap: 4 },
  hint: { fontSize: 12, color: '#888', textAlign: 'center', marginTop: 8 },
});
