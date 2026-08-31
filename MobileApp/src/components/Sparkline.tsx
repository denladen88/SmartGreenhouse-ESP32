import React from 'react';
import { View } from 'react-native';

interface SparklineProps {
  values: (number | null)[];
  color: string;
  height?: number;
}

const BUCKET_COUNT = 60;

// Мінімальний тренд-графік без зовнішніх бібліотек (react-native-svg/чарти
// ризикували несумісністю з Expo SDK 54 + New Architecture на момент
// написання) — просто стовпчики висотою пропорційною значенню в межах min/max.
// За 24 год точок буває кілька сотень-тисяч, тож згортаємо їх у BUCKET_COUNT
// усереднених стовпчиків; null (пропуск сенсора) лишаємо як розрив, а не
// викидаємо, щоб не стискати вісь часу.
function bucketize(values: (number | null)[]): (number | null)[] {
  if (values.length <= BUCKET_COUNT) {
    return values;
  }
  const size = Math.ceil(values.length / BUCKET_COUNT);
  const out: (number | null)[] = [];
  for (let i = 0; i < values.length; i += size) {
    const slice = values.slice(i, i + size).filter((v): v is number => v !== null);
    out.push(slice.length ? slice.reduce((a, b) => a + b, 0) / slice.length : null);
  }
  return out;
}

export function Sparkline({ values, color, height = 40 }: SparklineProps) {
  const buckets = bucketize(values);
  const nums = buckets.filter((v): v is number => v !== null);
  if (nums.length === 0) {
    return <View style={{ height }} />;
  }

  const min = Math.min(...nums);
  const max = Math.max(...nums);
  const range = max - min || 1;

  return (
    <View style={{ flexDirection: 'row', alignItems: 'flex-end', height, gap: 2 }}>
      {buckets.map((v, i) => (
        <View
          key={i}
          style={{
            flex: 1,
            height: v === null ? 2 : Math.max(2, ((v - min) / range) * height),
            backgroundColor: color,
            opacity: v === null ? 0.15 : 1,
            borderRadius: 1,
          }}
        />
      ))}
    </View>
  );
}
