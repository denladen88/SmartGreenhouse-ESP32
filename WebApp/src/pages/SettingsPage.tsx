import { useQueryClient } from '@tanstack/react-query';
import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useConfig } from '../ConfigContext';
import { defaultBackendUrl } from '../config';

interface SettingsPageProps {
  // true лише коли сторінка змонтована всередині основного layout'а (де вже
  // є куди повертатись) — під час первинного налаштування кнопки "Нова
  // посадка"/"Назад" не мають сенсу.
  embedded?: boolean;
}

export function SettingsPage({ embedded = false }: SettingsPageProps) {
  const { config, saveConfig } = useConfig();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [backendUrl, setBackendUrl] = useState(config?.backendUrl ?? defaultBackendUrl());
  const [apiKey, setApiKey] = useState(config?.apiKey ?? '');
  const [saved, setSaved] = useState(false);

  const onSave = (e: React.FormEvent) => {
    e.preventDefault();
    if (!backendUrl.trim() || !apiKey.trim()) {
      return;
    }
    saveConfig({ backendUrl: backendUrl.trim().replace(/\/+$/, ''), apiKey: apiKey.trim() });
    // Без цього старий провалений запит (напр. 'planting','current' з
    // помилкою через попередню неправильну адресу) лишається в кеші й не
    // повторюється сам — користувач бачив би той самий баннер помилки,
    // навіть виправивши URL/ключ, поки вручну не перейде на іншу сторінку
    // й назад чи не перезавантажить сторінку.
    queryClient.invalidateQueries();
    setSaved(true);
    setTimeout(() => setSaved(false), 2000);
  };

  return (
    <div className="page page-narrow">
      <h1>Налаштування Backend</h1>
      <form onSubmit={onSave} className="form">
        <label>
          Адреса Backend
          <input
            type="url"
            value={backendUrl}
            onChange={(e) => setBackendUrl(e.target.value)}
            placeholder="http://192.168.1.50:5080"
            autoComplete="off"
          />
        </label>

        <label>
          X-Api-Key
          <input
            type="password"
            value={apiKey}
            onChange={(e) => setApiKey(e.target.value)}
            placeholder="api key"
            autoComplete="off"
          />
        </label>

        <button type="submit">Зберегти</button>
        {saved && <span className="hint">Збережено</span>}
      </form>

      {embedded && (
        <button className="secondary" onClick={() => navigate('/onboarding')}>
          Нова посадка
        </button>
      )}
    </div>
  );
}
