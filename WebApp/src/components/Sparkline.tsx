// Веб-версія ../../MobileApp/src/components/Sparkline.tsx — той самий
// принцип (стовпчики без зовнішніх бібліотек), тільки div/CSS замість
// react-native View.
interface SparklineProps {
  values: (number | null)[];
  color: string;
  height?: number;
}

const BUCKET_COUNT = 60;

// За 24 год точок буває кілька сотень-тисяч — згортаємо у BUCKET_COUNT
// усереднених стовпчиків, інакше рендерились би тисячі вузлів на картку.
// null (пропуск сенсора) лишаємо як розрив, а не викидаємо, щоб не стискати
// вісь часу.
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
    return <div style={{ height }} />;
  }

  const min = Math.min(...nums);
  const max = Math.max(...nums);
  const range = max - min || 1;

  return (
    <div style={{ display: 'flex', alignItems: 'flex-end', height, gap: 2 }}>
      {buckets.map((v, i) => (
        <div
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
    </div>
  );
}
