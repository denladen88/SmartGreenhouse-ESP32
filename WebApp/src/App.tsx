import { createContext, useContext } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Navigate, NavLink, Outlet, Route, Routes } from 'react-router-dom';
import { useApiClient } from './api/hooks';
import { useLiveUpdates, type LiveStatus } from './api/signalr';
import { useConfig } from './ConfigContext';
import { CameraPage } from './pages/CameraPage';
import { ControlsPage } from './pages/ControlsPage';
import { DashboardPage } from './pages/DashboardPage';
import { HistoryPage } from './pages/HistoryPage';
import { OnboardingPage } from './pages/OnboardingPage';
import { ProfileEditPage } from './pages/ProfileEditPage';
import { SettingsPage } from './pages/SettingsPage';
import type { Planting } from './types';

// Статус SignalR-з'єднання прокидаємо в Layout, щоб показати індикатор
// "живі оновлення офлайн" — інакше падіння хабу видно лише в консолі, а
// Dashboard мовчки завмирає зі старими числами.
const LiveStatusContext = createContext<LiveStatus>('connecting');

function Layout() {
  const live = useContext(LiveStatusContext);
  return (
    <div className="app-shell">
      <header className="topbar">
        <span className="brand">🌱 SmartGreenhouse</span>
        <nav>
          <NavLink to="/" end>
            Теплиця
          </NavLink>
          <NavLink to="/camera">Камера</NavLink>
          <NavLink to="/controls">Керування</NavLink>
          <NavLink to="/history">Історія</NavLink>
          <NavLink to="/profile">Профіль</NavLink>
          <NavLink to="/settings">Налаштування</NavLink>
        </nav>
        {live !== 'connected' && (
          <span className="live-offline">
            {live === 'reconnecting' ? 'Відновлення зв’язку…' : 'Живі оновлення офлайн'}
          </span>
        )}
      </header>
      <main>
        <Outlet />
      </main>
    </div>
  );
}

// Аналог гейтингу з ../MobileApp/src/navigation/RootNavigator.tsx: без
// заведеної Planting (404 з /api/planting/current, ApiClient повертає null)
// веде на онбординг замість Dashboard/Controls/History. Важливо відрізняти
// це від isError (неправильний URL/ключ, мережева помилка тощо) — раніше
// обидва випадки трактувались однаково як "нема посадки" й помилково кидали
// на онбординг навіть тоді, коли посадка вже була заведена, просто запит
// падав з іншої причини. Settings навмисно НЕ під цим гейтом (окремий
// маршрут у Layout нижче) — інакше при помилці з'єднання нема як дістатись
// до екрана, де саме цю помилку й можна виправити.
function RequirePlanting() {
  const api = useApiClient();
  const plantingQuery = useQuery({
    queryKey: ['planting', 'current'],
    queryFn: () => api.get<Planting>('/api/planting/current', { notFoundAsNull: true }),
  });

  if (plantingQuery.isLoading) {
    return <div className="page">Завантаження…</div>;
  }
  if (plantingQuery.isError) {
    return (
      <div className="page">
        <p className="error">Не вдалось з'єднатись з Backend: {(plantingQuery.error as Error).message}</p>
        <p className="hint">Перевір адресу й ключ у Налаштуваннях.</p>
        <button className="secondary" onClick={() => plantingQuery.refetch()}>
          Спробувати ще раз
        </button>
      </div>
    );
  }
  if (!plantingQuery.data) {
    return <Navigate to="/onboarding" replace />;
  }
  return <Outlet />;
}

function ConfiguredApp() {
  const live = useLiveUpdates();

  return (
    <LiveStatusContext.Provider value={live}>
      <Routes>
        <Route path="/onboarding" element={<OnboardingPage />} />
        <Route element={<Layout />}>
          <Route path="/settings" element={<SettingsPage embedded />} />
          <Route element={<RequirePlanting />}>
            <Route path="/" element={<DashboardPage />} />
            <Route path="/camera" element={<CameraPage />} />
            <Route path="/controls" element={<ControlsPage />} />
            <Route path="/history" element={<HistoryPage />} />
            <Route path="/profile" element={<ProfileEditPage />} />
          </Route>
        </Route>
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </LiveStatusContext.Provider>
  );
}

export function App() {
  const { isConfigured } = useConfig();

  if (!isConfigured) {
    return <SettingsPage />;
  }

  return <ConfiguredApp />;
}
