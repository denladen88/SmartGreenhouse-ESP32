import { useQuery } from '@tanstack/react-query';
import React from 'react';
import { RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { useApiClient } from '../api/hooks';
import { Sparkline } from '../components/Sparkline';
import type { PlantProfile, TelemetryRecord } from '../types';

interface MetricCardProps {
  label: string;
  value: number | null;
  suffix: string;
  color: string;
  precision: number;
  history: (number | null)[];
}

function MetricCard({ label, value, suffix, color, precision, history }: MetricCardProps) {
  return (
    <View style={styles.card}>
      <Text style={styles.cardLabel}>{label}</Text>
      <Text style={styles.cardValue}>{value === null ? 'N/A' : `${value.toFixed(precision)}${suffix}`}</Text>
      <Sparkline values={history} color={color} />
    </View>
  );
}

// null (пропуск сенсора) НЕ відфільтровуємо — Sparkline малює його як розрив,
// інакше вісь часу стискається й тренд виглядає не так, як був насправді.
function extractSeries(
  history: TelemetryRecord[] | null | undefined,
  selector: (t: TelemetryRecord) => number | null,
): (number | null)[] {
  return (history ?? []).map(selector);
}

// Live-оновлення (SignalR) підключаються один раз у RootNavigator і патчать
// той самий react-query кеш ('telemetry','latest') — цей екран сам нічого
// не polling'ить, лише читає кеш. refetchInterval нижче — фолбек на випадок,
// якщо SignalR-хаб недоступний.
export function DashboardScreen() {
  const api = useApiClient();

  const latestQuery = useQuery({
    queryKey: ['telemetry', 'latest'],
    queryFn: () => api.get<TelemetryRecord>('/api/telemetry/latest', { notFoundAsNull: true }),
    refetchInterval: 60 * 1000,
  });

  const historyQuery = useQuery({
    queryKey: ['telemetry', 'history'],
    queryFn: () => api.get<TelemetryRecord[]>('/api/telemetry/history?minutes=1440'),
    refetchInterval: 5 * 60 * 1000,
  });

  const profileQuery = useQuery({
    queryKey: ['plantProfile'],
    queryFn: () => api.get<PlantProfile>('/api/plant-profile', { notFoundAsNull: true }),
    refetchInterval: 60 * 1000,
  });

  const latest = latestQuery.data ?? null;
  const history = historyQuery.data ?? [];
  const profile = profileQuery.data ?? null;

  const refresh = () => {
    latestQuery.refetch();
    historyQuery.refetch();
    profileQuery.refetch();
  };

  const isError = latestQuery.isError || historyQuery.isError || profileQuery.isError;
  if (isError && !latest) {
    const err = (latestQuery.error || historyQuery.error || profileQuery.error) as Error;
    return (
      <ScrollView
        style={styles.container}
        refreshControl={<RefreshControl refreshing={latestQuery.isFetching} onRefresh={refresh} />}
      >
        <Text style={styles.errorBanner}>Не вдалось завантажити дані: {err.message}</Text>
      </ScrollView>
    );
  }

  return (
    <ScrollView
      style={styles.container}
      refreshControl={
        <RefreshControl refreshing={latestQuery.isFetching || historyQuery.isFetching} onRefresh={refresh} />
      }
    >
      {latest && (
        <Text style={styles.updatedAt}>
          Оновлено: {new Date(latest.timestamp).toLocaleString('uk-UA')}
        </Text>
      )}

      <View style={styles.grid}>
        <MetricCard
          label="Температура"
          value={latest?.temperatureC ?? null}
          suffix="°C"
          color="#e07a5f"
          precision={1}
          history={extractSeries(history, (t) => t.temperatureC)}
        />
        <MetricCard
          label="Вологість повітря"
          value={latest?.humidityPct ?? null}
          suffix="%"
          color="#3d9be9"
          precision={1}
          history={extractSeries(history, (t) => t.humidityPct)}
        />
        <MetricCard
          label="Вологість ґрунту"
          value={latest?.soilMoisturePct ?? null}
          suffix="%"
          color="#6a994e"
          precision={0}
          history={extractSeries(history, (t) => t.soilMoisturePct)}
        />
        <MetricCard
          label="Освітленість"
          value={latest?.lux ?? null}
          suffix=" lx"
          color="#f2b134"
          precision={0}
          history={extractSeries(history, (t) => t.lux)}
        />
        <MetricCard
          label="Темп. ґрунту"
          value={latest?.soilTempC ?? null}
          suffix="°C"
          color="#bc6c25"
          precision={1}
          history={extractSeries(history, (t) => t.soilTempC)}
        />
        <MetricCard
          label="Тиск"
          value={latest?.pressureHpa ?? null}
          suffix=" hPa"
          color="#8d99ae"
          precision={0}
          history={extractSeries(history, (t) => t.pressureHpa)}
        />
      </View>

      {profile && (
        <View style={styles.profileBox}>
          <Text style={styles.profileTitle}>{profile.plantName}</Text>
          {profile.growthStage ? (
            <Text style={styles.profileLine}>Етап розвитку: {profile.growthStage}</Text>
          ) : null}
          <Text style={styles.profileLine}>
            Темп: {profile.tempMinC.toFixed(0)}–{profile.tempMaxC.toFixed(0)}°C · Вологість:{' '}
            {profile.humidityMinPct.toFixed(0)}–{profile.humidityMaxPct.toFixed(0)}%
          </Text>
          <Text style={styles.profileLine}>
            Ґрунт: {profile.soilMoistureMinPct.toFixed(0)}–{profile.soilMoistureMaxPct.toFixed(0)}% · Світло:{' '}
            {profile.dailyLightHoursTarget.toFixed(1)}г/добу
          </Text>
          <Text style={styles.profileNotes}>{profile.notes}</Text>
        </View>
      )}
      {!profile && !profileQuery.isLoading && (
        <Text style={styles.noProfile}>
          AI ще не встановив профіль для цієї рослини — з'явиться після першого аналізу.
        </Text>
      )}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, padding: 12 },
  updatedAt: { color: '#888', fontSize: 12, marginBottom: 8, textAlign: 'center' },
  errorBanner: { color: '#900', backgroundColor: '#fee', padding: 10, borderRadius: 8, marginTop: 8 },
  grid: { flexDirection: 'row', flexWrap: 'wrap', gap: 10, justifyContent: 'space-between' },
  card: {
    width: '48%',
    backgroundColor: '#f7f7f7',
    borderRadius: 12,
    padding: 12,
    marginBottom: 10,
  },
  cardLabel: { fontSize: 12, color: '#666' },
  cardValue: { fontSize: 22, fontWeight: '700', marginVertical: 4 },
  profileBox: { marginTop: 8, padding: 14, backgroundColor: '#eef6ee', borderRadius: 12 },
  profileTitle: { fontSize: 16, fontWeight: '600', marginBottom: 4 },
  profileLine: { fontSize: 13, color: '#333', marginBottom: 2 },
  profileNotes: { fontSize: 12, color: '#555', marginTop: 6, fontStyle: 'italic' },
  noProfile: { textAlign: 'center', color: '#888', marginTop: 16 },
});
