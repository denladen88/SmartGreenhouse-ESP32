import { useQuery } from '@tanstack/react-query';
import { DECISION_HISTORY_COUNT } from '../api/constants';
import { useApiClient } from '../api/hooks';
import type { AiDecisionRecord } from '../types';

function DecisionRow({ item }: { item: AiDecisionRecord }) {
  return (
    <div className="history-row">
      <div className="timestamp">{new Date(item.timestamp).toLocaleString('uk-UA')}</div>
      <div className="state">
        Насос: {item.pumpOn ? 'Увімк' : 'Вимк'} · Вентилятор: {item.fanOn ? 'Увімк' : 'Вимк'} · Світло:{' '}
        {item.lightBrightness} · Нагрівач ґрунту: {item.soilHeaterPower} · Нагрівач повітря: {item.airHeaterPower}
      </div>
      <div className="reason">{item.reason}</div>
      {item.photoDescription && <div className="photo-description">{item.photoDescription}</div>}
    </div>
  );
}

// Веб-версія ../../MobileApp/src/screens/HistoryScreen.tsx.
export function HistoryPage() {
  const api = useApiClient();

  const query = useQuery({
    queryKey: ['decisions', 'history'],
    queryFn: () => api.get<AiDecisionRecord[]>(`/api/decisions/history?count=${DECISION_HISTORY_COUNT}`),
    // Live-пуш DecisionReceived (api/signalr.ts) дописує нові рішення в цей
    // самий кеш одразу. Але коли хаб офлайн, без цього фолбеку команди
    // локального контролера (кожні ~10 хв) і ручні override не з'являлись би
    // до перезавантаження сторінки — той самий підхід, що й на Dashboard.
    refetchInterval: 60 * 1000,
  });

  const decisions = query.data ?? [];

  return (
    <div className="page page-narrow">
      <div className="page-header">
        <h1>Історія</h1>
        <button className="secondary" onClick={() => query.refetch()} disabled={query.isFetching}>
          {query.isFetching ? 'Оновлення…' : 'Оновити'}
        </button>
      </div>
      {query.isError && (
        <>
          <p className="error">Не вдалось завантажити історію: {(query.error as Error).message}</p>
          <button className="secondary" onClick={() => query.refetch()}>
            Спробувати ще раз
          </button>
        </>
      )}
      {!query.isError && decisions.length === 0 && !query.isLoading && <p className="hint">Історії ще немає</p>}
      {decisions.map((item) => (
        <DecisionRow key={item.id} item={item} />
      ))}
    </div>
  );
}
