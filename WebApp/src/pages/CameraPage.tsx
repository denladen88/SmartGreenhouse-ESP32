import { useCallback, useEffect, useRef, useState } from 'react';
import { useConfig } from '../ConfigContext';

// Тягне кадр з Backend/Controllers/CameraController.cs (проксі до ESP32-CAM):
//   200 -> JPEG, 204 -> замало світла (нічний режим), 502 -> ESP недоступний.
// Не через ApiClient/react-query — це бінарні дані, тримаємо object-URL у
// локальному стані й самі його звільняємо.
export function CameraPage() {
  const { config } = useConfig();
  const [uri, setUri] = useState<string | null>(null);
  const [status, setStatus] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [at, setAt] = useState<Date | null>(null);
  const [auto, setAuto] = useState(false);
  const objectUrl = useRef<string | null>(null);

  const load = useCallback(async () => {
    if (!config) return;
    setLoading(true);
    setError(null);
    try {
      const res = await fetch(`${config.backendUrl}/api/camera/snapshot?t=${Date.now()}`, {
        headers: { 'X-Api-Key': config.apiKey },
      });
      if (res.status === 204) {
        setStatus(204);
        setUri(null);
        setAt(new Date());
      } else if (res.ok) {
        const blob = await res.blob();
        if (objectUrl.current) URL.revokeObjectURL(objectUrl.current);
        objectUrl.current = URL.createObjectURL(blob);
        setUri(objectUrl.current);
        setStatus(200);
        setAt(new Date());
      } else {
        setStatus(res.status);
        setError(res.status === 502 ? 'Камера недоступна (ESP не відповідає)' : `Камера недоступна (${res.status})`);
      }
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  }, [config]);

  useEffect(() => {
    load();
  }, [load]);

  useEffect(() => {
    if (!auto) return;
    const id = setInterval(load, 15000);
    return () => clearInterval(id);
  }, [auto, load]);

  useEffect(
    () => () => {
      if (objectUrl.current) URL.revokeObjectURL(objectUrl.current);
    },
    [],
  );

  return (
    <div className="page page-narrow">
      <h1>Камера</h1>
      <div className="camera-toolbar">
        <button onClick={load} disabled={loading}>
          {loading ? 'Оновлення…' : 'Оновити'}
        </button>
        <label className="camera-auto">
          <input type="checkbox" checked={auto} onChange={(e) => setAuto(e.target.checked)} />
          Автооновлення (15с)
        </label>
      </div>

      {error && <p className="error">{error}</p>}
      {status === 204 && !error && (
        <p className="hint">Зараз замало світла — камера не знімає (нічний режим).</p>
      )}
      {uri && <img className="camera-frame" src={uri} alt="Знімок з теплиці" />}
      {at && <p className="hint">Знімок: {at.toLocaleString('uk-UA')}</p>}
    </div>
  );
}
