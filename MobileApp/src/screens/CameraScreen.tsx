import { useFocusEffect } from '@react-navigation/native';
import React, { useCallback, useEffect, useState } from 'react';
import { ActivityIndicator, Button, Image, ScrollView, StyleSheet, Switch, Text, View } from 'react-native';
import { useConfig } from '../config/ConfigContext';

// Читаємо base64 з Blob — у React Native FileReader.readAsDataURL підтримується.
function blobToDataUri(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = () => reject(new Error('Не вдалось прочитати зображення'));
    reader.readAsDataURL(blob);
  });
}

// Тягне кадр з Backend/Controllers/CameraController.cs (проксі до ESP32-CAM):
//   200 -> JPEG, 204 -> замало світла (нічний режим), 502 -> ESP недоступний.
export function CameraScreen() {
  const { config } = useConfig();
  const [uri, setUri] = useState<string | null>(null);
  const [status, setStatus] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [at, setAt] = useState<Date | null>(null);
  const [auto, setAuto] = useState(false);

  const load = useCallback(async () => {
    if (!config) return;
    setLoading(true);
    setError(null);
    try {
      const res = await fetch(`${config.backendUrl}/api/camera/snapshot?t=${Date.now()}`, {
        headers: { 'X-Api-Key': config.apiKey },
      });
      if (res.status === 204) {
        setStatus(204);
        setUri(null);
        setAt(new Date());
      } else if (res.ok) {
        const blob = await res.blob();
        setUri(await blobToDataUri(blob));
        setStatus(200);
        setAt(new Date());
      } else {
        setStatus(res.status);
        setError(res.status === 502 ? 'Камера недоступна (ESP не відповідає)' : `Камера недоступна (${res.status})`);
      }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  }, [config]);

  useFocusEffect(
    useCallback(() => {
      load();
    }, [load]),
  );

  useEffect(() => {
    if (!auto) return;
    const id = setInterval(load, 15000);
    return () => clearInterval(id);
  }, [auto, load]);

  return (
    <ScrollView style={styles.container}>
      <View style={styles.toolbar}>
        <Button title={loading ? 'Оновлення…' : 'Оновити'} onPress={load} disabled={loading} />
        <View style={styles.auto}>
          <Text style={styles.autoLabel}>Авто (15с)</Text>
          <Switch value={auto} onValueChange={setAuto} />
        </View>
      </View>

      {error && <Text style={styles.error}>{error}</Text>}
      {status === 204 && !error && (
        <Text style={styles.hint}>Зараз замало світла — камера не знімає (нічний режим).</Text>
      )}
      {loading && !uri && <ActivityIndicator style={{ marginTop: 24 }} />}
      {uri && <Image source={{ uri }} style={styles.frame} resizeMode="contain" />}
      {at && <Text style={styles.hint}>Знімок: {at.toLocaleString('uk-UA')}</Text>}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, padding: 16 },
  toolbar: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 },
  auto: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  autoLabel: { fontSize: 13, color: '#666' },
  frame: { width: '100%', aspectRatio: 4 / 3, borderRadius: 12, backgroundColor: '#000', marginTop: 8 },
  error: { color: '#900', backgroundColor: '#fee', padding: 10, borderRadius: 8, marginBottom: 8 },
  hint: { fontSize: 12, color: '#888', textAlign: 'center', marginTop: 8 },
});
