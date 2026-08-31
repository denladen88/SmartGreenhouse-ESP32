import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useApiClient } from '../api/hooks';
import type { Planting, PlantingRequest } from '../types';

// Локальна календарна дата (не UTC) — toISOString() дав би день по UTC, що
// для зон на схід від Гринвіча ввечері вже "завтра".
const today = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

// Веб-версія ../../MobileApp/src/screens/OnboardingScreen.tsx — заводить
// нову посадку (POST /api/planting), яка одразу засіює свіжий AI-профіль у
// фоні (Backend/Controllers/PlantingController.cs).
export function OnboardingPage() {
  const api = useApiClient();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const [plantName, setPlantName] = useState('');
  const [soilType, setSoilType] = useState('');
  const [plantedDate, setPlantedDate] = useState(today());
  const [notes, setNotes] = useState('');
  const [error, setError] = useState<string | null>(null);

  const mutation = useMutation({
    mutationFn: () => {
      const body: PlantingRequest = {
        plantName: plantName.trim(),
        soilType: soilType.trim(),
        // Полудень UTC, а не північ: користувач читає значення поля як
        // локальну календарну дату, і `${date}T00:00:00Z` для зон на захід
        // від UTC зсувало б посадку на попередній день.
        plantedDateUtc: `${plantedDate}T12:00:00Z`,
        notes: notes.trim(),
      };
      return api.post<Planting>('/api/planting', body);
    },
    onSuccess: (planting) => {
      queryClient.setQueryData(['planting', 'current'], planting);
      navigate('/');
    },
    onError: (err: Error) => setError(err.message),
  });

  const submit = () => {
    setError(null);
    // Захист від тихого накопичення посадок: на онбординг можна потрапити й
    // при вже заведеній посадці (кнопка "Нова посадка" в Налаштуваннях).
    const existing = queryClient.getQueryData<Planting>(['planting', 'current']);
    if (existing && !window.confirm(`Зараз активна посадка: ${existing.plantName}. Замінити її новою?`)) {
      return;
    }
    mutation.mutate();
  };

  return (
    <div className="page page-narrow">
      <h1>Нова посадка</h1>
      {error && <p className="error">{error}</p>}
      <form
        className="form"
        onSubmit={(e) => {
          e.preventDefault();
          submit();
        }}
      >
        <label>
          Назва/сорт рослини
          <input
            value={plantName}
            onChange={(e) => setPlantName(e.target.value)}
            placeholder="напр. Базилік Genovese"
          />
        </label>

        <label>
          Тип ґрунту
          <input
            value={soilType}
            onChange={(e) => setSoilType(e.target.value)}
            placeholder="напр. універсальний, кокосовий субстрат"
          />
        </label>

        <label>
          Дата посадки
          <input type="date" value={plantedDate} onChange={(e) => setPlantedDate(e.target.value)} />
        </label>

        <label>
          Нотатки догляду (опційно)
          <textarea
            value={notes}
            onChange={(e) => setNotes(e.target.value)}
            placeholder="особливості догляду, на що зважати"
            rows={4}
          />
        </label>

        <button type="submit" disabled={mutation.isPending || !plantName.trim()}>
          {mutation.isPending ? 'Зберігаємо…' : 'Почати посадку'}
        </button>
      </form>
    </div>
  );
}
