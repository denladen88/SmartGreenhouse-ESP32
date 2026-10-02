import { useQuery } from '@tanstack/react-query';
import { useApiClient } from '../api/hooks';
import { Sparkline } from '../components/Sparkline';
import type { AiDecisionRecord, AutomationOverview, PlantProfile, TelemetryRecord } from '../types';

type MetricTone = 'ok' | 'attention' | 'neutral';

interface MetricCardProps {
  label: string;
  value: number | null;
  suffix: string;
  color: string;
  precision: number;
  history: (number | null)[];
  target?: string;
  tone?: MetricTone;
  status?: string;
}

function MetricCard({ label, value, suffix, color, precision, history, target, tone = 'neutral', status }: MetricCardProps) {
  return (
    <article className={`metric-card metric-card-${tone}`}>
      <div className="metric-card-head">
        <span className="metric-label">{label}</span>
        {status && <span className={`status-pill status-${tone}`}>{status}</span>}
      </div>
      <div className="metric-value">{value === null ? 'Немає даних' : `${value.toFixed(precision)}${suffix}`}</div>
      {target && <div className="metric-target">Ціль AI: {target}</div>}
      <div className="metric-chart"><Sparkline values={history} color={color} /></div>
    </article>
  );
}

function extractSeries(history: TelemetryRecord[] | null | undefined, selector: (t: TelemetryRecord) => number | null) {
  return (history ?? []).map(selector);
}

function rangeState(value: number | null | undefined, min?: number, max?: number) {
  if (value === null || value === undefined || min === undefined || max === undefined) {
    return { tone: 'neutral' as const, status: 'Без оцінки' };
  }
  if (value < min) return { tone: 'attention' as const, status: 'Нижче цілі' };
  if (value > max) return { tone: 'attention' as const, status: 'Вище цілі' };
  return { tone: 'ok' as const, status: 'У нормі' };
}

function formatDate(value: string | undefined) {
  return value ? new Date(value).toLocaleString('uk-UA') : '—';
}

function sourceLabel(source: string | undefined) {
  if (source === 'ManualOverride') return 'Ручне керування';
  if (source === 'StressTest') return 'Тестовий режим';
  return 'Локальна автоматика';
}

interface ActuatorCardProps { name: string; value: string; active: boolean; reason: string }

function ActuatorCard({ name, value, active, reason }: ActuatorCardProps) {
  return (
    <article className={`actuator-card ${active ? 'actuator-active' : ''}`}>
      <div className="actuator-head">
        <span className={`state-dot ${active ? 'state-dot-active' : ''}`} />
        <span className="actuator-name">{name}</span>
        <span className={`actuator-value ${active ? 'actuator-value-active' : ''}`}>{value}</span>
      </div>
      <p>{reason}</p>
    </article>
  );
}

export function DashboardPage() {
  const api = useApiClient();
  const latestQuery = useQuery({
    queryKey: ['telemetry', 'latest'],
    queryFn: () => api.get<TelemetryRecord>('/api/telemetry/latest', { notFoundAsNull: true }),
    refetchInterval: 60 * 1000,
  });
  const historyQuery = useQuery({
    queryKey: ['telemetry', 'history'],
    queryFn: () => api.get<TelemetryRecord[]>('/api/telemetry/history?minutes=1440'),
    refetchInterval: 5 * 60 * 1000,
  });
  const profileQuery = useQuery({
    queryKey: ['plantProfile'],
    queryFn: () => api.get<PlantProfile>('/api/plant-profile', { notFoundAsNull: true }),
    refetchInterval: 60 * 1000,
  });
  const decisionQuery = useQuery({
    queryKey: ['decisions', 'latest'],
    queryFn: () => api.get<AiDecisionRecord[]>('/api/decisions/history?count=1').then((r) => r?.[0] ?? null),
    refetchInterval: 60 * 1000,
  });
  const automationQuery = useQuery({
    queryKey: ['automation', 'overview'],
    queryFn: () => api.get<AutomationOverview>('/api/automation/overview'),
    refetchInterval: 60 * 1000,
  });

  const latest = latestQuery.data ?? null;
  const history = historyQuery.data ?? [];
  const profile = profileQuery.data ?? null;
  const decision = decisionQuery.data ?? null;
  const automation = automationQuery.data ?? null;
  const refresh = () => {
    latestQuery.refetch(); historyQuery.refetch(); profileQuery.refetch();
    decisionQuery.refetch(); automationQuery.refetch();
  };

  if ((latestQuery.isError || historyQuery.isError || profileQuery.isError) && !latest) {
    const err = (latestQuery.error || historyQuery.error || profileQuery.error) as Error;
    return <div className="page dashboard-page"><div className="error-panel">
      <strong>Не вдалося завантажити стан теплиці</strong><span>{err.message}</span>
      <button onClick={refresh}>Спробувати ще раз</button>
    </div></div>;
  }

  const tempState = rangeState(latest?.temperatureC, profile?.tempMinC, profile?.tempMaxC);
  const humidityState = rangeState(latest?.humidityPct, profile?.humidityMinPct, profile?.humidityMaxPct);
  const soilState = rangeState(latest?.soilMoisturePct, profile?.soilMoistureMinPct, profile?.soilMoistureMaxPct);
  const soilTempState = rangeState(latest?.soilTempC, profile?.soilTempMinC, profile?.soilTempMaxC);
  const attentionCount = [tempState, humidityState, soilState, soilTempState].filter((s) => s.tone === 'attention').length;
  const fallbackReason = decision?.reason || 'Пояснення зʼявиться після наступного циклу автоматики.';
  const powerValue = (power: number) => power > 0 ? `${power}/255 · ${Math.round(power / 2.55)}%` : 'Вимкнено';
  const actuators = decision ? [
    { name: 'Насос поливу', value: decision.pumpOn ? 'Увімкнено' : 'Вимкнено', active: decision.pumpOn, reason: decision.pumpReason || fallbackReason },
    { name: 'Фітолампа', value: powerValue(decision.lightBrightness), active: decision.lightBrightness > 0, reason: decision.lightReason || fallbackReason },
    { name: 'Нагрівач ґрунту', value: powerValue(decision.soilHeaterPower), active: decision.soilHeaterPower > 0, reason: decision.soilHeaterReason || fallbackReason },
    { name: 'Нагрівач повітря', value: powerValue(decision.airHeaterPower), active: decision.airHeaterPower > 0, reason: decision.airHeaterReason || fallbackReason },
    { name: 'Витяжка', value: decision.exhaustFanOn ? 'Увімкнено' : 'Вимкнено', active: decision.exhaustFanOn, reason: decision.exhaustFanReason || fallbackReason },
    { name: 'Циркуляція', value: decision.fanOn ? 'Увімкнено' : 'Вимкнено', active: decision.fanOn, reason: decision.fanReason || fallbackReason },
  ] : [];

  return (
    <div className="page dashboard-page">
      <header className="dashboard-hero">
        <div><div className="eyebrow">SMART GREENHOUSE · LIVE</div><h1>{profile?.plantName || 'Огляд теплиці'}</h1>
          <p>{profile?.growthStage ? `Етап: ${profile.growthStage}` : 'Моніторинг середовища та автоматичного керування'}</p></div>
        <div className="hero-status">
          <span className={`health-badge ${attentionCount ? 'health-attention' : 'health-ok'}`}>
            {attentionCount ? `${attentionCount} показники поза ціллю` : 'Ключові показники в нормі'}
          </span>
          <span className="hero-time">Оновлено {formatDate(latest?.timestamp)}</span>
        </div>
      </header>

      <section>
        <div className="section-heading"><div><span className="section-kicker">Середовище</span><h2>Поточні показники</h2></div>
          <span className="section-note">Графіки за останні 24 години</span></div>
        <div className="metrics-grid">
          <MetricCard label="Температура повітря" value={latest?.temperatureC ?? null} suffix="°C" color="#ef6c4d" precision={1} history={extractSeries(history, (t) => t.temperatureC)} target={profile ? `${profile.tempMinC.toFixed(0)}–${profile.tempMaxC.toFixed(0)}°C` : undefined} {...tempState} />
          <MetricCard label="Вологість повітря" value={latest?.humidityPct ?? null} suffix="%" color="#3498db" precision={1} history={extractSeries(history, (t) => t.humidityPct)} target={profile ? `${profile.humidityMinPct.toFixed(0)}–${profile.humidityMaxPct.toFixed(0)}%` : undefined} {...humidityState} />
          <MetricCard label="Вологість ґрунту" value={latest?.soilMoisturePct ?? null} suffix="%" color="#5b9d49" precision={0} history={extractSeries(history, (t) => t.soilMoisturePct)} target={profile ? `${profile.soilMoistureMinPct.toFixed(0)}–${profile.soilMoistureMaxPct.toFixed(0)}%` : undefined} {...soilState} />
          <MetricCard label="Температура ґрунту" value={latest?.soilTempC ?? null} suffix="°C" color="#b96d32" precision={1} history={extractSeries(history, (t) => t.soilTempC)} target={profile ? `${profile.soilTempMinC.toFixed(1)}–${profile.soilTempMaxC.toFixed(1)}°C` : undefined} {...soilTempState} />
          <MetricCard label="Освітленість" value={latest?.lux ?? null} suffix=" lx" color="#e9a820" precision={0} history={extractSeries(history, (t) => t.lux)} target={profile ? `${profile.dailyLightHoursTarget.toFixed(1)} год/добу` : undefined} status="Добова ціль" />
          <MetricCard label="Атмосферний тиск" value={latest?.pressureHpa ?? null} suffix=" hPa" color="#7786a3" precision={0} history={extractSeries(history, (t) => t.pressureHpa)} status="Довідково" />
        </div>
      </section>

      <section>
        <div className="section-heading"><div><span className="section-kicker">Рішення автоматики</span><h2>Що працює і чому</h2></div>
          {decision && <div className="decision-meta"><span>{sourceLabel(decision.source)}</span><time>{formatDate(decision.timestamp)}</time></div>}</div>
        <div className="explain-banner"><strong>Важливо:</strong> AI задає цільові діапазони раз на день. Нижче локальний контролер застосовує до них прозорі правила після кожного нового вимірювання.</div>
        {actuators.length ? <div className="actuator-grid">{actuators.map((a) => <ActuatorCard key={a.name} {...a} />)}</div> : <div className="empty-card">Рішення автоматики ще немає.</div>}
      </section>

      <section>
        <div className="section-heading"><div><span className="section-kicker">Логіка backend</span><h2>Правила спрацювання</h2></div><span className="section-note">{automation?.evaluation}</span></div>
        {automation ? <><p className="controller-summary">{automation.controller}</p><div className="rules-grid">
          {automation.rules.map((rule) => <details className="rule-card" key={rule.id}><summary><span>{rule.actuator}</span><small>{rule.purpose}</small></summary>
            <dl><div><dt>Запуск</dt><dd>{rule.trigger}</dd></div><div><dt>Зупинка</dt><dd>{rule.stop}</dd></div><div><dt>Захист</dt><dd>{rule.safety}</dd></div></dl>
          </details>)}
        </div></> : <div className="empty-card">Правила стануть доступні після оновлення backend.</div>}
      </section>

      <section>
        <div className="section-heading"><div><span className="section-kicker">AI-агроном</span><h2>Останній аналіз і рішення</h2></div>
          {profile && <span className="section-note">AI-аналіз: {formatDate(profile.lastAiReviewedUtc || profile.lastUpdatedUtc)}</span>}</div>
        {profile ? <div className="ai-panel">
          <div className="ai-summary"><div><span className="ai-label">Рішення AI</span><h3>Цільовий профіль для “{profile.plantName}”</h3><p>{profile.notes || 'AI не додав текстового пояснення.'}</p></div>
            <div className="target-grid">
              <div><span>Повітря</span><strong>{profile.tempMinC.toFixed(0)}–{profile.tempMaxC.toFixed(0)}°C</strong></div>
              <div><span>Вологість</span><strong>{profile.humidityMinPct.toFixed(0)}–{profile.humidityMaxPct.toFixed(0)}%</strong></div>
              <div><span>Ґрунт</span><strong>{profile.soilMoistureMinPct.toFixed(0)}–{profile.soilMoistureMaxPct.toFixed(0)}%</strong></div>
              <div><span>Темп. ґрунту</span><strong>{profile.soilTempMinC.toFixed(1)}–{profile.soilTempMaxC.toFixed(1)}°C</strong></div>
              <div><span>Світло</span><strong>{profile.dailyLightHoursTarget.toFixed(1)} год/добу</strong></div>
              <div><span>Фото</span><strong>{profile.lastAiHadPhoto ? 'Додано до аналізу' : 'Без фото'}</strong></div>
            </div></div>
          <div className="ai-audit-grid">
            <details className="audit-card"><summary>Prompt, надісланий AI</summary><p>Точний текст запиту без JPEG-даних.</p><pre>{profile.lastAiPrompt || 'Prompt зʼявиться після наступного AI-аналізу.'}</pre></details>
            <details className="audit-card"><summary>Raw-відповідь AI</summary><p>JSON до перевірки й застосування безпечних меж backend.</p><pre>{profile.lastAiResponse || 'Відповідь зʼявиться після наступного AI-аналізу.'}</pre></details>
          </div>
        </div> : <div className="empty-card">AI ще не створив профіль для цієї рослини.</div>}
      </section>

      {(decisionQuery.isError || automationQuery.isError) && <p className="inline-warning">Частину пояснень не вдалося оновити. Основні показники залишаються доступними.</p>}
    </div>
  );
}
