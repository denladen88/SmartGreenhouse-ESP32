import { useQueryClient } from '@tanstack/react-query';
import * as signalR from '@microsoft/signalr';
import { useEffect, useState } from 'react';
import { useConfig } from '../config/ConfigContext';
import type { AiDecisionRecord, PlantProfile, TelemetryRecord } from '../types';
import { DECISION_HISTORY_COUNT } from './constants';

// Живі оновлення з TelemetryHub (Backend/Hubs/TelemetryHub.cs) замість
// періодичного polling — сервер сам штовхає нову телеметрію/рішення одразу,
// як тільки прийшли з MQTT або їх ухвалив AI/локальний контролер. access_token
// у query-рядку — бо під час WebSocket-хендшейку SignalR не може виставити
// заголовок X-Api-Key, backend-мідлвар це враховує (ApiKeyMiddleware.cs).
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
      .withUrl(`${config.backendUrl}/hubs/live?access_token=${encodeURIComponent(config.apiKey)}`)
      .withAutomaticReconnect()
      .build();

    connection.on('TelemetryReceived', (record: TelemetryRecord) => {
      queryClient.setQueryData(['telemetry', 'latest'], record);
    });

    connection.on('DecisionReceived', (record: AiDecisionRecord) => {
      queryClient.setQueryData(['decisions', 'latest'], record);
      // Дедуп за id: той самий запис теоретично може прийти сюди більше
      // одного разу (перепідключення SignalR, Fast Refresh під час розробки
      // тощо) — без цього FlatList у HistoryScreen отримував дублікати id й
      // React лаявся на "two children with the same key". Обрізаємо до тієї ж
      // довжини, що тягне HistoryScreen (DECISION_HISTORY_COUNT).
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
