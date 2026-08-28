import { useQuery } from '@tanstack/react-query';
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
    <div className="card">
      <div className="card-label">{label}</div>
      <div className="card-value">{value === null ? 'N/A' : `${value.toFixed(precision)}${suffix}`}</div>
      <Sparkline values={history} color={color} />
    </div>
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

// Веб-версія ../../MobileApp/src/screens/DashboardScreen.tsx — живі
// оновлення підключаються один раз у App.tsx (useLiveUpdates) і патчать той
// самий react-query кеш ('telemetry','latest'). refetchInterval нижче —
// фолбек на випадок, якщо SignalR-хаб недоступний.
export function DashboardPage() {
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

  const isError = latestQuery.isError || historyQuery.isError || profileQuery.isError;
  if (isError && !latest) {
    const err = (latestQuery.error || historyQuery.error || profileQuery.error) as Error;
    return (
      <div className="page">
        <p className="error">Не вдалось завантажити дані: {err.message}</p>
        <button
          className="secondary"
          onClick={() => {
            latestQuery.refetch();
            historyQuery.refetch();
            profileQuery.refetch();
          }}
        >
          Спробувати ще раз
        </button>
      </div>
    );
  }

  return (
    <div className="page">
      {latest && <p className="updated-at">Оновлено: {new Date(latest.timestamp).toLocaleString('uk-UA')}</p>}

      <div className="grid">
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
      </div>

      {profile && (
        <div className="profile-box">
          <div className="profile-title">{profile.plantName}</div>
          <div className="profile-line">
            Темп: {profile.tempMinC.toFixed(0)}–{profile.tempMaxC.toFixed(0)}°C · Вологість:{' '}
            {profile.humidityMinPct.toFixed(0)}–{profile.humidityMaxPct.toFixed(0)}%
          </div>
          <div className="profile-line">
            Ґрунт: {profile.soilMoistureMinPct.toFixed(0)}–{profile.soilMoistureMaxPct.toFixed(0)}% · Світло:{' '}
            {profile.dailyLightHoursTarget.toFixed(1)}г/добу
          </div>
          <div className="profile-notes">{profile.notes}</div>
        </div>
      )}
      {!profile && !profileQuery.isLoading && (
        <p className="no-profile">AI ще не встановив профіль для цієї рослини — з'явиться після першого аналізу.</p>
      )}
    </div>
  );
}
