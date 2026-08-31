import * as signalR from '@microsoft/signalr';
import { useEffect, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useConfig } from '../ConfigContext';
import type { AiDecisionRecord, PlantProfile, TelemetryRecord } from '../types';
import { DECISION_HISTORY_COUNT } from './constants';

// Той самий підхід, що й у ../../MobileApp/src/api/signalr.ts: живі
// оновлення з TelemetryHub замість polling, access_token у query-рядку (бо
// SignalR WebSocket-хендшейк не може виставити заголовок X-Api-Key).
export type LiveStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

export function useLiveUpdates(): LiveStatus {
  const { config } = useConfig();
  const queryClient = useQueryClient();
  const [status, setStatus] = useState<LiveStatus>('connecting');

  useEffect(() => {
    if (!config) {
      setStatus('disconnected');
      return;
    }
    setStatus('connecting');

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${config.backendUrl}/hubs/live?access_token=${encodeURIComponent(config.apiKey)}`, {
        // За замовчуванням @microsoft/signalr шле негоціацію з
        // credentials: 'include' (кукі браузера) — а специфікація CORS
        // забороняє Access-Control-Allow-Origin: '*' разом із credentials
        // include, тому Backend.AddCors(AllowAnyOrigin) валив негоціацію в
        // браузері (curl/Node цю перевірку не роблять, тож там усе виглядало
        // справним). Кукі тут і не потрібні — авторизація йде через
        // access_token у query, не через кукі.
        withCredentials: false,
      })
      .withAutomaticReconnect()
      .build();

    connection.on('TelemetryReceived', (record: TelemetryRecord) => {
      queryClient.setQueryData(['telemetry', 'latest'], record);
    });

    connection.on('DecisionReceived', (record: AiDecisionRecord) => {
      queryClient.setQueryData(['decisions', 'latest'], record);
      // Дедуп за id — той самий фікс, що й у мобільному застосунку
      // (MobileApp/src/api/signalr.ts): без нього дублікати ламали
      // key-based рендер списку історії. Обрізаємо до тієї ж довжини, що
      // тягне HistoryPage (DECISION_HISTORY_COUNT).
      queryClient.setQueryData<AiDecisionRecord[]>(['decisions', 'history'], (old) => {
        if (old?.some((d) => d.id === record.id)) {
          return old;
        }
        return [record, ...(old ?? [])].slice(0, DECISION_HISTORY_COUNT);
      });
    });

    connection.on('PlantProfileReceived', (profile: PlantProfile) => {
      queryClient.setQueryData(['plantProfile'], profile);
    });

    connection.onreconnecting(() => setStatus('reconnecting'));
    connection.onreconnected(() => setStatus('connected'));
    connection.onclose(() => setStatus('disconnected'));

    connection
      .start()
      .then(() => setStatus('connected'))
      .catch((err) => {
        console.warn('SignalR connection failed', err);
        setStatus('disconnected');
      });

    return () => {
      connection.stop();
    };
  }, [config, queryClient]);

  return status;
}
