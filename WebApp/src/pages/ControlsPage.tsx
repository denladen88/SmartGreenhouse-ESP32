import { useMutation, useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useApiClient } from '../api/hooks';
import type { AiCommand, AiDecisionRecord } from '../types';

// Веб-версія ../../MobileApp/src/screens/ControlsScreen.tsx. Окремий
// queryKey ('decisions','latest') від HistoryPage ('decisions','history') —
// інакше два запити з різним count борються за один кеш-запис.
export function ControlsPage() {
  const api = useApiClient();

  const latestDecisionQuery = useQuery({
    queryKey: ['decisions', 'latest'],
    queryFn: () => api.get<AiDecisionRecord[]>('/api/decisions/history?count=1').then((r) => r?.[0] ?? null),
    // Фолбек, коли SignalR-хаб офлайн — інакше показаний тут "поточний стан"
    // застигає на останньому, що встиг долетіти живою подією DecisionReceived.
    refetchInterval: 60 * 1000,
  });
  const latest = latestDecisionQuery.data;

  const [pumpOn, setPumpOn] = useState(false);
  const [fanOn, setFanOn] = useState(false);
  const [lightBrightness, setLightBrightness] = useState(0);
  const [soilHeaterPower, setSoilHeaterPower] = useState(0);
  const [airHeaterPower, setAirHeaterPower] = useState(0);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (latest) {
      setPumpOn(latest.pumpOn);
      setFanOn(latest.fanOn);
      setLightBrightness(latest.lightBrightness);
      setSoilHeaterPower(latest.soilHeaterPower);
      setAirHeaterPower(latest.airHeaterPower);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [latest?.id]);

  const mutation = useMutation({
    mutationFn: (command: AiCommand) => api.post<AiDecisionRecord>('/api/commands', command),
    onError: (err: Error) => setError(err.message),
  });

  const send = () => {
    setError(null);
    mutation.mutate({
      pump_on: pumpOn,
      fan_on: fanOn,
      light_brightness: Math.round(lightBrightness),
      soil_heater_power: Math.round(soilHeaterPower),
      air_heater_power: Math.round(airHeaterPower),
    });
  };

  return (
    <div className="page page-narrow">
      <h1>Керування</h1>
      {error && <p className="error">{error}</p>}

      <div className="control-row">
        <span>Насос</span>
        <input type="checkbox" checked={pumpOn} onChange={(e) => setPumpOn(e.target.checked)} />
      </div>

      <div className="control-row">
        <span>Вентилятор</span>
        <input type="checkbox" checked={fanOn} onChange={(e) => setFanOn(e.target.checked)} />
      </div>

      <div className="slider-block">
        <label>Яскравість світла: {Math.round(lightBrightness)}</label>
        <input
          type="range"
          min={0}
          max={255}
          value={lightBrightness}
          onChange={(e) => setLightBrightness(Number(e.target.value))}
        />
      </div>

      <div className="slider-block">
        <label>Потужність нагрівача ґрунту: {Math.round(soilHeaterPower)}</label>
        <input
          type="range"
          min={0}
          max={255}
          value={soilHeaterPower}
          onChange={(e) => setSoilHeaterPower(Number(e.target.value))}
        />
      </div>

      <div className="slider-block">
        <label>Потужність нагрівача повітря: {Math.round(airHeaterPower)}</label>
        <input
          type="range"
          min={0}
          max={255}
          value={airHeaterPower}
          onChange={(e) => setAirHeaterPower(Number(e.target.value))}
        />
      </div>

      <button onClick={send} disabled={mutation.isPending}>
        {mutation.isPending ? 'Надсилаємо…' : 'Надіслати команду'}
      </button>

      {latest && (
        <p className="hint">Override діє до наступного тіку локального контролера (~10 хв) — далі AI знову вирішує сам.</p>
      )}
    </div>
  );
}
