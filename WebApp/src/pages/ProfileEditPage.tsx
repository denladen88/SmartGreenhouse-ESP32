import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState, type FormEvent } from 'react';
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
const labelOf = (key: RangeKey) => FIELDS.find((f) => f.key === key)?.label ?? key;

export function ProfileEditPage() {
  const api = useApiClient();
  const queryClient = useQueryClient();

  const profileQuery = useQuery({
    queryKey: ['plantProfile'],
    queryFn: () => api.get<PlantProfile>('/api/plant-profile', { notFoundAsNull: true }),
  });
  const profile = profileQuery.data ?? null;

  const [values, setValues] = useState<Record<RangeKey, string>>(emptyValues);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    if (profile) {
      setValues(Object.fromEntries(FIELDS.map((f) => [f.key, String(profile[f.key])])) as Record<RangeKey, string>);
    }
    // Пересіюємо поля лише коли прийшов інший профіль (id) чи його оновили
    // (lastUpdatedUtc) — не на кожен рендер, щоб не затирати введене.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [profile?.id, profile?.lastUpdatedUtc]);

  const mutation = useMutation({
    mutationFn: (body: Record<RangeKey, number>) => api.put<PlantProfile>('/api/plant-profile', body),
    onSuccess: (updated) => {
      if (updated) queryClient.setQueryData(['plantProfile'], updated);
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    },
    onError: (e: Error) => setError(e.message),
  });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    const nums = {} as Record<RangeKey, number>;
    for (const f of FIELDS) {
      const n = Number(values[f.key]);
      if (values[f.key].trim() === '' || !Number.isFinite(n)) {
        setError(`Некоректне число: ${f.label}`);
        return;
      }
      nums[f.key] = n;
    }
    for (const [min, max] of PAIRS) {
      if (nums[min] > nums[max]) {
        setError(`Мінімум більший за максимум: ${labelOf(min)}`);
        return;
      }
    }
    mutation.mutate(nums);
  };

  if (profileQuery.isLoading) {
    return (
      <div className="page page-narrow">
        <h1>Профіль рослини</h1>
        <p className="hint">Завантаження…</p>
      </div>
    );
  }

  if (!profile) {
    return (
      <div className="page page-narrow">
        <h1>Профіль рослини</h1>
        <p className="hint">
          AI ще не створив профіль для цієї рослини — редагування буде доступне після першого аналізу.
        </p>
      </div>
    );
  }

  return (
    <div className="page page-narrow">
      <h1>Профіль рослини — {profile.plantName}</h1>
      <p className="hint">Ці межі використовує локальний контролер. Наступний аналіз AI може їх переписати.</p>
      {error && <p className="error">{error}</p>}
      <form className="form" onSubmit={submit}>
        {FIELDS.map((f) => (
          <label key={f.key}>
            {f.label}
            <input
              type="number"
              step="0.1"
              value={values[f.key]}
              onChange={(e) => setValues((v) => ({ ...v, [f.key]: e.target.value }))}
            />
          </label>
        ))}
        <button type="submit" disabled={mutation.isPending}>
          {mutation.isPending ? 'Зберігаємо…' : 'Зберегти'}
        </button>
        {saved && <span className="hint">Збережено</span>}
      </form>
    </div>
  );
}
