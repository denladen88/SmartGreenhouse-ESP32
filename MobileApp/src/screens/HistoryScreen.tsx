import { useQuery } from '@tanstack/react-query';
import React from 'react';
import { Button, FlatList, RefreshControl, StyleSheet, Text, View } from 'react-native';
import { DECISION_HISTORY_COUNT } from '../api/constants';
import { useApiClient } from '../api/hooks';
import type { AiDecisionRecord } from '../types';

function DecisionRow({ item }: { item: AiDecisionRecord }) {
  return (
    <View style={styles.row}>
      <Text style={styles.timestamp}>{new Date(item.timestamp).toLocaleString('uk-UA')}</Text>
      <Text style={styles.state}>
        Насос: {item.pumpOn ? 'Увімк' : 'Вимк'} · Вентилятор: {item.fanOn ? 'Увімк' : 'Вимк'} · Світло:{' '}
        {item.lightBrightness} · Нагрівач ґрунту: {item.soilHeaterPower} · Нагрівач повітря: {item.airHeaterPower}
      </Text>
      <Text style={styles.reason}>{item.reason}</Text>
      {item.photoDescription ? <Text style={styles.photoDescription}>{item.photoDescription}</Text> : null}
    </View>
  );
}

// Стрічка AiDecisionRecord — і від AI (локальний контролер/профільний
// аналіз), і від ручних override з Controls-екрана (Reason="Manual override
// via mobile app") — обидва пишуться в ту саму таблицю на бекенді.
export function HistoryScreen() {
  const api = useApiClient();

  const query = useQuery({
    queryKey: ['decisions', 'history'],
    queryFn: () => api.get<AiDecisionRecord[]>(`/api/decisions/history?count=${DECISION_HISTORY_COUNT}`),
  });

  if (query.isError) {
    return (
      <View style={styles.errorWrap}>
        <Text style={styles.errorBanner}>Не вдалось завантажити історію: {(query.error as Error).message}</Text>
        <Button title="Спробувати ще раз" onPress={() => query.refetch()} />
      </View>
    );
  }

  return (
    <FlatList
      style={styles.container}
      data={query.data ?? []}
      keyExtractor={(item) => item.id}
      renderItem={({ item }) => <DecisionRow item={item} />}
      refreshControl={<RefreshControl refreshing={query.isFetching} onRefresh={() => query.refetch()} />}
      ListEmptyComponent={!query.isLoading ? <Text style={styles.empty}>Історії ще немає</Text> : null}
    />
  );
}

const styles = StyleSheet.create({
  container: { flex: 1 },
  errorWrap: { flex: 1, padding: 16, gap: 12 },
  errorBanner: { color: '#900', backgroundColor: '#fee', padding: 10, borderRadius: 8 },
  row: { padding: 14, borderBottomWidth: 1, borderBottomColor: '#eee' },
  timestamp: { fontSize: 12, color: '#888' },
  state: { fontSize: 14, marginTop: 4 },
  reason: { fontSize: 13, color: '#444', marginTop: 4 },
  photoDescription: { fontSize: 12, color: '#666', marginTop: 4, fontStyle: 'italic' },
  empty: { textAlign: 'center', color: '#888', marginTop: 40 },
});
