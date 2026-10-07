import { useQuery } from '@tanstack/react-query';
import React, { useState } from 'react';
import { Pressable, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { useApiClient } from '../api/hooks';
import { Sparkline } from '../components/Sparkline';
import type { AiDecisionRecord, AutomationOverview, AutomationRule, PlantProfile, TelemetryRecord } from '../types';

type MetricTone = 'ok' | 'attention' | 'neutral';

interface MetricCardProps {
  label: string; value: number | null; suffix: string; color: string; precision: number;
  history: (number | null)[]; target?: string; tone?: MetricTone; status?: string;
}

function MetricCard({ label, value, suffix, color, precision, history, target, tone = 'neutral', status }: MetricCardProps) {
  return <View style={[styles.metricCard, tone === 'ok' && styles.metricOk, tone === 'attention' && styles.metricAttention]}>
    <View style={styles.metricHead}><Text style={styles.metricLabel}>{label}</Text>
      {status ? <Text style={[styles.statusPill, tone === 'ok' && styles.statusOk, tone === 'attention' && styles.statusAttention]}>{status}</Text> : null}
    </View>
    <Text style={styles.metricValue}>{value === null ? 'Немає даних' : `${value.toFixed(precision)}${suffix}`}</Text>
    <Text style={styles.metricTarget}>{target ? `Ціль AI: ${target}` : ' '}</Text>
    <Sparkline values={history} color={color} />
  </View>;
}

function extractSeries(history: TelemetryRecord[] | null | undefined, selector: (t: TelemetryRecord) => number | null) {
  return (history ?? []).map(selector);
}

function rangeState(value: number | null | undefined, min?: number, max?: number) {
  if (value === null || value === undefined || min === undefined || max === undefined) return { tone: 'neutral' as const, status: 'Без оцінки' };
  if (value < min) return { tone: 'attention' as const, status: 'Нижче цілі' };
  if (value > max) return { tone: 'attention' as const, status: 'Вище цілі' };
  return { tone: 'ok' as const, status: 'У нормі' };
}

function SectionHeader({ kicker, title, note }: { kicker: string; title: string; note?: string }) {
  return <View style={styles.sectionHeader}><Text style={styles.kicker}>{kicker}</Text><Text style={styles.sectionTitle}>{title}</Text>
    {note ? <Text style={styles.sectionNote}>{note}</Text> : null}</View>;
}

function formatRuntime(runtimeMs: number | undefined, measuredAt: string | undefined, nowMs: number) {
  if (!runtimeMs || runtimeMs <= 0) return 'Зараз не працює';

  const sampleTime = measuredAt ? Date.parse(measuredAt) : Number.NaN;
  const sampleAgeMs = Number.isFinite(sampleTime) ? Math.max(0, nowMs - sampleTime) : 0;
  const isFresh = sampleAgeMs <= 5 * 60 * 1000;
  const totalSeconds = Math.floor((runtimeMs + (isFresh ? sampleAgeMs : 0)) / 1000);
  const days = Math.floor(totalSeconds / 86400);
  const hours = Math.floor((totalSeconds % 86400) / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  const parts = days > 0
    ? [`${days} д`, `${hours} год`, `${minutes} хв`]
    : hours > 0
      ? [`${hours} год`, `${minutes} хв`]
      : minutes > 0
        ? [`${minutes} хв`, `${seconds} с`]
        : [`${seconds} с`];
  return `${isFresh ? 'Безперервно' : 'Останнє значення ≈'} ${parts.join(' ')}`;
}

function ActuatorCard({ name, value, active, reason, runtime }: { name: string; value: string; active: boolean; reason: string; runtime: string }) {
  return <View style={[styles.actuatorCard, active && styles.actuatorActive]}>
    <View style={styles.actuatorHead}><View style={[styles.stateDot, active && styles.stateDotActive]} /><Text style={styles.actuatorName}>{name}</Text>
      <Text style={[styles.actuatorValue, active && styles.actuatorValueActive]}>{value}</Text></View>
    <Text style={styles.actuatorRuntime}>{runtime}</Text>
    <Text style={styles.actuatorReason}>{reason}</Text>
  </View>;
}

function RuleCard({ rule }: { rule: AutomationRule }) {
  const [open, setOpen] = useState(false);
  return <View style={styles.expandCard}><Pressable style={styles.expandHeader} onPress={() => setOpen((v) => !v)}>
    <View style={styles.expandTitle}><Text style={styles.ruleTitle}>{rule.actuator}</Text><Text style={styles.rulePurpose}>{rule.purpose}</Text></View>
    <Text style={styles.expandSign}>{open ? '−' : '+'}</Text></Pressable>
    {open ? <View style={styles.ruleBody}>
      <Text style={styles.ruleLabel}>ЗАПУСК</Text><Text style={styles.ruleText}>{rule.trigger}</Text>
      <Text style={styles.ruleLabel}>ЗУПИНКА</Text><Text style={styles.ruleText}>{rule.stop}</Text>
      <Text style={styles.ruleLabel}>ЗАХИСТ</Text><Text style={styles.ruleText}>{rule.safety}</Text>
    </View> : null}
  </View>;
}

function AuditCard({ title, caption, content }: { title: string; caption: string; content: string }) {
  const [open, setOpen] = useState(false);
  return <View style={styles.expandCard}><Pressable style={styles.expandHeader} onPress={() => setOpen((v) => !v)}>
    <View style={styles.expandTitle}><Text style={styles.ruleTitle}>{title}</Text><Text style={styles.rulePurpose}>{caption}</Text></View>
    <Text style={styles.expandSign}>{open ? '−' : '+'}</Text></Pressable>
    {open ? <ScrollView horizontal style={styles.codeScroll}><Text selectable style={styles.code}>{content}</Text></ScrollView> : null}
  </View>;
}

function sourceLabel(source: string | undefined) {
  if (source === 'ManualOverride') return 'Ручне керування';
  if (source === 'StressTest') return 'Тестовий режим';
  return 'Локальна автоматика';
}

export function DashboardScreen() {
  const api = useApiClient();
  const [nowMs, setNowMs] = useState(() => Date.now());
  React.useEffect(() => {
    const timer = setInterval(() => setNowMs(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  const latestQuery = useQuery({ queryKey: ['telemetry', 'latest'], queryFn: () => api.get<TelemetryRecord>('/api/telemetry/latest', { notFoundAsNull: true }), refetchInterval: 60_000 });
  const historyQuery = useQuery({ queryKey: ['telemetry', 'history'], queryFn: () => api.get<TelemetryRecord[]>('/api/telemetry/history?minutes=1440'), refetchInterval: 300_000 });
  const profileQuery = useQuery({ queryKey: ['plantProfile'], queryFn: () => api.get<PlantProfile>('/api/plant-profile', { notFoundAsNull: true }), refetchInterval: 60_000 });
  const decisionQuery = useQuery({ queryKey: ['decisions', 'latest'], queryFn: () => api.get<AiDecisionRecord[]>('/api/decisions/history?count=1').then((r) => r?.[0] ?? null), refetchInterval: 60_000 });
  const automationQuery = useQuery({ queryKey: ['automation', 'overview'], queryFn: () => api.get<AutomationOverview>('/api/automation/overview'), refetchInterval: 60_000 });

  const latest = latestQuery.data ?? null;
  const history = historyQuery.data ?? [];
  const profile = profileQuery.data ?? null;
  const decision = decisionQuery.data ?? null;
  const automation = automationQuery.data ?? null;
  const refresh = () => { latestQuery.refetch(); historyQuery.refetch(); profileQuery.refetch(); decisionQuery.refetch(); automationQuery.refetch(); };

  if ((latestQuery.isError || historyQuery.isError || profileQuery.isError) && !latest) {
    const err = (latestQuery.error || historyQuery.error || profileQuery.error) as Error;
    return <ScrollView style={styles.container} refreshControl={<RefreshControl refreshing={latestQuery.isFetching} onRefresh={refresh} />}>
      <View style={styles.errorPanel}><Text style={styles.errorTitle}>Не вдалося завантажити стан теплиці</Text><Text style={styles.errorText}>{err.message}</Text></View>
    </ScrollView>;
  }

  const tempState = rangeState(latest?.temperatureC, profile?.tempMinC, profile?.tempMaxC);
  const humidityState = rangeState(latest?.humidityPct, profile?.humidityMinPct, profile?.humidityMaxPct);
  const soilState = rangeState(latest?.soilMoisturePct, profile?.soilMoistureMinPct, profile?.soilMoistureMaxPct);
  const soilTempState = rangeState(latest?.soilTempC, profile?.soilTempMinC, profile?.soilTempMaxC);
  const attentionCount = [tempState, humidityState, soilState, soilTempState].filter((s) => s.tone === 'attention').length;
  const fallbackReason = decision?.reason || 'Пояснення зʼявиться після наступного циклу автоматики.';
  const powerValue = (power: number) => power > 0 ? `${power}/255 · ${Math.round(power / 2.55)}%` : 'Вимкнено';
  const latestSampleTime = latest?.timestamp ? Date.parse(latest.timestamp) : Number.NaN;
  const telemetryFresh = Number.isFinite(latestSampleTime) && nowMs - latestSampleTime <= 5 * 60 * 1000;
  const isRunning = (runtimeMs: number | undefined) => telemetryFresh && (runtimeMs ?? 0) > 0;
  const actuators = decision ? [
    { name: 'Насос поливу', value: isRunning(latest?.pumpRuntimeMs) ? 'Увімкнено' : 'Вимкнено', active: isRunning(latest?.pumpRuntimeMs), runtime: formatRuntime(latest?.pumpRuntimeMs, latest?.timestamp, nowMs), reason: decision.pumpReason || fallbackReason },
    { name: 'Фітолампа', value: isRunning(latest?.lightRuntimeMs) ? powerValue(decision.lightBrightness) : 'Вимкнено', active: isRunning(latest?.lightRuntimeMs), runtime: formatRuntime(latest?.lightRuntimeMs, latest?.timestamp, nowMs), reason: decision.lightReason || fallbackReason },
    { name: 'Нагрівач ґрунту', value: isRunning(latest?.soilHeaterRuntimeMs) ? powerValue(decision.soilHeaterPower) : 'Вимкнено', active: isRunning(latest?.soilHeaterRuntimeMs), runtime: formatRuntime(latest?.soilHeaterRuntimeMs, latest?.timestamp, nowMs), reason: decision.soilHeaterReason || fallbackReason },
    { name: 'Нагрівач повітря', value: isRunning(latest?.airHeaterRuntimeMs) ? powerValue(decision.airHeaterPower) : 'Вимкнено', active: isRunning(latest?.airHeaterRuntimeMs), runtime: formatRuntime(latest?.airHeaterRuntimeMs, latest?.timestamp, nowMs), reason: decision.airHeaterReason || fallbackReason },
    { name: 'Витяжка', value: isRunning(latest?.exhaustFanRuntimeMs) ? 'Увімкнено' : 'Вимкнено', active: isRunning(latest?.exhaustFanRuntimeMs), runtime: formatRuntime(latest?.exhaustFanRuntimeMs, latest?.timestamp, nowMs), reason: decision.exhaustFanReason || fallbackReason },
    { name: 'Циркуляція', value: isRunning(latest?.fanRuntimeMs) ? 'Увімкнено' : 'Вимкнено', active: isRunning(latest?.fanRuntimeMs), runtime: formatRuntime(latest?.fanRuntimeMs, latest?.timestamp, nowMs), reason: decision.fanReason || fallbackReason },
  ] : [];

  return <ScrollView style={styles.container} contentContainerStyle={styles.content} refreshControl={<RefreshControl refreshing={latestQuery.isFetching || historyQuery.isFetching} onRefresh={refresh} />}>
    <View style={styles.hero}><Text style={styles.kicker}>SMART GREENHOUSE · LIVE</Text><Text style={styles.heroTitle}>{profile?.plantName || 'Огляд теплиці'}</Text>
      <Text style={styles.heroText}>{profile?.growthStage ? `Етап: ${profile.growthStage}` : 'Моніторинг середовища та автоматики'}</Text>
      <Text style={[styles.healthBadge, attentionCount ? styles.healthAttention : styles.healthOk]}>{attentionCount ? `${attentionCount} показники поза ціллю` : 'Ключові показники в нормі'}</Text>
      <Text style={styles.updatedAt}>Оновлено {latest?.timestamp ? new Date(latest.timestamp).toLocaleString('uk-UA') : '—'}</Text></View>

    <SectionHeader kicker="Середовище" title="Поточні показники" note="Тренд за 24 години" />
    <View style={styles.grid}>
      <MetricCard label="Температура повітря" value={latest?.temperatureC ?? null} suffix="°C" color="#ef6c4d" precision={1} history={extractSeries(history, (t) => t.temperatureC)} target={profile ? `${profile.tempMinC.toFixed(0)}–${profile.tempMaxC.toFixed(0)}°C` : undefined} {...tempState} />
      <MetricCard label="Вологість повітря" value={latest?.humidityPct ?? null} suffix="%" color="#3498db" precision={1} history={extractSeries(history, (t) => t.humidityPct)} target={profile ? `${profile.humidityMinPct.toFixed(0)}–${profile.humidityMaxPct.toFixed(0)}%` : undefined} {...humidityState} />
      <MetricCard label="Вологість ґрунту" value={latest?.soilMoisturePct ?? null} suffix="%" color="#5b9d49" precision={0} history={extractSeries(history, (t) => t.soilMoisturePct)} target={profile ? `${profile.soilMoistureMinPct.toFixed(0)}–${profile.soilMoistureMaxPct.toFixed(0)}%` : undefined} {...soilState} />
      <MetricCard label="Температура ґрунту" value={latest?.soilTempC ?? null} suffix="°C" color="#b96d32" precision={1} history={extractSeries(history, (t) => t.soilTempC)} target={profile ? `${profile.soilTempMinC.toFixed(1)}–${profile.soilTempMaxC.toFixed(1)}°C` : undefined} {...soilTempState} />
      <MetricCard label="Освітленість" value={latest?.lux ?? null} suffix=" lx" color="#e9a820" precision={0} history={extractSeries(history, (t) => t.lux)} target={profile ? `${profile.dailyLightHoursTarget.toFixed(1)} год/добу` : undefined} status="Добова ціль" />
      <MetricCard label="Тиск" value={latest?.pressureHpa ?? null} suffix=" hPa" color="#7786a3" precision={0} history={extractSeries(history, (t) => t.pressureHpa)} status="Довідково" />
    </View>

    <SectionHeader kicker="Рішення автоматики" title="Що працює і чому" note={decision ? `${sourceLabel(decision.source)} · ${new Date(decision.timestamp).toLocaleString('uk-UA')}` : undefined} />
    <View style={styles.infoBanner}><Text style={styles.infoText}><Text style={styles.infoStrong}>Важливо: </Text>AI задає цільові діапазони, а актуатори перемикає локальний контролер за правилами.</Text></View>
    {actuators.map((a) => <ActuatorCard key={a.name} {...a} />)}

    <SectionHeader kicker="Логіка backend" title="Правила спрацювання" note={automation?.evaluation} />
    {automation ? <><Text style={styles.controllerText}>{automation.controller}</Text>{automation.rules.map((rule) => <RuleCard key={rule.id} rule={rule} />)}</> : <Text style={styles.emptyText}>Правила стануть доступні після оновлення backend.</Text>}

    <SectionHeader kicker="AI-агроном" title="Останній аналіз і рішення" note={(profile?.lastAiReviewedUtc || profile?.lastUpdatedUtc) ? `AI-аналіз: ${new Date((profile?.lastAiReviewedUtc || profile?.lastUpdatedUtc)!).toLocaleString('uk-UA')}` : undefined} />
    {profile ? <View style={styles.aiPanel}><Text style={styles.aiTitle}>Цільовий профіль для “{profile.plantName}”</Text><Text style={styles.aiNotes}>{profile.notes}</Text>
      <View style={styles.targets}>
        <Text style={styles.targetItem}>Повітря  {profile.tempMinC.toFixed(0)}–{profile.tempMaxC.toFixed(0)}°C</Text>
        <Text style={styles.targetItem}>Вологість  {profile.humidityMinPct.toFixed(0)}–{profile.humidityMaxPct.toFixed(0)}%</Text>
        <Text style={styles.targetItem}>Ґрунт  {profile.soilMoistureMinPct.toFixed(0)}–{profile.soilMoistureMaxPct.toFixed(0)}%</Text>
        <Text style={styles.targetItem}>Темп. ґрунту  {profile.soilTempMinC.toFixed(1)}–{profile.soilTempMaxC.toFixed(1)}°C</Text>
        <Text style={styles.targetItem}>Світло  {profile.dailyLightHoursTarget.toFixed(1)} год/добу</Text>
      </View>
      <AuditCard title="Prompt, надісланий AI" caption={`Точний текст запиту · ${profile.lastAiHadPhoto ? 'із фото' : 'без фото'}`} content={profile.lastAiPrompt || 'Prompt зʼявиться після наступного AI-аналізу.'} />
      <AuditCard title="Raw-відповідь AI" caption="JSON до перевірки безпечних меж" content={profile.lastAiResponse || 'Відповідь зʼявиться після наступного AI-аналізу.'} />
    </View> : <Text style={styles.emptyText}>AI ще не створив профіль для цієї рослини.</Text>}
  </ScrollView>;
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: '#f4f6f3' }, content: { padding: 12, paddingBottom: 36 },
  hero: { padding: 20, borderRadius: 18, backgroundColor: '#ffffff', borderWidth: 1, borderColor: '#e3e8e1', marginBottom: 28 },
  kicker: { color: '#287c54', fontSize: 10, fontWeight: '800', letterSpacing: 1.2 },
  heroTitle: { fontSize: 28, fontWeight: '800', color: '#1f2924', marginTop: 4 }, heroText: { color: '#64706a', marginTop: 4 },
  healthBadge: { alignSelf: 'flex-start', overflow: 'hidden', borderRadius: 999, paddingHorizontal: 10, paddingVertical: 5, marginTop: 14, fontSize: 12, fontWeight: '700' },
  healthOk: { color: '#26734d', backgroundColor: '#e9f7ef' }, healthAttention: { color: '#a95d00', backgroundColor: '#fff3df' },
  updatedAt: { color: '#7a847f', fontSize: 11, marginTop: 8 },
  sectionHeader: { marginTop: 4, marginBottom: 12 }, sectionTitle: { color: '#1f2924', fontSize: 21, fontWeight: '700', marginTop: 2 }, sectionNote: { color: '#7a847f', fontSize: 11, marginTop: 3 },
  grid: { flexDirection: 'row', flexWrap: 'wrap', gap: 10, justifyContent: 'space-between', marginBottom: 28 },
  metricCard: { width: '48%', minHeight: 146, padding: 13, borderRadius: 14, backgroundColor: '#fff', borderWidth: 1, borderColor: '#e3e8e1', borderTopWidth: 3 },
  metricOk: { borderTopColor: '#26734d' }, metricAttention: { borderTopColor: '#d07a16' }, metricHead: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 4 },
  metricLabel: { color: '#64706a', fontSize: 11, flexShrink: 1 }, statusPill: { color: '#64706a', backgroundColor: '#f1f3f1', borderRadius: 999, paddingHorizontal: 6, paddingVertical: 3, fontSize: 8, fontWeight: '700' },
  statusOk: { color: '#26734d', backgroundColor: '#e9f7ef' }, statusAttention: { color: '#a95d00', backgroundColor: '#fff3df' }, metricValue: { color: '#202823', fontSize: 23, fontWeight: '800', marginTop: 5 }, metricTarget: { minHeight: 20, color: '#7a847f', fontSize: 10, marginBottom: 5 },
  infoBanner: { padding: 12, backgroundColor: '#eaf4fb', borderLeftWidth: 3, borderLeftColor: '#3d9be9', borderRadius: 8, marginBottom: 10 }, infoText: { color: '#596760', fontSize: 12 }, infoStrong: { color: '#25312b', fontWeight: '700' },
  actuatorCard: { padding: 14, backgroundColor: '#fff', borderWidth: 1, borderColor: '#e3e8e1', borderRadius: 13, marginBottom: 9 }, actuatorActive: { borderColor: '#9bc9ae', backgroundColor: '#f3fbf6' },
  actuatorHead: { flexDirection: 'row', alignItems: 'center', gap: 8 }, stateDot: { width: 8, height: 8, borderRadius: 4, backgroundColor: '#9aa39e' }, stateDotActive: { backgroundColor: '#26734d' }, actuatorName: { color: '#25312b', fontWeight: '700', flex: 1 }, actuatorValue: { color: '#7a847f', fontSize: 11, fontWeight: '700' }, actuatorValueActive: { color: '#26734d' }, actuatorRuntime: { color: '#26734d', fontSize: 11, fontWeight: '700', marginTop: 7, marginLeft: 16 }, actuatorReason: { color: '#64706a', fontSize: 12, lineHeight: 17, marginTop: 8, marginLeft: 16 },
  controllerText: { color: '#596760', fontSize: 12, padding: 12, backgroundColor: '#eaf4fb', borderRadius: 9, marginBottom: 9 },
  expandCard: { backgroundColor: '#fff', borderWidth: 1, borderColor: '#e3e8e1', borderRadius: 12, marginBottom: 8, overflow: 'hidden' }, expandHeader: { flexDirection: 'row', alignItems: 'center', padding: 13 }, expandTitle: { flex: 1 }, ruleTitle: { color: '#25312b', fontWeight: '700' }, rulePurpose: { color: '#7a847f', fontSize: 10, marginTop: 2 }, expandSign: { color: '#64706a', fontSize: 22, marginLeft: 8 }, ruleBody: { borderTopWidth: 1, borderTopColor: '#e3e8e1', padding: 13 }, ruleLabel: { color: '#287c54', fontSize: 9, fontWeight: '800', marginTop: 7 }, ruleText: { color: '#64706a', fontSize: 12, lineHeight: 17, marginTop: 2 },
  aiPanel: { padding: 14, backgroundColor: '#fff', borderWidth: 1, borderColor: '#e3e8e1', borderRadius: 15 }, aiTitle: { color: '#25312b', fontSize: 17, fontWeight: '700' }, aiNotes: { color: '#64706a', fontSize: 12, lineHeight: 18, marginTop: 7 }, targets: { flexDirection: 'row', flexWrap: 'wrap', gap: 7, marginVertical: 13 }, targetItem: { color: '#3f4d46', backgroundColor: '#f1f4f1', borderRadius: 8, paddingHorizontal: 9, paddingVertical: 7, fontSize: 11 }, codeScroll: { maxHeight: 330, borderTopWidth: 1, borderTopColor: '#e3e8e1', backgroundColor: '#f7f8f7' }, code: { minWidth: '100%', color: '#334039', fontFamily: 'monospace', fontSize: 10, lineHeight: 15, padding: 12 },
  emptyText: { color: '#7a847f', textAlign: 'center', padding: 20 }, errorPanel: { margin: 12, padding: 18, backgroundColor: '#fff', borderRadius: 14 }, errorTitle: { color: '#a62922', fontWeight: '700' }, errorText: { color: '#64706a', marginTop: 6 },
});
